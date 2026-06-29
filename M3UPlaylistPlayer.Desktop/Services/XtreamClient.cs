using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Xml;
using M3UPlaylistPlayer.Desktop.Models;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class XtreamClient
{
    private const int MaxGuideIdsPerRequest = 300;
    private const int ShortGuideCacheMinutes = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private readonly XtreamSettings _settings;
    private readonly object _guideLock = new();
    private readonly Dictionary<int, string> _epgIdsByLiveStreamId = [];
    private readonly Dictionary<int, string> _namesByLiveStreamId = [];
    private readonly Dictionary<int, CachedShortGuideEntry> _shortGuideCacheByStreamId = [];
    private IReadOnlyDictionary<string, GuideInfo>? _xmltvGuideCache;
    private DateTimeOffset _xmltvGuideExpires = DateTimeOffset.MinValue;
    private Task<IReadOnlyDictionary<string, GuideInfo>>? _xmltvGuideLoad;

    public XtreamClient(XtreamSettings settings)
    {
        _settings = settings;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("M3UPlaylistPlayer.Desktop/1.0");
    }

    public async Task<IReadOnlyList<MediaItem>> GetMediaAsync(MediaKind kind, CancellationToken cancellationToken)
    {
        var categoryAction = kind == MediaKind.Live ? "get_live_categories" : "get_vod_categories";
        var streamAction = kind == MediaKind.Live ? "get_live_streams" : "get_vod_streams";

        var categories = await GetJsonAsync<List<XtreamCategory>>(BuildApiUrl(categoryAction), cancellationToken) ?? [];
        var categoryNames = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.CategoryId))
            .GroupBy(category => category.CategoryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().CategoryName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        return kind == MediaKind.Live
            ? await GetLiveStreamsAsync(categoryNames, cancellationToken)
            : await GetMoviesAsync(categoryNames, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(MediaKind kind, CancellationToken cancellationToken)
    {
        var categoryAction = kind == MediaKind.Live ? "get_live_categories" : "get_vod_categories";
        var categories = await GetJsonAsync<List<XtreamCategory>>(BuildApiUrl(categoryAction), cancellationToken) ?? [];

        return categories
            .Select(category => category.CategoryName)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(category => category!)
            .ToArray();
    }

    public async Task<MediaPage> GetMediaPageAsync(
        MediaKind kind,
        string? query,
        string? group,
        string? region,
        IReadOnlyCollection<string>? excludedGroups,
        IReadOnlyCollection<string>? selectedGroups,
        int skip,
        int limit,
        CancellationToken cancellationToken)
    {
        var categoryAction = kind == MediaKind.Live ? "get_live_categories" : "get_vod_categories";
        var categories = await GetJsonAsync<List<XtreamCategory>>(BuildApiUrl(categoryAction), cancellationToken) ?? [];
        var categoryNames = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.CategoryId))
            .GroupBy(category => category.CategoryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First().CategoryName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        return kind == MediaKind.Live
            ? await GetLivePageAsync(categoryNames, query, group, region, excludedGroups, selectedGroups, skip, limit, cancellationToken)
            : await GetMoviePageAsync(categoryNames, query, group, region, excludedGroups, selectedGroups, skip, limit, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, GuideInfo>> GetGuideAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..8];
        LogGuidePerf(
            requestId,
            "GetGuideAsync start. RequestedIds={0}",
            ids.Count);

        var normalizeStopwatch = Stopwatch.StartNew();
        var liveIds = ids
            .Select(TryParseLiveStreamId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .Take(MaxGuideIdsPerRequest)
            .ToArray();
        normalizeStopwatch.Stop();

        LogGuidePerf(
            requestId,
            "Normalized ids in {0}ms. LiveIds={1}, MaxPerRequest={2}",
            normalizeStopwatch.ElapsedMilliseconds,
            liveIds.Length,
            MaxGuideIdsPerRequest);

        var results = liveIds.ToDictionary(
            streamId => $"live-{streamId}",
            _ => new GuideInfo(null, null),
            StringComparer.OrdinalIgnoreCase);

        var cachedFillBefore = CountMissingGuide(results, liveIds);
        var cachedFillStopwatch = Stopwatch.StartNew();
        FillMissingGuideFromCachedXmltv(results, liveIds);
        cachedFillStopwatch.Stop();
        var cachedFillAfter = CountMissingGuide(results, liveIds);

        LogGuidePerf(
            requestId,
            "Cached XMLTV fill completed in {0}ms. MissingBefore={1}, MissingAfter={2}",
            cachedFillStopwatch.ElapsedMilliseconds,
            cachedFillBefore,
            cachedFillAfter);

        var xmltvFillBefore = CountMissingGuide(results, liveIds);
        var xmltvFillAfter = xmltvFillBefore;
        if (xmltvFillBefore > 0)
        {
            var xmltvFillStopwatch = Stopwatch.StartNew();
            try
            {
                await FillMissingGuideFromXmltvAsync(results, liveIds, cancellationToken);
            }
            catch (Exception ex)
            {
                LogGuidePerf(
                    requestId,
                    "XMLTV fill failed in {0}ms. ErrorType={1}",
                    xmltvFillStopwatch.ElapsedMilliseconds,
                    ex.GetType().Name);
            }

            xmltvFillStopwatch.Stop();
            xmltvFillAfter = CountMissingGuide(results, liveIds);
            LogGuidePerf(
                requestId,
                "XMLTV fill completed in {0}ms. MissingBefore={1}, MissingAfter={2}",
                xmltvFillStopwatch.ElapsedMilliseconds,
                xmltvFillBefore,
                xmltvFillAfter);
        }
        else
        {
            LogGuidePerf(
                requestId,
                "XMLTV fill skipped. MissingBefore={0}",
                xmltvFillBefore);
        }

        var shortGuideIds = liveIds
            .Where(streamId => IsMissingGuide(results.GetValueOrDefault($"live-{streamId}")))
            .ToArray();

        var shortCacheFillBefore = shortGuideIds.Length;
        var shortCacheHits = 0;
        if (shortGuideIds.Length > 0)
        {
            foreach (var streamId in shortGuideIds)
            {
                if (TryGetCachedShortGuide(streamId, out var cachedGuide))
                {
                    results[$"live-{streamId}"] = cachedGuide;
                    shortCacheHits++;
                }
            }

            shortGuideIds = liveIds
                .Where(streamId => IsMissingGuide(results.GetValueOrDefault($"live-{streamId}")))
                .ToArray();
        }

        LogGuidePerf(
            requestId,
            "Short EPG cache fill done. MissingBefore={0}, CacheHits={1}, MissingAfter={2}",
            shortCacheFillBefore,
            shortCacheHits,
            shortGuideIds.Length);

        LogGuidePerf(
            requestId,
            "Short EPG stage prepared. RequestsNeeded={0}",
            shortGuideIds.Length);

        if (shortGuideIds.Length == 0)
        {
            totalStopwatch.Stop();
            LogGuidePerf(
                requestId,
                "GetGuideAsync done in {0}ms. MissingBeforeFallback=0, MissingAfterFallback=0, Returned={1}",
                totalStopwatch.ElapsedMilliseconds,
                results.Count);

            return results;
        }

        long shortGuideTotalMs = 0;
        var shortGuideSuccess = 0;
        var shortGuideFailures = 0;

        using var semaphore = new SemaphoreSlim(3);
        var tasks = shortGuideIds.Select(async streamId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var shortStopwatch = Stopwatch.StartNew();
                var guide = await GetShortGuideAsync(streamId, cancellationToken);
                shortStopwatch.Stop();

                lock (results)
                {
                    results[$"live-{streamId}"] = guide;
                }

                CacheShortGuide(streamId, guide);

                Interlocked.Add(ref shortGuideTotalMs, shortStopwatch.ElapsedMilliseconds);
                Interlocked.Increment(ref shortGuideSuccess);
            }
            catch
            {
                var emptyGuide = new GuideInfo(null, null);
                lock (results)
                {
                    results[$"live-{streamId}"] = emptyGuide;
                }

                CacheShortGuide(streamId, emptyGuide);

                Interlocked.Increment(ref shortGuideFailures);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var shortStageStopwatch = Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        shortStageStopwatch.Stop();

        LogGuidePerf(
            requestId,
            "Short EPG completed in {0}ms. Success={1}, Failures={2}, SumCallMs={3}",
            shortStageStopwatch.ElapsedMilliseconds,
            shortGuideSuccess,
            shortGuideFailures,
            shortGuideTotalMs);

        var fallbackBefore = CountMissingGuide(results, liveIds);
        var fallbackStopwatch = Stopwatch.StartNew();
        FillMissingGuideFromCachedXmltv(results, liveIds);
        fallbackStopwatch.Stop();
        var fallbackAfter = CountMissingGuide(results, liveIds);

        totalStopwatch.Stop();
        LogGuidePerf(
            requestId,
            "GetGuideAsync done in {0}ms. MissingBeforeFallback={1}, MissingAfterFallback={2}, Returned={3}",
            totalStopwatch.ElapsedMilliseconds,
            fallbackBefore,
            fallbackAfter,
            results.Count);

        return results;
    }

    public async Task WarmGuideCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetXmltvGuideAsync(cancellationToken);
        }
        catch
        {
            lock (_guideLock)
            {
                _xmltvGuideLoad = null;
            }
        }
    }

    private async Task<IReadOnlyList<MediaItem>> GetLiveStreamsAsync(
        IReadOnlyDictionary<string, string> categoryNames,
        CancellationToken cancellationToken)
    {
        var streams = await GetJsonAsync<List<XtreamLiveStream>>(BuildApiUrl("get_live_streams"), cancellationToken) ?? [];
        return streams
            .Where(stream => stream.StreamId is not null && !string.IsNullOrWhiteSpace(stream.Name))
            .Select(stream =>
            {
                var streamId = stream.StreamId.GetValueOrDefault();
                var group = stream.CategoryId is not null && categoryNames.TryGetValue(stream.CategoryId, out var category)
                    ? category
                    : string.Empty;
                var url = !string.IsNullOrWhiteSpace(stream.DirectSource) && Uri.TryCreate(stream.DirectSource, UriKind.Absolute, out _)
                    ? stream.DirectSource
                    : $"{_settings.Origin}/live/{Uri.EscapeDataString(_settings.Username)}/{Uri.EscapeDataString(_settings.Password)}/{streamId}.ts";

                TrackLiveInfo(streamId, stream.EpgChannelId, stream.Name);
                return new MediaItem($"live-{streamId}", MediaKind.Live, stream.Name!, group, url, stream.StreamIcon, stream.EpgChannelId);
            })
            .OrderBy(item => item.Group)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private async Task<MediaPage> GetLivePageAsync(
        IReadOnlyDictionary<string, string> categoryNames,
        string? query,
        string? group,
        string? region,
        IReadOnlyCollection<string>? excludedGroups,
        IReadOnlyCollection<string>? selectedGroups,
        int skip,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>();
        var matched = 0;
        IReadOnlyDictionary<string, GuideInfo>? guide = null;
        var queryRegex = BuildSearchRegex(query);
        var excludedSet = ToExcludedGroupSet(excludedGroups);
        var selectedSet = ToGroupSet(selectedGroups);
        if (queryRegex is not null)
        {
            try
            {
                guide = await GetXmltvGuideAsync(cancellationToken);
            }
            catch
            {
                guide = null;
            }
        }

        await using var stream = await _httpClient.GetStreamAsync(BuildApiUrl("get_live_streams"), cancellationToken);

        await foreach (var liveStream in JsonSerializer.DeserializeAsyncEnumerable<XtreamLiveStream>(stream, JsonOptions, cancellationToken))
        {
            if (liveStream?.StreamId is null || string.IsNullOrWhiteSpace(liveStream.Name))
            {
                continue;
            }

            var item = CreateLiveItem(liveStream, categoryNames);
            if (!Matches(item, queryRegex, group, region, excludedSet, selectedSet, guide))
            {
                continue;
            }

            if (matched++ < skip)
            {
                continue;
            }

            items.Add(item);
            if (items.Count >= limit)
            {
                return new MediaPage(items, matched, HasMore: true);
            }
        }

        return new MediaPage(items, matched, HasMore: false);
    }

    private async Task<IReadOnlyList<MediaItem>> GetMoviesAsync(
        IReadOnlyDictionary<string, string> categoryNames,
        CancellationToken cancellationToken)
    {
        var streams = await GetJsonAsync<List<XtreamMovieStream>>(BuildApiUrl("get_vod_streams"), cancellationToken) ?? [];
        return streams
            .Where(stream => stream.StreamId is not null && !string.IsNullOrWhiteSpace(stream.Name))
            .Select(stream =>
            {
                var group = stream.CategoryId is not null && categoryNames.TryGetValue(stream.CategoryId, out var category)
                    ? category
                    : string.Empty;
                var extension = string.IsNullOrWhiteSpace(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension.Trim('.');
                var url = $"{_settings.Origin}/movie/{Uri.EscapeDataString(_settings.Username)}/{Uri.EscapeDataString(_settings.Password)}/{stream.StreamId}.{extension}";

                return new MediaItem($"movie-{stream.StreamId}", MediaKind.Movies, stream.Name!, group, url, stream.StreamIcon, null);
            })
            .OrderBy(item => item.Group)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private async Task<MediaPage> GetMoviePageAsync(
        IReadOnlyDictionary<string, string> categoryNames,
        string? query,
        string? group,
        string? region,
        IReadOnlyCollection<string>? excludedGroups,
        IReadOnlyCollection<string>? selectedGroups,
        int skip,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaItem>();
        var matched = 0;
        var queryRegex = BuildSearchRegex(query);
        var excludedSet = ToExcludedGroupSet(excludedGroups);
        var selectedSet = ToGroupSet(selectedGroups);
        await using var stream = await _httpClient.GetStreamAsync(BuildApiUrl("get_vod_streams"), cancellationToken);

        await foreach (var movieStream in JsonSerializer.DeserializeAsyncEnumerable<XtreamMovieStream>(stream, JsonOptions, cancellationToken))
        {
            if (movieStream?.StreamId is null || string.IsNullOrWhiteSpace(movieStream.Name))
            {
                continue;
            }

            var item = CreateMovieItem(movieStream, categoryNames);
            if (!Matches(item, queryRegex, group, region, excludedSet, selectedSet))
            {
                continue;
            }

            if (matched++ < skip)
            {
                continue;
            }

            items.Add(item);
            if (items.Count >= limit)
            {
                return new MediaPage(items, matched, HasMore: true);
            }
        }

        return new MediaPage(items, matched, HasMore: false);
    }

    private async Task<GuideInfo> GetShortGuideAsync(int streamId, CancellationToken cancellationToken)
    {
        var url = BuildApiUrl(
            "get_short_epg",
            ("stream_id", streamId.ToString()),
            ("limit", "4"));
        var response = await GetJsonAsync<ShortEpgResponse>(url, cancellationToken);
        var listings = response?.Listings ?? [];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        EpgListing? current = null;
        EpgListing? next = null;

        foreach (var listing in listings.OrderBy(listing => listing.StartTimestamp))
        {
            if (listing.StartTimestamp <= now && listing.StopTimestamp > now)
            {
                current ??= listing;
            }
            else if (listing.StartTimestamp > now)
            {
                next ??= listing;
            }
        }

        current ??= listings.FirstOrDefault();
        next ??= listings.FirstOrDefault(listing => listing.StartTimestamp > (current?.StartTimestamp ?? now));

        return new GuideInfo(
            DecodeBase64(current?.Title),
            DecodeBase64(next?.Title),
            DecodeBase64(current?.Description),
            FormatGuideTime(current?.StartTimestamp),
            FormatGuideTime(current?.StopTimestamp),
            DecodeBase64(next?.Description),
            FormatGuideTime(next?.StartTimestamp),
            FormatGuideTime(next?.StopTimestamp));
    }

    private async Task FillMissingGuideFromXmltvAsync(
        Dictionary<string, GuideInfo> results,
        IReadOnlyList<int> liveIds,
        CancellationToken cancellationToken)
    {
        if (!liveIds.Any(streamId => IsMissingGuide(results.GetValueOrDefault($"live-{streamId}"))))
        {
            return;
        }

        var xmltvGuide = await GetXmltvGuideAsync(cancellationToken);
        foreach (var streamId in liveIds)
        {
            var key = $"live-{streamId}";
            if (!IsMissingGuide(results.GetValueOrDefault(key)))
            {
                continue;
            }

            string? epgId;
            string? name;
            lock (_guideLock)
            {
                _epgIdsByLiveStreamId.TryGetValue(streamId, out epgId);
                _namesByLiveStreamId.TryGetValue(streamId, out name);
            }

            if (!string.IsNullOrWhiteSpace(epgId) && xmltvGuide.TryGetValue(epgId, out var guide))
            {
                results[key] = guide;
            }
            else if (!string.IsNullOrWhiteSpace(name) &&
                     xmltvGuide.TryGetValue($"name:{NormalizeGuideName(name)}", out guide))
            {
                results[key] = guide;
            }
        }
    }

    private void FillMissingGuideFromCachedXmltv(
        Dictionary<string, GuideInfo> results,
        IReadOnlyList<int> liveIds)
    {
        IReadOnlyDictionary<string, GuideInfo>? xmltvGuide;
        lock (_guideLock)
        {
            xmltvGuide = _xmltvGuideCache is not null && _xmltvGuideExpires > DateTimeOffset.UtcNow
                ? _xmltvGuideCache
                : null;
        }

        if (xmltvGuide is null)
        {
            return;
        }

        foreach (var streamId in liveIds)
        {
            var key = $"live-{streamId}";
            if (!IsMissingGuide(results.GetValueOrDefault(key)))
            {
                continue;
            }

            string? epgId;
            string? name;
            lock (_guideLock)
            {
                _epgIdsByLiveStreamId.TryGetValue(streamId, out epgId);
                _namesByLiveStreamId.TryGetValue(streamId, out name);
            }

            if (!string.IsNullOrWhiteSpace(epgId) && xmltvGuide.TryGetValue(epgId, out var guide))
            {
                results[key] = guide;
            }
            else if (!string.IsNullOrWhiteSpace(name) &&
                     xmltvGuide.TryGetValue($"name:{NormalizeGuideName(name)}", out guide))
            {
                results[key] = guide;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, GuideInfo>> GetXmltvGuideAsync(CancellationToken cancellationToken)
    {
        var start = Stopwatch.StartNew();

        lock (_guideLock)
        {
            if (_xmltvGuideCache is not null && _xmltvGuideExpires > DateTimeOffset.UtcNow)
            {
                start.Stop();
                LogGuidePerf(
                    "cache",
                    "GetXmltvGuideAsync cache hit in {0}ms. CachedChannels={1}",
                    start.ElapsedMilliseconds,
                    _xmltvGuideCache.Count);
                return _xmltvGuideCache;
            }

            _xmltvGuideLoad ??= LoadXmltvGuideAsync(cancellationToken);
        }

        var guide = await _xmltvGuideLoad;
        lock (_guideLock)
        {
            _xmltvGuideCache = guide;
            _xmltvGuideExpires = DateTimeOffset.UtcNow.AddMinutes(20);
            _xmltvGuideLoad = null;
        }

        start.Stop();
        LogGuidePerf(
            "load",
            "GetXmltvGuideAsync loaded in {0}ms. CachedChannels={1}",
            start.ElapsedMilliseconds,
            guide.Count);

        return guide;
    }

    private async Task<IReadOnlyDictionary<string, GuideInfo>> LoadXmltvGuideAsync(CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;

        var downloadStopwatch = Stopwatch.StartNew();
        await using var stream = await _httpClient.GetStreamAsync(BuildXmltvUrl(), cancellationToken);
        downloadStopwatch.Stop();

        var byChannel = new Dictionary<string, XmltvGuidePair>(StringComparer.OrdinalIgnoreCase);
        var channelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var parseStopwatch = Stopwatch.StartNew();
        var seenProgrammes = 0;
        var keptProgrammes = 0;

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false
        };

        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (string.Equals(reader.Name, "channel", StringComparison.OrdinalIgnoreCase))
            {
                await ReadChannelAliasesAsync(reader, channelAliases, cancellationToken);
                continue;
            }

            if (!string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seenProgrammes++;

            var channelId = reader.GetAttribute("channel");
            var start = ParseXmltvTime(reader.GetAttribute("start"));
            var stop = ParseXmltvTime(reader.GetAttribute("stop"));
            if (string.IsNullOrWhiteSpace(channelId) || start is null || stop is null || stop <= now)
            {
                await SkipElementAsync(reader);
                continue;
            }

            var (title, description) = await ReadProgrammeDetailsAsync(reader, cancellationToken);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            keptProgrammes++;

            byChannel.TryGetValue(channelId, out var pair);
            pair ??= new XmltvGuidePair();
            if (start <= now && stop > now)
            {
                pair = pair with
                {
                    Now = title,
                    NowDescription = description,
                    NowStart = FormatGuideTime(start),
                    NowEnd = FormatGuideTime(stop)
                };
            }
            else if (start > now && (pair.NextStartSort is null || start < pair.NextStartSort))
            {
                pair = pair with
                {
                    Next = title,
                    NextDescription = description,
                    NextStart = FormatGuideTime(start),
                    NextEnd = FormatGuideTime(stop),
                    NextStartSort = start
                };
            }

            byChannel[channelId] = pair;
        }

        parseStopwatch.Stop();

        var guide = byChannel.ToDictionary(
            pair => pair.Key,
            pair => new GuideInfo(
                pair.Value.Now,
                pair.Value.Next,
                pair.Value.NowDescription,
                pair.Value.NowStart,
                pair.Value.NowEnd,
                pair.Value.NextDescription,
                pair.Value.NextStart,
                pair.Value.NextEnd),
            StringComparer.OrdinalIgnoreCase);

        foreach (var alias in channelAliases)
        {
            if (guide.TryGetValue(alias.Value, out var guideInfo))
            {
                guide[$"name:{alias.Key}"] = guideInfo;
            }
        }

        totalStopwatch.Stop();
        LogGuidePerf(
            "xmltv",
            "LoadXmltvGuideAsync done in {0}ms. DownloadMs={1}, ParseMs={2}, ProgrammeMatches={3}, KeptProgrammes={4}, FinalKeys={5}, AliasCount={6}",
            totalStopwatch.ElapsedMilliseconds,
            downloadStopwatch.ElapsedMilliseconds,
            parseStopwatch.ElapsedMilliseconds,
            seenProgrammes,
            keptProgrammes,
            guide.Count,
            channelAliases.Count);

        return guide;
    }

    private static async Task ReadChannelAliasesAsync(
        XmlReader reader,
        Dictionary<string, string> channelAliases,
        CancellationToken cancellationToken)
    {
        var channelId = reader.GetAttribute("id");
        if (string.IsNullOrWhiteSpace(channelId))
        {
            await SkipElementAsync(reader);
            return;
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                string.Equals(reader.Name, "channel", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "display-name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var displayName = (await reader.ReadElementContentAsStringAsync()).Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var normalized = NormalizeGuideName(displayName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                channelAliases[normalized] = channelId;
            }
        }
    }

    private static async Task<(string? Title, string? Description)> ReadProgrammeDetailsAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        string? title = null;
        string? description = null;

        if (reader.IsEmptyElement)
        {
            return (null, null);
        }

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement &&
                string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (string.Equals(reader.Name, "title", StringComparison.OrdinalIgnoreCase))
            {
                title = (await reader.ReadElementContentAsStringAsync()).Trim();
            }
            else if (string.Equals(reader.Name, "desc", StringComparison.OrdinalIgnoreCase))
            {
                description = (await reader.ReadElementContentAsStringAsync()).Trim();
            }
        }

        return (title, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    private static async Task SkipElementAsync(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }
        }
    }

    private static int CountMissingGuide(
        IReadOnlyDictionary<string, GuideInfo> results,
        IReadOnlyList<int> liveIds)
    {
        var missing = 0;
        foreach (var streamId in liveIds)
        {
            if (IsMissingGuide(results.GetValueOrDefault($"live-{streamId}")))
            {
                missing++;
            }
        }

        return missing;
    }

    private bool TryGetCachedShortGuide(int streamId, out GuideInfo guide)
    {
        lock (_guideLock)
        {
            if (!_shortGuideCacheByStreamId.TryGetValue(streamId, out var cached))
            {
                guide = new GuideInfo(null, null);
                return false;
            }

            if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _shortGuideCacheByStreamId.Remove(streamId);
                guide = new GuideInfo(null, null);
                return false;
            }

            guide = cached.Guide;
            return true;
        }
    }

    private void CacheShortGuide(int streamId, GuideInfo guide)
    {
        lock (_guideLock)
        {
            _shortGuideCacheByStreamId[streamId] = new CachedShortGuideEntry(
                guide,
                DateTimeOffset.UtcNow.AddMinutes(ShortGuideCacheMinutes));
        }
    }

    private static void LogGuidePerf(string scope, string message, params object?[] args)
    {
        var formatted = args.Length == 0 ? message : string.Format(message, args);
        var line = $"[XtreamGuide:{scope}] {formatted}";
        Trace.WriteLine(line);
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} {line}");
    }

    private MediaItem CreateLiveItem(
        XtreamLiveStream stream,
        IReadOnlyDictionary<string, string> categoryNames)
    {
        var group = stream.CategoryId is not null && categoryNames.TryGetValue(stream.CategoryId, out var category)
            ? category
            : string.Empty;
        var streamId = stream.StreamId.GetValueOrDefault();
        var url = !string.IsNullOrWhiteSpace(stream.DirectSource) && Uri.TryCreate(stream.DirectSource, UriKind.Absolute, out _)
            ? stream.DirectSource
            : $"{_settings.Origin}/live/{Uri.EscapeDataString(_settings.Username)}/{Uri.EscapeDataString(_settings.Password)}/{streamId}.ts";

        TrackLiveInfo(streamId, stream.EpgChannelId, stream.Name);
        return new MediaItem($"live-{streamId}", MediaKind.Live, stream.Name!, group, url, stream.StreamIcon, stream.EpgChannelId);
    }

    private MediaItem CreateMovieItem(
        XtreamMovieStream stream,
        IReadOnlyDictionary<string, string> categoryNames)
    {
        var group = stream.CategoryId is not null && categoryNames.TryGetValue(stream.CategoryId, out var category)
            ? category
            : string.Empty;
        var extension = string.IsNullOrWhiteSpace(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension.Trim('.');
        var url = $"{_settings.Origin}/movie/{Uri.EscapeDataString(_settings.Username)}/{Uri.EscapeDataString(_settings.Password)}/{stream.StreamId}.{extension}";

        return new MediaItem($"movie-{stream.StreamId}", MediaKind.Movies, stream.Name!, group, url, stream.StreamIcon, null);
    }

    private static bool Matches(
        MediaItem item,
        Regex? queryRegex,
        string? group,
        string? region,
        IReadOnlySet<string>? excludedGroups,
        IReadOnlySet<string>? selectedGroups,
        IReadOnlyDictionary<string, GuideInfo>? guide = null)
    {
        var normalizedGroup = NormalizeGroup(item.Group);

        if (!string.IsNullOrWhiteSpace(group) && !string.Equals(normalizedGroup, NormalizeGroup(group), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selectedGroups is not null && !selectedGroups.Contains(normalizedGroup))
        {
            return false;
        }

        if (excludedGroups is not null && excludedGroups.Contains(normalizedGroup))
        {
            return false;
        }

        if (string.Equals(region, "uk", StringComparison.OrdinalIgnoreCase) && !IsUkItem(item))
        {
            return false;
        }

        return queryRegex is null ||
               SearchMatches($"{item.Name} {item.Group} {item.EpgId}", queryRegex) ||
               GuideMatches(item, queryRegex, guide);
    }

    private static bool GuideMatches(MediaItem item, Regex queryRegex, IReadOnlyDictionary<string, GuideInfo>? guide)
    {
        if (guide is null)
        {
            return false;
        }

        GuideInfo? guideInfo = null;
        if (!string.IsNullOrWhiteSpace(item.EpgId))
        {
            guide.TryGetValue(item.EpgId, out guideInfo);
        }

        if (IsMissingGuide(guideInfo))
        {
            guide.TryGetValue($"name:{NormalizeGuideName(item.Name)}", out guideInfo);
        }

        return guideInfo is not null &&
               SearchMatches($"{guideInfo.NowTitle} {guideInfo.NextTitle}", queryRegex);
    }

    private static bool SearchMatches(string? value, Regex queryRegex)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               queryRegex.IsMatch(value);
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

    private static IReadOnlySet<string>? ToExcludedGroupSet(IReadOnlyCollection<string>? excludedGroups)
    {
        return ToGroupSet(excludedGroups);
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

    private static bool IsUkItem(MediaItem item)
    {
        return HasUkPrefix(item.Name) || HasUkPrefix(item.Group);
    }

    private static bool HasUkPrefix(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("UK ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("UK|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("UK ▎", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("UK:", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackLiveInfo(int streamId, string? epgId, string? name)
    {
        lock (_guideLock)
        {
            if (!string.IsNullOrWhiteSpace(epgId))
            {
                _epgIdsByLiveStreamId[streamId] = epgId;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                _namesByLiveStreamId[streamId] = name;
            }
        }
    }

    private static bool IsMissingGuide(GuideInfo? guide)
    {
        return guide is null ||
               string.IsNullOrWhiteSpace(guide.NowTitle) &&
               string.IsNullOrWhiteSpace(guide.NextTitle);
    }

    private static string? ReadXmlAttribute(string attrs, string name)
    {
        var match = Regex.Match(
            attrs,
            $"{Regex.Escape(name)}\\s*=\\s*[\"'](?<value>.*?)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string? ReadXmlTitle(string body)
    {
        var match = Regex.Match(
            body,
            "<title\\b[^>]*>(?<value>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim()
            : null;
    }

    private static string? ReadXmlDescription(string body)
    {
        var match = Regex.Match(
            body,
            "<desc\\b[^>]*>(?<value>.*?)</desc>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim()
            : null;
    }

    private static IReadOnlyDictionary<string, string> ReadXmltvChannelAliases(string content)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var channelMatches = Regex.Matches(
            content,
            "<channel\\b(?<attrs>[^>]*)>(?<body>.*?)</channel>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in channelMatches)
        {
            var id = ReadXmlAttribute(match.Groups["attrs"].Value, "id");
            var displayName = ReadXmlDisplayName(match.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var normalized = NormalizeGuideName(displayName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                aliases[normalized] = id;
            }
        }

        return aliases;
    }

    private static string? ReadXmlDisplayName(string body)
    {
        var match = Regex.Match(
            body,
            "<display-name\\b[^>]*>(?<value>.*?)</display-name>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim()
            : null;
    }

    private static string NormalizeGuideName(string value)
    {
        var normalized = WebUtility.HtmlDecode(value).ToLowerInvariant();
        normalized = normalized.Replace('▎', ' ').Replace('|', ' ');
        normalized = Regex.Replace(normalized, "\\b(uk|us|ca|ie|in|eu|ar|rl)\\b", " ");
        normalized = Regex.Replace(normalized, "\\b(fhd|uhd|hd|sd|raw|vip|rec)\\b", " ");
        normalized = Regex.Replace(normalized, "[^a-z0-9+]+", " ");
        return Regex.Replace(normalized, "\\s+", " ").Trim();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private string BuildApiUrl(string action)
    {
        return $"{_settings.Origin}/player_api.php?username={Uri.EscapeDataString(_settings.Username)}&password={Uri.EscapeDataString(_settings.Password)}&action={Uri.EscapeDataString(action)}";
    }

    private string BuildApiUrl(string action, params (string Key, string Value)[] parameters)
    {
        var url = BuildApiUrl(action);
        foreach (var parameter in parameters)
        {
            url += $"&{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}";
        }

        return url;
    }

    private string BuildXmltvUrl()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EpgUrl))
        {
            return _settings.EpgUrl;
        }

        return $"{_settings.Origin}/xmltv.php?username={Uri.EscapeDataString(_settings.Username)}&password={Uri.EscapeDataString(_settings.Password)}";
    }

    private static DateTimeOffset? ParseXmltvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14)
        {
            return null;
        }

        var date = value[..14];
        var offset = value[14..].Trim().Replace(" ", string.Empty);
        if (offset.Length == 5 && (offset[0] == '+' || offset[0] == '-'))
        {
            offset = $"{offset[..3]}:{offset[3..]}";
        }

        return DateTimeOffset.TryParseExact(
            $"{date} {offset}",
            "yyyyMMddHHmmss zzz",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var result)
            ? result
            : null;
    }

    private static string? FormatGuideTime(long? timestamp)
    {
        return timestamp is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToLocalTime().ToString("HH:mm")
            : null;
    }

    private static string? FormatGuideTime(DateTimeOffset? timestamp)
    {
        return timestamp?.ToLocalTime().ToString("HH:mm");
    }

    public sealed record MediaPage(IReadOnlyList<MediaItem> Items, int MatchedCount, bool HasMore);

    public sealed record GuideInfo(
        string? NowTitle,
        string? NextTitle,
        string? NowDescription = null,
        string? NowStart = null,
        string? NowEnd = null,
        string? NextDescription = null,
        string? NextStart = null,
        string? NextEnd = null);

    private sealed record XmltvGuidePair(
        string? Now = null,
        string? Next = null,
        string? NowDescription = null,
        string? NowStart = null,
        string? NowEnd = null,
        string? NextDescription = null,
        string? NextStart = null,
        string? NextEnd = null,
        DateTimeOffset? NextStartSort = null);

    private sealed record CachedShortGuideEntry(GuideInfo Guide, DateTimeOffset ExpiresAt);

    private static int? TryParseLiveStreamId(string id)
    {
        return id.StartsWith("live-", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(id["live-".Length..], out var streamId)
            ? streamId
            : null;
    }

    private static string? DecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();
        }
        catch
        {
            return value;
        }
    }

    private sealed class XtreamCategory
    {
        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        [JsonPropertyName("category_name")]
        public string? CategoryName { get; set; }
    }

    private sealed class XtreamLiveStream
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("stream_id")]
        public int? StreamId { get; set; }

        [JsonPropertyName("stream_icon")]
        public string? StreamIcon { get; set; }

        [JsonPropertyName("epg_channel_id")]
        public string? EpgChannelId { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        [JsonPropertyName("direct_source")]
        public string? DirectSource { get; set; }
    }

    private sealed class XtreamMovieStream
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("stream_id")]
        public int? StreamId { get; set; }

        [JsonPropertyName("stream_icon")]
        public string? StreamIcon { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        [JsonPropertyName("container_extension")]
        public string? ContainerExtension { get; set; }
    }

    private sealed class ShortEpgResponse
    {
        [JsonPropertyName("epg_listings")]
        public List<EpgListing>? Listings { get; set; }
    }

    private sealed class EpgListing
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("start_timestamp")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long StartTimestamp { get; set; }

        [JsonPropertyName("stop_timestamp")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long StopTimestamp { get; set; }
    }
}
