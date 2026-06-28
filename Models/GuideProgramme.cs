namespace M3UPlaylistPlayer.Models;

public sealed record GuideProgramme(
    string ChannelId,
    string Title,
    string? Description,
    DateTimeOffset Start,
    DateTimeOffset Stop)
{
    public bool IsOnNow(DateTimeOffset now) => Start <= now && Stop > now;
}
