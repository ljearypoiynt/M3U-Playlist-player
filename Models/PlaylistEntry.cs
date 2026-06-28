namespace M3UPlaylistPlayer.Models;

public sealed record PlaylistEntry(
    string Id,
    string Name,
    string Url,
    string BrowserUrl,
    string GroupTitle,
    string? TvgId,
    string? TvgLogo,
    string? Country,
    bool IsUk);
