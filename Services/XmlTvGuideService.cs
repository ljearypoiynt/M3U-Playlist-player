using System.Globalization;
using System.IO;
using System.Xml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using M3UPlaylistPlayer.Models;
using M3UPlaylistPlayer.Options;

namespace M3UPlaylistPlayer.Services;

public sealed class XmlTvGuideService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    IOptions<PlaylistOptions> options) : IGuideService
{
    public async Task<IReadOnlyDictionary<string, StreamGuide>> GetGuideAsync(
        string sourceUrl,
        IReadOnlyCollection<string> channelIds,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var normalizedIds = channelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedIds.Length == 0)
        {
            return new Dictionary<string, StreamGuide>(StringComparer.OrdinalIgnoreCase);
        }

        var guideUrl = BuildGuideUrl(sourceUrl);
        var cacheKey = $"guide::{guideUrl}";
        if (!refresh &&
            memoryCache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, StreamGuide>? cached) &&
            cached is not null)
        {
            return FilterGuide(cached, normalizedIds);
        }

        using var client = httpClientFactory.CreateClient(nameof(PlaylistService));
        var content = await client.GetStringAsync(guideUrl, cancellationToken);
        var guide = await ParseGuideAsync(SanitizeXmlTv(content), normalizedIds, cancellationToken);
        memoryCache.Set(cacheKey, guide, TimeSpan.FromMinutes(Math.Max(1, options.Value.GuideCacheMinutes)));

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

    private static async Task<IReadOnlyDictionary<string, StreamGuide>> ParseGuideAsync(
        string content,
        IReadOnlyCollection<string> channelIds,
        CancellationToken cancellationToken)
    {
        var wanted = channelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;
        var latestStop = now.AddHours(-6);
        var horizon = now.AddHours(18);
        var results = new Dictionary<string, StreamGuide>(StringComparer.OrdinalIgnoreCase);

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

            var channelId = reader.GetAttribute("channel");
            if (string.IsNullOrWhiteSpace(channelId) || !wanted.Contains(channelId))
            {
                continue;
            }

            var start = ParseXmlTvTime(reader.GetAttribute("start"));
            var stop = ParseXmlTvTime(reader.GetAttribute("stop"));
            if (start is null || stop is null || stop <= latestStop || start > horizon)
            {
                continue;
            }

            var programme = await ReadProgrammeAsync(reader, channelId, start.Value, stop.Value);
            if (programme is null)
            {
                continue;
            }

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
