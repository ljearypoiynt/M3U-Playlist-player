namespace M3UPlaylistPlayer.Desktop.Services;

public sealed record XtreamSettings(string Host, string Username, string Password, string? EpgUrl = null)
{
    public string Origin
    {
        get
        {
            var host = Host.Trim().TrimEnd('/');
            return host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host
                : $"http://{host}";
        }
    }

    public string SourceHost
    {
        get
        {
            return Uri.TryCreate(Origin, UriKind.Absolute, out var uri)
                ? uri.Host
                : Host.Trim();
        }
    }

    public static bool TryFromPlaylistUrl(string? playlistUrl, string? epgUrl, out XtreamSettings settings, out string error)
    {
        settings = new XtreamSettings(string.Empty, string.Empty, string.Empty);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            error = "Playlist URL is required.";
            return false;
        }

        if (!Uri.TryCreate(playlistUrl.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Playlist URL is not a valid absolute URL.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        query.TryGetValue("username", out var username);
        query.TryGetValue("password", out var password);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            error = "Playlist URL must include username and password query parameters.";
            return false;
        }

        string? safeEpgUrl = null;
        if (!string.IsNullOrWhiteSpace(epgUrl))
        {
            if (!Uri.TryCreate(epgUrl.Trim(), UriKind.Absolute, out _))
            {
                error = "EPG URL is not a valid absolute URL.";
                return false;
            }

            safeEpgUrl = epgUrl.Trim();
        }

        settings = new XtreamSettings(uri.GetLeftPart(UriPartial.Authority), username, password, safeEpgUrl);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return values;
        }

        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }
}
