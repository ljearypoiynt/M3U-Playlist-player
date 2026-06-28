using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public interface IStreamLauncher
{
    Task<LaunchResult> LaunchAsync(PlaylistEntry entry, CancellationToken cancellationToken);
}

public sealed record LaunchResult(bool Success, string Message);
