namespace M3UPlaylistPlayer.Models;

public sealed record M3UPlaylistParseResult(
    IReadOnlyList<PlaylistEntry> Entries,
    string? GuideUrl);
