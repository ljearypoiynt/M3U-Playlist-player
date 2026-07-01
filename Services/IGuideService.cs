using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public interface IGuideService
{
    Task<IReadOnlyDictionary<string, StreamGuide>> GetGuideAsync(
        string sourceUrl,
        string? guideUrl,
        IReadOnlyCollection<string> channelIds,
        bool refresh,
        CancellationToken cancellationToken);
}
