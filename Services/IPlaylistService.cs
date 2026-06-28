using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public interface IPlaylistService
{
    Task<PlaylistResult> GetPlaylistAsync(string sourceUrl, bool refresh, CancellationToken cancellationToken);
    Task<PlaylistEntry?> FindEntryAsync(string sourceUrl, string id, CancellationToken cancellationToken);
}
