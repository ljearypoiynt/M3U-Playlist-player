using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public interface IGuideService
{
    Task<IReadOnlyDictionary<string, StreamGuide>> GetGuideAsync(
        string sourceUrl,
        IReadOnlyCollection<string> channelIds,
        bool refresh,
        CancellationToken cancellationToken);
}
