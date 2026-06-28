using LibVLCSharp.Shared;

namespace M3UPlaylistPlayer.Desktop.Models;

public sealed record CastTarget(string Name, string Source, RendererItem? Renderer)
{
    public bool IsLocal => Renderer is null;

    public override string ToString()
    {
        return IsLocal ? Name : $"{Name} ({Source})";
    }
}
