using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using M3UPlaylistPlayer.Models;
using M3UPlaylistPlayer.Options;

namespace M3UPlaylistPlayer.Services;

public sealed class XmlTvGuideService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<XmlTvGuideService> logger,
    IOptions<PlaylistOptions> options) : IGuideService
{
    public async Task<IReadOnlyDictionary<string, StreamGuide>> GetGuideAsync(
        string sourceUrl,
        IReadOnlyCollection<string> channelIds,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();

        var normalizedIds = channelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        logger.LogInformation(
            "Guide request started. RequestedChannels={RequestedChannels}, DistinctChannels={DistinctChannels}, Refresh={Refresh}",
            channelIds.Count,
            normalizedIds.Length,
            refresh);

        if (normalizedIds.Length == 0)
        {
            totalStopwatch.Stop();
            logger.LogInformation("Guide request ended with no channel ids. TotalMs={TotalMs}", totalStopwatch.ElapsedMilliseconds);
            return new Dictionary<string, StreamGuide>(StringComparer.OrdinalIgnoreCase);
        }

        var urlStopwatch = Stopwatch.StartNew();
        var guideUrl = BuildGuideUrl(sourceUrl);
        urlStopwatch.Stop();
        logger.LogInformation("Guide URL built in {BuildUrlMs}ms. GuideUrl={GuideUrl}", urlStopwatch.ElapsedMilliseconds, guideUrl);

        var cacheKey = $"guide::{guideUrl}";
        if (!refresh &&
            memoryCache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, StreamGuide>? cached) &&
            cached is not null)
        {
            var filterStopwatch = Stopwatch.StartNew();
            var filteredFromCache = FilterGuide(cached, normalizedIds);
            filterStopwatch.Stop();
            totalStopwatch.Stop();

            logger.LogInformation(
                "Guide cache hit. CachedChannels={CachedChannels}, ReturnedChannels={ReturnedChannels}, FilterMs={FilterMs}, TotalMs={TotalMs}",
                cached.Count,
                filteredFromCache.Count,
                filterStopwatch.ElapsedMilliseconds,
                totalStopwatch.ElapsedMilliseconds);

            return filteredFromCache;
        }

        logger.LogInformation("Guide cache miss. Downloading XMLTV.");
        using var client = httpClientFactory.CreateClient(nameof(PlaylistService));

        var fetchStopwatch = Stopwatch.StartNew();
        var content = await client.GetStringAsync(guideUrl, cancellationToken);
        fetchStopwatch.Stop();
        logger.LogInformation("Guide download completed in {FetchMs}ms. PayloadChars={PayloadChars}", fetchStopwatch.ElapsedMilliseconds, content.Length);

        var sanitizeStopwatch = Stopwatch.StartNew();
        var sanitized = SanitizeXmlTv(content);
        sanitizeStopwatch.Stop();
        logger.LogInformation("Guide sanitization completed in {SanitizeMs}ms. SanitizedChars={SanitizedChars}", sanitizeStopwatch.ElapsedMilliseconds, sanitized.Length);

        var parseStopwatch = Stopwatch.StartNew();
        var guide = await ParseGuideAsync(sanitized, normalizedIds, cancellationToken);
        parseStopwatch.Stop();

        memoryCache.Set(cacheKey, guide, TimeSpan.FromMinutes(Math.Max(1, options.Value.GuideCacheMinutes)));
        totalStopwatch.Stop();

        logger.LogInformation(
            "Guide parse and cache completed. ParsedChannels={ParsedChannels}, ParseMs={ParseMs}, TotalMs={TotalMs}",
            guide.Count,
            parseStopwatch.ElapsedMilliseconds,
            totalStopwatch.ElapsedMilliseconds);

        return guide;
    }

    private static IReadOnlyDictionary<string, StreamGuide> FilterGuide(
        IReadOnlyDictionary<string, StreamGuide> guide,
        IReadOnlyCollection<string> channelIds)
    {
        return channelIds
            .Where(guide.ContainsKey)
            .ToDictionary(id => id, id => guide[id], StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, StreamGuide>> ParseGuideAsync(
        string content,
        IReadOnlyCollection<string> channelIds,
        CancellationToken cancellationToken)
    {
        var parseStopwatch = Stopwatch.StartNew();
        var wanted = channelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;
        var latestStop = now.AddHours(-6);
        var horizon = now.AddHours(18);
        var results = new Dictionary<string, StreamGuide>(StringComparer.OrdinalIgnoreCase);
        var programmeElementsSeen = 0;
        var programmesForWantedChannels = 0;
        var programmesInWindow = 0;
        var programmesParsedWithTitle = 0;

        var settings = new XmlReaderSettings
        {
            Async = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var textReader = new StringReader(content);
        using var reader = XmlReader.Create(textReader, settings);
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            programmeElementsSeen++;

            var channelId = reader.GetAttribute("channel");
            if (string.IsNullOrWhiteSpace(channelId) || !wanted.Contains(channelId))
            {
                continue;
            }

            programmesForWantedChannels++;

            var start = ParseXmlTvTime(reader.GetAttribute("start"));
            var stop = ParseXmlTvTime(reader.GetAttribute("stop"));
            if (start is null || stop is null || stop <= latestStop || start > horizon)
            {
                continue;
            }

            programmesInWindow++;

            var programme = await ReadProgrammeAsync(reader, channelId, start.Value, stop.Value);
            if (programme is null)
            {
                continue;
            }

            programmesParsedWithTitle++;

            results.TryGetValue(channelId, out var existing);
            existing ??= new StreamGuide(null, null);
            if (programme.IsOnNow(now))
            {
                results[channelId] = existing with { Now = PickEarlier(existing?.Now, programme) };
            }
            else if (programme.Start > now)
            {
                results[channelId] = existing with { Next = PickEarlier(existing?.Next, programme) };
            }
        }

        parseStopwatch.Stop();
        logger.LogInformation(
            "Guide XML parsed. WantedChannels={WantedChannels}, ProgrammeElementsSeen={ProgrammeElementsSeen}, ProgrammesForWantedChannels={ProgrammesForWantedChannels}, ProgrammesInWindow={ProgrammesInWindow}, ProgrammesWithTitle={ProgrammesWithTitle}, ResultChannels={ResultChannels}, ParseLoopMs={ParseLoopMs}",
            wanted.Count,
            programmeElementsSeen,
            programmesForWantedChannels,
            programmesInWindow,
            programmesParsedWithTitle,
            results.Count,
            parseStopwatch.ElapsedMilliseconds);

        return results;
    }

    private static async Task<GuideProgramme?> ReadProgrammeAsync(
        XmlReader reader,
        string channelId,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        var title = string.Empty;
        string? description = null;

        if (reader.IsEmptyElement)
        {
            return null;
        }

        while (await reader.ReadAsync())
        {
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

        return string.IsNullOrWhiteSpace(title)
            ? null
            : new GuideProgramme(channelId, title, string.IsNullOrWhiteSpace(description) ? null : description, start, stop);
    }

    private static GuideProgramme PickEarlier(GuideProgramme? current, GuideProgramme candidate)
    {
        return current is null || candidate.Start < current.Start ? candidate : current;
    }

    private static DateTimeOffset? ParseXmlTvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length >= 5)
        {
            var offsetStart = normalized.Length - 5;
            if ((normalized[offsetStart] == '+' || normalized[offsetStart] == '-') &&
                normalized[offsetStart + 3] != ':')
            {
                normalized = normalized.Insert(offsetStart + 3, ":");
            }
        }

        string[] formats =
        [
            "yyyyMMddHHmmss zzz",
            "yyyyMMddHHmmsszzz",
            "yyyyMMddHHmmss"
        ];

        return DateTimeOffset.TryParseExact(
            normalized,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string BuildGuideUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Enter a valid playlist URL before loading the guide.");
        }

        var query = uri.Query.TrimStart('?');
        var origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        return $"{origin}/xmltv.php?{query}";
    }

    private static string SanitizeXmlTv(string content)
    {
        var sanitized = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (sanitized.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var end = sanitized.IndexOf("?>", StringComparison.Ordinal);
            if (end >= 0)
            {
                sanitized = sanitized[(end + 2)..].TrimStart();
            }
        }

        if (sanitized.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var end = sanitized.IndexOf('>');
            if (end >= 0)
            {
                sanitized = sanitized[(end + 1)..].TrimStart();
            }
        }

        return sanitized;
    }
}
