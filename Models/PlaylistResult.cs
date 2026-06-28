namespace M3UPlaylistPlayer.Models;

public sealed record PlaylistResult(
    IReadOnlyList<PlaylistEntry> Entries,
    DateTimeOffset DownloadedAt,
    string SourceUrl,
    string SourceKind = "M3U");
