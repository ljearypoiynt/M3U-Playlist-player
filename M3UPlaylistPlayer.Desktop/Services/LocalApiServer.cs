using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using M3UPlaylistPlayer.Desktop.Models;
using QRCoder;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class LocalApiServer
{
    private readonly XtreamClient _client;
    private readonly HlsStreamService _hlsStreamService = new();
    private readonly SessionManager _sessionManager = new();
    private readonly SetupLinkManager _setupLinkManager = new();
    private readonly CuratedListStore _curatedListStore = new();
    private readonly Dictionary<MediaKind, IReadOnlyList<MediaItem>> _cache = [];
    private readonly Dictionary<MediaKind, Task<IReadOnlyList<MediaItem>>> _loads = [];
    private readonly object _cacheLock = new();
    private readonly object _remoteLock = new();
    private long _remoteCommandSequence;
    private RemoteCommand? _lastRemoteCommand;
    private WebApplication? _app;

    public LocalApiServer(XtreamClient client, string? url = null)
    {
        _client = client;
        if (!string.IsNullOrWhiteSpace(url))
        {
            Url = url;
        }
    }

    public string Url { get; private set; } = "http://0.0.0.0:5055";

    public string DisplayUrl => BuildUrl(GetPreferredLocalAddress(), GetPortFromUrl());

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader());
        });

        _app = builder.Build();
        _app.UseForwardedHeaders();
        _app.Use(async (context, next) =>
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "*";
            context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            context.Response.Headers.CacheControl = "no-store";

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next();
        });
        _app.UseCors();

        _app.MapGet("/", () => Results.Json(new
        {
            name = "M3U Playlist Player Local API",
            status = "running",
            endpoints = new[]
            {
                "/api/status",
                "/api/qr.svg?value=...",
                "/api/setup-links",
                "/api/setup-links/{setupId}",
                "/api/setup-links/{setupId}/category-preview",
                "/api/setup-links/{setupId}/session",
                "/api/sessions",
                "/api/sessions/{sessionId}/status",
                "/api/sessions/{sessionId}/excluded-categories",
                "/api/sessions/{sessionId}/media?kind=live",
                "/api/sessions/{sessionId}/categories?kind=live",
                "/api/sessions/{sessionId}/curated-lists?kind=live",
                "/api/sessions/{sessionId}/guide?ids=live-123,live-456",
                "/api/sessions/{sessionId}/item/{id}?kind=live",
                "/api/sessions/{sessionId}/play/{id}?kind=live",
                "/api/sessions/{sessionId}/stop",
                "/api/sessions/{sessionId}/remote/command",
                "/api/sessions/{sessionId}/remote/commands?after=0",
                "/api/media?kind=live",
                "/api/media?kind=movies",
                "/api/categories?kind=live",
                "/api/guide?ids=live-123,live-456",
                "/api/item/{id}?kind=live",
                "/api/play/{id}?kind=live",
                "/api/stop/{sessionId}",
                "/api/remote/command",
                "/api/remote/commands?after=0",
                "/remote",
                "/setup",
                "/sessions",
                "/api/hls/{sessionId}/{fileName}"
            }
        }));

        _app.MapGet("/remote", () => ServeRemoteAsset("index.html"));
        _app.MapGet("/remote-sw.js", (HttpContext context) =>
        {
            context.Response.Headers["Service-Worker-Allowed"] = "/";
            return ServeRemoteAsset("sw.js");
        });
        _app.MapGet("/remote/manifest.webmanifest", (string? session) => Results.Json(new Dictionary<string, object?>
        {
            ["name"] = "M3U TV Remote",
            ["short_name"] = "M3U Remote",
            ["description"] = "Local remote control for M3U Playlist Player.",
            ["start_url"] = string.IsNullOrWhiteSpace(session)
                ? "/sessions"
                : $"/remote?session={Uri.EscapeDataString(session)}",
            ["scope"] = "/",
            ["display"] = "standalone",
            ["background_color"] = "#090909",
            ["theme_color"] = "#090909",
            ["orientation"] = "portrait",
            ["icons"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["src"] = "/remote/icon.svg",
                    ["sizes"] = "any",
                    ["type"] = "image/svg+xml",
                    ["purpose"] = "any maskable"
                }
            }
        }, contentType: "application/manifest+json"));
        _app.MapGet("/remote/{fileName}", (string fileName) => ServeRemoteAsset(fileName));
        _app.MapGet("/setup", () => ServeRemoteAsset("setup.html"));
        _app.MapGet("/sessions", () => ServeRemoteAsset("sessions.html"));
        _app.MapGet("/api/qr.svg", (string? value) =>
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            {
                return Results.BadRequest("A QR value is required.");
            }

            return Results.Text(CreateQrSvg(value), "image/svg+xml");
        });

        _app.MapGet("/api/status", (HttpContext context) => Results.Json(new
        {
            status = "running",
            host = Dns.GetHostName(),
            localUrl = GetLocalNetworkUrl(context),
            localUrls = GetLocalAddressCandidates()
                .Select(address => BuildUrl(address, context.Connection.LocalPort))
                .ToArray(),
            time = DateTimeOffset.Now
        }));

        _app.MapPost("/api/setup-links", (HttpContext context, CreateSetupLinkRequest request) =>
        {
            var link = _setupLinkManager.Create(request.DeviceName, DateTimeOffset.Now);
            var setupUrl = BuildRequestUrl(context, $"/setup?code={Uri.EscapeDataString(link.Id)}");
            return Results.Json(new
            {
                setupId = link.Id,
                setupUrl,
                qrUrl = BuildRequestUrl(context, $"/api/setup-links/{Uri.EscapeDataString(link.Id)}/qr.svg"),
                expiresAt = link.ExpiresAt
            });
        });

        _app.MapGet("/api/setup-links/{setupId}", (string setupId) =>
        {
            return _setupLinkManager.TryGet(setupId, out var link)
                ? Results.Json(ToSafeSetupLinkStatus(link))
                : Results.NotFound(new { error = "Setup link not found or expired." });
        });

        _app.MapGet("/api/setup-links/{setupId}/configuration", (string setupId) =>
        {
            if (!_setupLinkManager.TryGet(setupId, out var link))
            {
                return Results.NotFound(new { error = "Setup link not found or expired." });
            }

            var configuration = link.Configuration;
            return configuration is null
                ? Results.Json(new { submitted = false })
                : Results.Json(new
                {
                    submitted = true,
                    playlistUrl = configuration.PlaylistUrl,
                    epgUrl = configuration.EpgUrl,
                    excludedCategories = configuration.ExcludedCategories,
                    selectedCategories = configuration.SelectedCategories
                });
        });

        _app.MapPost("/api/setup-links/{setupId}/category-preview", async (string setupId, CategoryPreviewRequest request, CancellationToken token) =>
        {
            if (!_setupLinkManager.TryGet(setupId, out _))
            {
                return Results.NotFound(new { error = "Setup link not found or expired." });
            }

            if (!XtreamSettings.TryFromPlaylistUrl(request.PlaylistUrl, request.EpgUrl, out var settings, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var client = new XtreamClient(settings);
            var kind = ParseKind(request.Kind);
            IReadOnlyList<string> categories;
            var upstreamUnavailable = false;
            try
            {
                categories = await client.GetCategoriesAsync(kind, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                upstreamUnavailable = true;
                categories = [];
                LogUpstreamFailure("setup-category-preview", ex);
            }

            return Results.Json(new
            {
                kind = kind.ToString().ToLowerInvariant(),
                upstreamUnavailable,
                count = categories.Count,
                categories
            });
        });

        _app.MapPost("/api/setup-links/{setupId}", (string setupId, SubmitSetupRequest request) =>
        {
            if (!_setupLinkManager.TryGet(setupId, out var link))
            {
                return Results.NotFound(new { error = "Setup link not found or expired." });
            }

            if (!XtreamSettings.TryFromPlaylistUrl(request.PlaylistUrl, request.EpgUrl, out _, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var excludedCategories = request.ExcludedCategories?
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedCategories = request.SelectedCategories?
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var configuration = new SetupConfiguration(
                request.PlaylistUrl!.Trim(),
                string.IsNullOrWhiteSpace(request.EpgUrl) ? null : request.EpgUrl.Trim(),
                excludedCategories,
                selectedCategories);
            _setupLinkManager.Submit(setupId, configuration);
            return Results.Json(new
            {
                saved = true,
                setupId = link.Id
            });
        });

        _app.MapPost("/api/setup-links/{setupId}/session", (string setupId, SetupSessionRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.RemoteUrl))
            {
                return Results.BadRequest(new { error = "Session id and remote URL are required." });
            }

            if (!Uri.TryCreate(request.RemoteUrl, UriKind.Absolute, out _))
            {
                return Results.BadRequest(new { error = "Remote URL is not valid." });
            }

            return _setupLinkManager.AttachSession(setupId, new SetupSession(request.SessionId.Trim(), request.RemoteUrl.Trim()))
                ? Results.Json(new { saved = true, setupId })
                : Results.NotFound(new { error = "Setup link not found or expired." });
        });

        _app.MapGet("/api/setup-links/{setupId}/qr.svg", (HttpContext context, string setupId) =>
        {
            if (!_setupLinkManager.TryGet(setupId, out _))
            {
                return Results.NotFound();
            }

            var setupUrl = BuildRequestUrl(context, $"/setup?code={Uri.EscapeDataString(setupId)}");
            return Results.Text(CreateQrSvg(setupUrl), "image/svg+xml");
        });

        _app.MapPost("/api/sessions", (HttpContext context, CreateSessionRequest request) =>
        {
            if (!XtreamSettings.TryFromPlaylistUrl(request.PlaylistUrl, request.EpgUrl, out var settings, out var error))
            {
                return Results.BadRequest(new
                {
                    error
                });
            }

            var session = _sessionManager.CreateSession(
                request.DeviceName,
                settings,
                request.PlaybackMode,
                request.ExcludedCategories,
                request.SelectedCategories,
                request.RequestedSessionId);
            LoadPersistedCuratedLists(session);
            WarmGuideCacheInBackground(session);
            return Results.Json(new
            {
                sessionId = session.Id,
                remoteUrl = BuildRequestUrl(context, $"/remote?session={Uri.EscapeDataString(session.Id)}"),
                expiresAt = session.ExpiresAt
            });
        });

        _app.MapGet("/api/sessions", () => Results.Json(new
        {
            count = _sessionManager.GetSessions().Count,
            sessions = _sessionManager.GetSessions().Select(ToSafeSessionStatus).ToArray()
        }));

        _app.MapGet("/api/sessions/{sessionId}/status", (string sessionId) =>
        {
            return _sessionManager.TryGetSession(sessionId, out var session)
                ? Results.Json(ToSafeSessionStatus(session))
                : Results.NotFound(new { error = "Session not found or expired." });
        });

        _app.MapPost("/api/sessions/{sessionId}/excluded-categories", (string sessionId, CategoriesSelectionRequest request) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var mediaKind = ParseKind(request.Kind);
            session.SetExcludedCategories(mediaKind, request.ExcludedCategories);
            session.SetSelectedCategories(mediaKind, request.SelectedCategories);
            return Results.Json(new
            {
                saved = true,
                sessionId = session.Id,
                kind = mediaKind.ToString().ToLowerInvariant(),
                excludedCategories = session.GetExcludedCategories(mediaKind),
                selectedCategories = session.GetSelectedCategories(mediaKind)
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/curated-lists", (string sessionId, string? kind) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var mediaKind = ParseKind(kind);
            var builtIns = new List<object>
            {
                new
                {
                    id = "all",
                    kind = mediaKind.ToString().ToLowerInvariant(),
                    name = mediaKind == MediaKind.Live ? "All channels" : "All movies",
                    count = session.GetKnownCount(mediaKind),
                    builtIn = true
                }
            };

            if (mediaKind == MediaKind.Live)
            {
                builtIns.Add(new
                {
                    id = "builtin-uk",
                    kind = "live",
                    name = "UK Essentials",
                    count = (int?)null,
                    builtIn = true
                });
            }

            var custom = session.GetCuratedLists(mediaKind)
                .Select(ToCuratedListSummary)
                .ToArray();

            return Results.Json(new
            {
                sessionId = session.Id,
                kind = mediaKind.ToString().ToLowerInvariant(),
                lists = builtIns.Concat(custom).ToArray()
            });
        });

        _app.MapPost("/api/sessions/{sessionId}/curated-lists", (string sessionId, SaveCuratedListRequest request) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "List name is required." });
            }

            if (request.ItemIds is null || request.ItemIds.Count == 0)
            {
                return Results.BadRequest(new { error = "Choose at least one channel." });
            }

            var mediaKind = ParseKind(request.Kind);
            var list = session.SaveCuratedList(mediaKind, request.Name, request.ItemIds);
            _curatedListStore.Save(session.Settings, list);

            return Results.Json(new
            {
                saved = true,
                list = ToCuratedListSummary(list)
            });
        });

        _app.MapPut("/api/sessions/{sessionId}/curated-lists/{listId}", (string sessionId, string listId, SaveCuratedListRequest request) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            if (IsBuiltInCuratedList(listId))
            {
                return Results.BadRequest(new { error = "Built-in lists cannot be edited." });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "List name is required." });
            }

            if (request.ItemIds is null || request.ItemIds.Count == 0)
            {
                return Results.BadRequest(new { error = "Choose at least one channel." });
            }

            var mediaKind = ParseKind(request.Kind);
            var list = session.UpsertCuratedList(mediaKind, listId, request.Name, request.ItemIds);
            _curatedListStore.Save(session.Settings, list);

            return Results.Json(new
            {
                saved = true,
                list = ToCuratedListSummary(list)
            });
        });

        _app.MapDelete("/api/sessions/{sessionId}/curated-lists/{listId}", (string sessionId, string listId, string? kind) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            if (IsBuiltInCuratedList(listId))
            {
                return Results.BadRequest(new { error = "Built-in lists cannot be deleted." });
            }

            var mediaKind = ParseKind(kind);
            var deleted = session.DeleteCuratedList(mediaKind, listId);
            _curatedListStore.Delete(session.Settings, mediaKind, listId);

            return deleted
                ? Results.Json(new { deleted = true, listId })
                : Results.NotFound(new { error = "List not found." });
        });

        _app.MapGet("/api/sessions/{sessionId}/media", async (
            HttpContext context,
            string sessionId,
            string? kind,
            string? query,
            string? group,
            string? region,
            string? list,
            int? skip,
            int? limit,
            CancellationToken token) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var mediaKind = ParseKind(kind);
            var safeSkip = Math.Max(0, skip ?? 0);
            var safeLimit = Math.Clamp(limit ?? 240, 1, 1000);
            var safeRegion = string.Equals(list, "builtin-uk", StringComparison.OrdinalIgnoreCase)
                ? "uk"
                : region;
            var excludedGroups = ReadExcludedGroups(context, session, mediaKind);
            var selectedGroups = session.GetSelectedCategories(mediaKind);
            var upstreamUnavailable = false;
            XtreamClient.MediaPage page;
            try
            {
                page = await GetSessionMediaPageAsync(session, mediaKind, query, group, safeRegion, list, excludedGroups, selectedGroups, safeSkip, safeLimit, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                upstreamUnavailable = true;
                page = EmptyMediaPage();
                LogUpstreamFailure("session-media", ex);
            }
            session.RememberCount(mediaKind, Math.Max(page.MatchedCount, safeSkip + page.Items.Count));

            return Results.Json(new
            {
                sessionId = session.Id,
                kind = mediaKind.ToString().ToLowerInvariant(),
                upstreamUnavailable,
                count = page.MatchedCount,
                hasMore = page.HasMore,
                skip = safeSkip,
                limit = safeLimit,
                items = page.Items
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/categories", async (string sessionId, string? kind, bool? keptOnly, CancellationToken token) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var mediaKind = ParseKind(kind);
            IReadOnlyList<string> categories;
            var upstreamUnavailable = false;
            try
            {
                categories = await session.Client.GetCategoriesAsync(mediaKind, token);
                if (keptOnly == true)
                {
                    var selected = ToGroupSet(session.GetSelectedCategories(mediaKind));
                    if (selected is not null)
                    {
                        categories = categories
                            .Where(category => selected.Contains(NormalizeGroup(category)))
                            .ToArray();
                    }
                    else
                    {
                        var excluded = ToGroupSet(session.GetExcludedCategories(mediaKind));
                        categories = excluded is null
                            ? categories
                            : categories
                                .Where(category => !excluded.Contains(NormalizeGroup(category)))
                                .ToArray();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                upstreamUnavailable = true;
                categories = [];
                LogUpstreamFailure("session-categories", ex);
            }

            return Results.Json(new
            {
                sessionId = session.Id,
                kind = mediaKind.ToString().ToLowerInvariant(),
                upstreamUnavailable,
                count = categories.Count,
                categories
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/guide", async (string sessionId, string? ids, string? source, CancellationToken token) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var requestedIds = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(300)
                .ToArray();
            var guideSource = ParseGuideSource(source);
            IReadOnlyDictionary<string, XtreamClient.GuideInfo> guide;
            string[] missingIds;
            var guideUnavailable = false;
            try
            {
                guide = await session.Client.GetGuideAsync(requestedIds, guideSource.IncludeMainGuide, guideSource.IncludeShortGuide, token);
                missingIds = GetMissingGuideIds(guide);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                guideUnavailable = true;
                guide = CreateEmptyGuide(requestedIds);
                missingIds = requestedIds;
                LogGuideEndpointFailure(guideSource.Name, ex);
            }

            return Results.Json(new
            {
                sessionId = session.Id,
                source = guideSource.Name,
                guideUnavailable,
                count = guide.Count,
                missingIds,
                guide
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/item/{id}", async (string sessionId, string id, string? kind, CancellationToken token) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var mediaKind = ParseKind(kind);
            IReadOnlyList<MediaItem> items;
            try
            {
                items = await session.Client.GetMediaAsync(mediaKind, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogUpstreamFailure("session-item", ex);
                items = [];
            }
            var item = items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            return item is null ? Results.NotFound() : Results.Json(item);
        });

        _app.MapGet("/api/sessions/{sessionId}/play/{id}", async (HttpContext context, string sessionId, string id, string? kind, CancellationToken token) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            try
            {
                var mediaKind = ParseKind(kind);
                var items = await session.Client.GetMediaAsync(mediaKind, token);
                var item = items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    return Results.NotFound(new
                    {
                        error = "Item not found",
                        id,
                        kind = mediaKind.ToString().ToLowerInvariant()
                    });
                }

                var hlsSession = await session.HlsStreamService.StartAsync(item, token);
                return Results.Json(new
                {
                    mode = "hls",
                    id = item.Id,
                    sessionId = hlsSession.Id,
                    item.Name,
                    url = BuildRequestUrl(context, $"/api/sessions/{Uri.EscapeDataString(session.Id)}/hls/{Uri.EscapeDataString(hlsSession.Id)}/index.m3u8")
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Could not prepare session playback",
                    detail: RedactSensitiveText(ex.Message),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        _app.MapGet("/api/sessions/{sessionId}/hls/{hlsSessionId}/{fileName}", (string sessionId, string hlsSessionId, string fileName) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound();
            }

            return session.HlsStreamService.TryGetFile(hlsSessionId, fileName, out var path, out var contentType)
                ? Results.File(path, contentType, enableRangeProcessing: true)
                : Results.NotFound();
        });

        _app.MapPost("/api/sessions/{sessionId}/stop", (string sessionId) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            session.HlsStreamService.StopAll();
            return Results.Json(new
            {
                stopped = true,
                sessionId = session.Id
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/stop", (string sessionId) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            session.HlsStreamService.StopAll();
            return Results.Json(new
            {
                stopped = true,
                sessionId = session.Id
            });
        });

        _app.MapPost("/api/sessions/{sessionId}/remote/command", (string sessionId, RemoteCommandRequest request) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var commandType = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (commandType is not ("play" or "stop"))
            {
                return Results.BadRequest(new
                {
                    error = "Command type must be play or stop."
                });
            }

            if (commandType == "play" && string.IsNullOrWhiteSpace(request.ItemId))
            {
                return Results.BadRequest(new
                {
                    error = "Play commands need an itemId."
                });
            }

            var command = session.AcceptRemoteCommand(request);
            return Results.Json(new
            {
                accepted = true,
                command.Sequence
            });
        });

        _app.MapGet("/api/sessions/{sessionId}/remote/commands", (string sessionId, long? after) =>
        {
            if (!_sessionManager.TryGetSession(sessionId, out var session))
            {
                return Results.NotFound(new { error = "Session not found or expired." });
            }

            var result = session.GetRemoteCommandAfter(Math.Max(0, after ?? 0));
            return Results.Json(new
            {
                hasCommand = result.HasCommand,
                sequence = result.Sequence,
                command = result.Command
            });
        });

        _app.MapGet("/api/media", async (HttpContext context, string? kind, string? query, string? group, string? region, int? skip, int? limit, CancellationToken token) =>
        {
            var mediaKind = ParseKind(kind);
            var safeSkip = Math.Max(0, skip ?? 0);
            var safeLimit = Math.Clamp(limit ?? 240, 1, 1000);
            var excludedGroups = ReadExcludedGroups(context);
            var upstreamUnavailable = false;
            XtreamClient.MediaPage page;
            try
            {
                page = await _client.GetMediaPageAsync(mediaKind, query, group, region, excludedGroups, null, safeSkip, safeLimit, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                upstreamUnavailable = true;
                page = EmptyMediaPage();
                LogUpstreamFailure("media", ex);
            }

            return Results.Json(new
            {
                kind = mediaKind.ToString().ToLowerInvariant(),
                upstreamUnavailable,
                count = page.MatchedCount,
                hasMore = page.HasMore,
                skip = safeSkip,
                limit = safeLimit,
                items = page.Items
            });
        });

        _app.MapGet("/api/categories", async (string? kind, CancellationToken token) =>
        {
            var mediaKind = ParseKind(kind);
            IReadOnlyList<string> categories;
            var upstreamUnavailable = false;
            try
            {
                categories = await _client.GetCategoriesAsync(mediaKind, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                upstreamUnavailable = true;
                categories = [];
                LogUpstreamFailure("categories", ex);
            }

            return Results.Json(new
            {
                kind = mediaKind.ToString().ToLowerInvariant(),
                upstreamUnavailable,
                count = categories.Count,
                categories
            });
        });

        _app.MapGet("/api/guide", async (string? ids, string? source, CancellationToken token) =>
        {
            var requestedIds = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(300)
                .ToArray();
            var guideSource = ParseGuideSource(source);
            IReadOnlyDictionary<string, XtreamClient.GuideInfo> guide;
            string[] missingIds;
            var guideUnavailable = false;
            try
            {
                guide = await _client.GetGuideAsync(requestedIds, guideSource.IncludeMainGuide, guideSource.IncludeShortGuide, token);
                missingIds = GetMissingGuideIds(guide);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                guideUnavailable = true;
                guide = CreateEmptyGuide(requestedIds);
                missingIds = requestedIds;
                LogGuideEndpointFailure(guideSource.Name, ex);
            }

            return Results.Json(new
            {
                source = guideSource.Name,
                guideUnavailable,
                count = guide.Count,
                missingIds,
                guide
            });
        });

        _app.MapGet("/api/item/{id}", async (string id, string? kind, CancellationToken token) =>
        {
            var mediaKind = ParseKind(kind);
            IReadOnlyList<MediaItem> items;
            try
            {
                items = await GetMediaAsync(mediaKind, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogUpstreamFailure("item", ex);
                items = [];
            }

            var item = items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            return item is null ? Results.NotFound() : Results.Json(item);
        });

        _app.MapGet("/api/play/{id}", async (HttpContext context, string id, string? kind, CancellationToken token) =>
        {
            try
            {
                var mediaKind = ParseKind(kind);
                var items = await GetMediaAsync(mediaKind, token);
                var item = items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    return Results.NotFound(new
                    {
                        error = "Item not found",
                        id,
                        kind = mediaKind.ToString().ToLowerInvariant()
                    });
                }

                var session = await _hlsStreamService.StartAsync(item, token);
                return Results.Json(new
                {
                    mode = "hls",
                    id = item.Id,
                    sessionId = session.Id,
                    item.Name,
                    url = BuildRequestUrl(context, $"/api/hls/{Uri.EscapeDataString(session.Id)}/index.m3u8")
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Could not prepare TV playback",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        _app.MapGet("/api/hls/{sessionId}/{fileName}", (string sessionId, string fileName) =>
        {
            return _hlsStreamService.TryGetFile(sessionId, fileName, out var path, out var contentType)
                ? Results.File(path, contentType, enableRangeProcessing: true)
                : Results.NotFound();
        });

        _app.MapGet("/api/stop/{sessionId}", (string sessionId) =>
        {
            var stopped = _hlsStreamService.Stop(sessionId);
            return Results.Json(new
            {
                stopped,
                sessionId
            });
        });

        _app.MapPost("/api/remote/command", (RemoteCommandRequest request) =>
        {
            var commandType = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (commandType is not ("play" or "stop"))
            {
                return Results.BadRequest(new
                {
                    error = "Command type must be play or stop."
                });
            }

            if (commandType == "play" && string.IsNullOrWhiteSpace(request.ItemId))
            {
                return Results.BadRequest(new
                {
                    error = "Play commands need an itemId."
                });
            }

            RemoteCommand command;
            lock (_remoteLock)
            {
                var item = request.Item.HasValue ? request.Item.Value.Clone() : (JsonElement?)null;
                _remoteCommandSequence += 1;
                command = new RemoteCommand(
                    _remoteCommandSequence,
                    commandType,
                    request.ItemId,
                    NormalizeRemoteKind(request.Kind),
                    NormalizePlaybackMode(request.PlaybackMode),
                    item,
                    DateTimeOffset.Now);
                _lastRemoteCommand = command;
            }

            return Results.Json(new
            {
                accepted = true,
                command.Sequence
            });
        });

        _app.MapGet("/api/remote/commands", (long? after) =>
        {
            var since = Math.Max(0, after ?? 0);

            lock (_remoteLock)
            {
                if (_lastRemoteCommand is not null && _lastRemoteCommand.Sequence > since)
                {
                    return Results.Json(new
                    {
                        hasCommand = true,
                        sequence = _lastRemoteCommand.Sequence,
                        command = _lastRemoteCommand
                    });
                }

                return Results.Json(new
                {
                    hasCommand = false,
                    sequence = _remoteCommandSequence
                });
            }
        });

        await _app.StartAsync(cancellationToken);
        await Task.Delay(500, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        _hlsStreamService.Dispose();
        _sessionManager.Dispose();
    }

    private async Task<IReadOnlyList<MediaItem>> GetMediaAsync(MediaKind kind, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(kind, out var cached))
            {
                return cached;
            }
        }

        Task<IReadOnlyList<MediaItem>> load;
        lock (_cacheLock)
        {
            if (!_loads.TryGetValue(kind, out load!))
            {
                load = _client.GetMediaAsync(kind, cancellationToken);
                _loads[kind] = load;
            }
        }

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await load;
        }
        catch
        {
            lock (_cacheLock)
            {
                _loads.Remove(kind);
            }

            throw;
        }

        lock (_cacheLock)
        {
            _cache[kind] = items;
            _loads.Remove(kind);
        }

        return items;
    }

    private static IReadOnlyList<MediaItem> Filter(
        IReadOnlyList<MediaItem> items,
        string? query,
        string? group)
    {
        var filtered = items.AsEnumerable();
        var queryRegex = BuildSearchRegex(query);
        if (!string.IsNullOrWhiteSpace(group))
        {
            filtered = filtered.Where(item => string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase));
        }

        if (queryRegex is not null)
        {
            filtered = filtered.Where(item => SearchMatches($"{item.Name} {item.Group} {item.EpgId}", queryRegex));
        }

        return filtered.ToArray();
    }

    private static async Task<XtreamClient.MediaPage> GetSessionMediaPageAsync(
        PlaybackSession session,
        MediaKind kind,
        string? query,
        string? group,
        string? region,
        string? list,
        IReadOnlyCollection<string> excludedGroups,
        IReadOnlyCollection<string> selectedGroups,
        int skip,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(list) ||
            string.Equals(list, "all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(list, "builtin-uk", StringComparison.OrdinalIgnoreCase))
        {
            return await session.Client.GetMediaPageAsync(kind, query, group, region, excludedGroups, selectedGroups, skip, limit, cancellationToken);
        }

        if (!session.TryGetCuratedList(kind, list, out var curatedList))
        {
            return new XtreamClient.MediaPage([], 0, HasMore: false);
        }

        var allowedIds = curatedList.ItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedSet = ToGroupSet(excludedGroups);
        var selectedSet = ToGroupSet(selectedGroups);
        var queryRegex = BuildSearchRegex(query);
        var filtered = (await session.Client.GetMediaAsync(kind, cancellationToken))
            .Where(item => allowedIds.Contains(item.Id))
            .Where(item => string.IsNullOrWhiteSpace(group) || string.Equals(NormalizeGroup(item.Group), NormalizeGroup(group), StringComparison.OrdinalIgnoreCase))
            .Where(item => selectedSet is null || selectedSet.Contains(NormalizeGroup(item.Group)))
            .Where(item => excludedSet is null || !excludedSet.Contains(NormalizeGroup(item.Group)))
            .Where(item => queryRegex is null || SearchMatches($"{item.Name} {item.Group} {item.EpgId}", queryRegex))
            .ToArray();
        var pageItems = filtered
            .Skip(skip)
            .Take(limit)
            .ToArray();

        return new XtreamClient.MediaPage(pageItems, filtered.Length, skip + pageItems.Length < filtered.Length);
    }

    private static bool SearchMatches(string? value, Regex queryRegex)
    {
        return !string.IsNullOrWhiteSpace(value) && queryRegex.IsMatch(value);
    }

    private static Regex? BuildSearchRegex(string? query)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Regex.Escape)
            .ToArray();

        return terms.Length == 0
            ? null
            : new Regex(string.Join(".*", terms), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<string> ReadExcludedGroups(HttpContext context, PlaybackSession? session = null, MediaKind? kind = null)
    {
        var queryGroups = context.Request.Query["excludeGroup"]
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group!.Trim());
        var sessionGroups = session is not null && kind is not null
            ? session.GetExcludedCategories(kind.Value)
            : [];

        return queryGroups
            .Concat(sessionGroups)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlySet<string>? ToGroupSet(IReadOnlyCollection<string>? groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return null;
        }

        var set = groups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(NormalizeGroup)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return set.Count == 0 ? null : set;
    }

    private static string NormalizeGroup(string? group)
    {
        return Regex.Replace(group ?? string.Empty, "\\s+", " ").Trim();
    }

    private static MediaKind ParseKind(string? kind)
    {
        return string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Movies
            : MediaKind.Live;
    }

    private static (bool IncludeMainGuide, bool IncludeShortGuide, string Name) ParseGuideSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            "short" or "fallback" => (false, true, "short"),
            "full" or "all" => (true, true, "full"),
            _ => (true, false, "main")
        };
    }

    private static string[] GetMissingGuideIds(IReadOnlyDictionary<string, XtreamClient.GuideInfo> guide)
    {
        return guide
            .Where(pair => IsMissingGuide(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, XtreamClient.GuideInfo> CreateEmptyGuide(IReadOnlyCollection<string> ids)
    {
        return ids.ToDictionary(
            id => id,
            _ => new XtreamClient.GuideInfo(null, null),
            StringComparer.OrdinalIgnoreCase);
    }

    private static XtreamClient.MediaPage EmptyMediaPage()
    {
        return new XtreamClient.MediaPage([], 0, HasMore: false);
    }

    private static void LogUpstreamFailure(string scope, Exception ex)
    {
        var message = SanitizeLogMessage(ex.Message);
        Console.WriteLine(
            "{0:O} [Upstream:{1}] Response degraded. ErrorType={2}, Message={3}",
            DateTimeOffset.UtcNow,
            scope,
            ex.GetType().Name,
            message);
    }

    private static void LogGuideEndpointFailure(string source, Exception ex)
    {
        var message = SanitizeLogMessage(ex.Message);
        Console.WriteLine(
            "{0:O} [GuideEndpoint] Guide response degraded. Source={1}, ErrorType={2}, Message={3}",
            DateTimeOffset.UtcNow,
            source,
            ex.GetType().Name,
            message);
    }

    private static string SanitizeLogMessage(string? value)
    {
        var message = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        if (message.Length > 260)
        {
            message = message[..260] + "...";
        }

        return string.IsNullOrWhiteSpace(message) ? "<empty>" : message;
    }

    private static bool IsMissingGuide(XtreamClient.GuideInfo? guide)
    {
        return guide is null ||
               string.IsNullOrWhiteSpace(guide.NowTitle) &&
               string.IsNullOrWhiteSpace(guide.NextTitle);
    }

    private static string NormalizeRemoteKind(string? kind)
    {
        return ParseKind(kind).ToString().ToLowerInvariant();
    }

    private static string NormalizePlaybackMode(string? playbackMode)
    {
        return string.Equals(playbackMode, "direct", StringComparison.OrdinalIgnoreCase)
            ? "direct"
            : "hls";
    }

    private static IResult ServeRemoteAsset(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var contentType = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".html" => "text/html; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => null
        };

        if (contentType is null)
        {
            return Results.NotFound();
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Remote", fileName);
        return File.Exists(path)
            ? Results.File(path, contentType)
            : Results.NotFound();
    }

    private void LoadPersistedCuratedLists(PlaybackSession session)
    {
        foreach (var kind in new[] { MediaKind.Live, MediaKind.Movies })
        {
            foreach (var list in _curatedListStore.GetLists(session.Settings, kind))
            {
                session.ImportCuratedList(kind, list);
            }
        }
    }

    private static string GetLocalNetworkUrl(HttpContext context)
    {
        var port = context.Connection.LocalPort;
        var address = GetPreferredLocalAddress();

        return address is null ? $"http://localhost:{port}" : $"http://{address}:{port}";
    }

    private int GetPortFromUrl()
    {
        return Uri.TryCreate(Url.Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var uri)
            ? uri.Port
            : 5055;
    }

    private static IPAddress? GetPreferredLocalAddress()
    {
        return GetLocalAddressCandidates().FirstOrDefault();
    }

    private static IReadOnlyList<IPAddress> GetLocalAddressCandidates()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(networkInterface =>
            {
                var properties = networkInterface.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(gateway.Address));

                return properties.UnicastAddresses
                    .Where(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address) &&
                        !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(address => new { address.Address, hasGateway });
            })
            .OrderByDescending(candidate => candidate.hasGateway)
            .ThenByDescending(candidate => IsHomeNetworkAddress(candidate.Address))
            .Select(candidate => candidate.Address)
            .Distinct()
            .ToArray();

        if (candidates.Length > 0)
        {
            return candidates;
        }

        return Dns.GetHostEntry(Dns.GetHostName())
            .AddressList
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
    }

    private static bool IsHomeNetworkAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;
    }

    private static string BuildUrl(IPAddress? address, int port)
    {
        return address is null ? $"http://localhost:{port}" : $"http://{address}:{port}";
    }

    private static string BuildRequestUrl(HttpContext context, string path)
    {
        var scheme = GetForwardedHeader(context, "X-Forwarded-Proto") ??
                     GetCloudflareVisitorScheme(context) ??
                     context.Request.Scheme;
        var host = GetForwardedHeader(context, "X-Forwarded-Host") ??
                   context.Request.Host.Value;
        var safePath = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;

        return $"{NormalizeScheme(scheme)}://{host}{safePath}";
    }

    private static string NormalizeScheme(string? scheme)
    {
        return string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";
    }

    private static string? GetForwardedHeader(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var firstValue = value.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(firstValue) ? null : firstValue;
    }

    private static string? GetCloudflareVisitorScheme(HttpContext context)
    {
        var value = context.Request.Headers["Cf-Visitor"].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, "\"scheme\"\\s*:\\s*\"(?<scheme>https?)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["scheme"].Value : null;
    }

    private static string RedactSensitiveText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var redacted = Regex.Replace(value, "([?&](?:username|password)=)[^&\\s]+", "$1redacted", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted, "(/(?:live|movie)/)[^/\\s]+/[^/\\s]+/", "$1redacted/redacted/", RegexOptions.IgnoreCase);
        return redacted;
    }

    private static string CreateQrSvg(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new SvgQRCode(data);
        return qrCode.GetGraphic(6);
    }

    private static object ToSafeSessionStatus(PlaybackSession session)
    {
        return new
        {
            status = "connected",
            sessionId = session.Id,
            session.DeviceName,
            session.SourceHost,
            playlistLoaded = session.GetKnownCount(MediaKind.Live).HasValue || session.GetKnownCount(MediaKind.Movies).HasValue,
            epgConfigured = true,
            liveCount = session.GetKnownCount(MediaKind.Live),
            movieCount = session.GetKnownCount(MediaKind.Movies),
            session.PlaybackMode,
            session.CreatedAt,
            session.LastSeenAt,
            session.ExpiresAt,
            excludedCategories = new
            {
                live = session.GetExcludedCategories(MediaKind.Live),
                movies = session.GetExcludedCategories(MediaKind.Movies)
            },
            selectedCategories = new
            {
                live = session.GetSelectedCategories(MediaKind.Live),
                movies = session.GetSelectedCategories(MediaKind.Movies)
            }
        };
    }

    private static void WarmGuideCacheInBackground(PlaybackSession session)
    {
        _ = Task.Run(() => session.Client.WarmGuideCacheAsync(CancellationToken.None));
    }

    private static object ToSafeSetupLinkStatus(SetupLink link)
    {
        return new
        {
            setupId = link.Id,
            link.DeviceName,
            submitted = link.IsSubmitted,
            remoteReady = link.Session is not null,
            remoteUrl = link.Session?.RemoteUrl,
            sessionId = link.Session?.SessionId,
            link.CreatedAt,
            link.ExpiresAt,
            link.SubmittedAt,
            link.SessionAttachedAt
        };
    }

    private static object ToCuratedListSummary(CuratedList list)
    {
        return new
        {
            list.Id,
            list.Kind,
            list.Name,
            count = list.ItemIds.Count,
            builtIn = false,
            itemIds = list.ItemIds,
            list.CreatedAt
        };
    }

    private static bool IsBuiltInCuratedList(string? id)
    {
        return string.Equals(id, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(id, "builtin-uk", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CreateSetupLinkRequest(string? DeviceName);

    private sealed record SubmitSetupRequest(
        string? PlaylistUrl,
        string? EpgUrl,
        IReadOnlyList<string>? ExcludedCategories,
        IReadOnlyList<string>? SelectedCategories);

    private sealed record CategoryPreviewRequest(
        string? PlaylistUrl,
        string? EpgUrl,
        string? Kind);

    private sealed record SetupSessionRequest(string? SessionId, string? RemoteUrl);

    private sealed record CreateSessionRequest(
        string? DeviceName,
        string? PlaylistUrl,
        string? EpgUrl,
        string? PlaybackMode,
        IReadOnlyList<string>? ExcludedCategories,
        IReadOnlyList<string>? SelectedCategories,
        string? RequestedSessionId);

    private sealed record CategoriesSelectionRequest(
        string? Kind,
        IReadOnlyList<string>? ExcludedCategories,
        IReadOnlyList<string>? SelectedCategories);

    private sealed record SaveCuratedListRequest(
        string? Kind,
        string? Name,
        IReadOnlyList<string>? ItemIds);

}
