using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public sealed partial class M3UPlaylistParser
{
    public IReadOnlyList<PlaylistEntry> Parse(string content)
    {
        var entries = new List<PlaylistEntry>();
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        ExtInf? pending = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pending = ParseExtInf(line);
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out _))
            {
                pending = null;
                continue;
            }

            var name = pending?.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = line;
            }

            var group = pending?.GroupTitle ?? string.Empty;
            var country = DetectCountry(name, group);
            entries.Add(new PlaylistEntry(
                CreateStableId(name, line),
                name,
                line,
                GetBrowserPlaybackUrl(line),
                group,
                pending?.TvgId,
                pending?.TvgLogo,
                country,
                string.Equals(country, "UK", StringComparison.OrdinalIgnoreCase)));

            pending = null;
        }

        return entries;
    }

    private static ExtInf ParseExtInf(string line)
    {
        var attributes = AttributeRegex()
            .Matches(line)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.OrdinalIgnoreCase);

        var commaIndex = line.IndexOf(',');
        var fallbackName = commaIndex >= 0 && commaIndex + 1 < line.Length ? line[(commaIndex + 1)..].Trim() : string.Empty;

        attributes.TryGetValue("tvg-name", out var tvgName);
        attributes.TryGetValue("tvg-id", out var tvgId);
        attributes.TryGetValue("tvg-logo", out var tvgLogo);
        attributes.TryGetValue("group-title", out var groupTitle);

        return new ExtInf(
            string.IsNullOrWhiteSpace(tvgName) ? fallbackName : tvgName.Trim(),
            string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim(),
            string.IsNullOrWhiteSpace(tvgLogo) ? null : tvgLogo.Trim(),
            string.IsNullOrWhiteSpace(groupTitle) ? string.Empty : groupTitle.Trim());
    }

    public static string? DetectCountry(string name, string group)
    {
        var text = $"{group} {name}";
        if (UkRegex().IsMatch(text))
        {
            return "UK";
        }

        if (text.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("England", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Scotland", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wales", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Northern Ireland", StringComparison.OrdinalIgnoreCase))
        {
            return "UK";
        }

        return null;
    }

    public static string CreateStableId(string name, string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}|{url}"));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static string GetBrowserPlaybackUrl(string url)
    {
        return url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? url[..^3] + ".m3u8"
            : url;
    }

    [GeneratedRegex("([\\w-]+)=\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("(^|[^a-zA-Z])UK([^a-zA-Z]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UkRegex();

    private sealed record ExtInf(string DisplayName, string? TvgId, string? TvgLogo, string GroupTitle);
}
