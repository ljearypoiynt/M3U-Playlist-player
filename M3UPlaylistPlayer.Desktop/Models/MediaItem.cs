namespace M3UPlaylistPlayer.Desktop.Models;

public sealed record MediaItem(
    string Id,
    MediaKind Kind,
    string Name,
    string Group,
    string Url,
    string? Icon,
    string? EpgId)
{
    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Group) ? Name : $"{Name}  |  {Group}";
    }
}
