namespace M3UPlaylistPlayer.Options;

public sealed class PlaylistOptions
{
    public string DefaultUrl { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int CacheMinutes { get; set; } = 15;
    public int GuideCacheMinutes { get; set; } = 60;

    public string GetDefaultSourceUrl()
    {
        if (!string.IsNullOrWhiteSpace(DefaultUrl))
        {
            return DefaultUrl;
        }

        if (string.IsNullOrWhiteSpace(Host) ||
            string.IsNullOrWhiteSpace(Username) ||
            string.IsNullOrWhiteSpace(Password))
        {
            return string.Empty;
        }

        var host = Host.Trim().TrimEnd('/');
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = $"http://{host}";
        }

        return $"{host}/get.php?username={Uri.EscapeDataString(Username.Trim())}&password={Uri.EscapeDataString(Password.Trim())}&type=m3u_plus&output=mpegts";
    }
}
