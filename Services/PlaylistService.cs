using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using M3UPlaylistPlayer.Models;
using M3UPlaylistPlayer.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace M3UPlaylistPlayer.Services;

public sealed class PlaylistService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    IOptions<PlaylistOptions> options,
    M3UPlaylistParser parser) : IPlaylistService
{
    public async Task<PlaylistResult> GetPlaylistAsync(string sourceUrl, bool refresh, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Enter a valid http or https M3U playlist URL.");
        }

        var cacheKey = $"playlist::{sourceUrl}";
        if (!refresh && memoryCache.TryGetValue(cacheKey, out PlaylistResult? cached) && cached is not null)
        {
            return cached;
        }

        using var client = httpClientFactory.CreateClient(nameof(PlaylistService));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsedPlaylist = parser.ParseWithMetadata(content);
        var entries = parsedPlaylist.Entries;
        var result = entries.Count == 0 && TryGetXtreamCredentials(uri, out var xtream)
            ? await GetXtreamLiveStreamsAsync(client, xtream, sourceUrl, cancellationToken)
            : new PlaylistResult(entries, DateTimeOffset.Now, sourceUrl, GuideUrl: parsedPlaylist.GuideUrl);

        memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, options.Value.CacheMinutes)));
        return result;
    }

    public async Task<PlaylistEntry?> FindEntryAsync(string sourceUrl, string id, CancellationToken cancellationToken)
    {
        var playlist = await GetPlaylistAsync(sourceUrl, refresh: false, cancellationToken);
        return playlist.Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetXtreamCredentials(Uri sourceUri, out XtreamCredentials credentials)
    {
        credentials = default;

        if (!sourceUri.AbsolutePath.EndsWith("/get.php", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(sourceUri.Query);
        if (!query.TryGetValue("username", out var username) ||
            !query.TryGetValue("password", out var password))
        {
            return false;
        }

        query.TryGetValue("output", out var output);
        var extension = string.Equals(output, "m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
        var origin = sourceUri.IsDefaultPort
            ? $"{sourceUri.Scheme}://{sourceUri.Host}"
            : $"{sourceUri.Scheme}://{sourceUri.Host}:{sourceUri.Port}";

        credentials = new XtreamCredentials(origin, username, password, extension);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<PlaylistResult> GetXtreamLiveStreamsAsync(
        HttpClient client,
        XtreamCredentials credentials,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var categoriesUrl = BuildPlayerApiUrl(credentials, "get_live_categories");
        var streamsUrl = BuildPlayerApiUrl(credentials, "get_live_streams");

        var categories = await GetJsonAsync<List<XtreamCategory>>(client, categoriesUrl, cancellationToken) ?? [];
        var streams = await GetJsonAsync<List<XtreamLiveStream>>(client, streamsUrl, cancellationToken) ?? [];
        var categoryNames = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.CategoryId))
            .GroupBy(category => category.CategoryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().CategoryName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var entries = streams
            .Where(stream => stream.StreamId is not null && !string.IsNullOrWhiteSpace(stream.Name))
            .Select(stream =>
            {
                var group = stream.CategoryId is not null && categoryNames.TryGetValue(stream.CategoryId, out var categoryName)
                    ? categoryName
                    : string.Empty;
                var url = !string.IsNullOrWhiteSpace(stream.DirectSource) && Uri.TryCreate(stream.DirectSource, UriKind.Absolute, out _)
                    ? stream.DirectSource
                    : $"{credentials.Origin}/live/{Uri.EscapeDataString(credentials.Username)}/{Uri.EscapeDataString(credentials.Password)}/{stream.StreamId}.{credentials.Extension}";
                var browserUrl = !string.IsNullOrWhiteSpace(stream.DirectSource) && Uri.TryCreate(stream.DirectSource, UriKind.Absolute, out _)
                    ? stream.DirectSource
                    : $"{credentials.Origin}/live/{Uri.EscapeDataString(credentials.Username)}/{Uri.EscapeDataString(credentials.Password)}/{stream.StreamId}.m3u8";
                var country = M3UPlaylistParser.DetectCountry(stream.Name!, group);

                return new PlaylistEntry(
                    M3UPlaylistParser.CreateStableId(stream.Name!, url),
                    stream.Name!,
                    url,
                    browserUrl,
                    group,
                    stream.EpgChannelId,
                    stream.StreamIcon,
                    country,
                    string.Equals(country, "UK", StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();

        return new PlaylistResult(entries, DateTimeOffset.Now, sourceUrl, "Xtream API");
    }

    private static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken cancellationToken)
    {
        await using var stream = await client.GetStreamAsync(url, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static string BuildPlayerApiUrl(XtreamCredentials credentials, string action)
    {
        return $"{credentials.Origin}/player_api.php?username={Uri.EscapeDataString(credentials.Username)}&password={Uri.EscapeDataString(credentials.Password)}&action={Uri.EscapeDataString(action)}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly record struct XtreamCredentials(string Origin, string Username, string Password, string Extension);

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
}
