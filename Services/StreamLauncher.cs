using System.ComponentModel;
using System.Diagnostics;
using M3UPlaylistPlayer.Models;

namespace M3UPlaylistPlayer.Services;

public sealed class StreamLauncher(IConfiguration configuration) : IStreamLauncher
{
    public Task<LaunchResult> LaunchAsync(PlaylistEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuredPlayer = configuration["Player:Command"];
        if (!string.IsNullOrWhiteSpace(configuredPlayer))
        {
            return TryStart(configuredPlayer, [entry.Url], out var configuredMessage)
                ? Task.FromResult(new LaunchResult(true, $"Opened {entry.Name} with {configuredPlayer}."))
                : Task.FromResult(new LaunchResult(false, $"Could not start the configured player '{configuredPlayer}': {configuredMessage}"));
        }

        foreach (var candidate in GetPlayerCandidates(entry.Url))
        {
            if (TryStart(candidate.FileName, candidate.Arguments, out _))
            {
                return Task.FromResult(new LaunchResult(true, $"Opened {entry.Name} with {candidate.DisplayName}."));
            }
        }

        return Task.FromResult(new LaunchResult(
            false,
            $"Could not find VLC or mpv on this laptop. Install VLC from videolan.org, then refresh this app. If VLC is installed somewhere custom, set Player:Command in appsettings.json. Stream URL: {entry.Url}"));
    }

    private static IEnumerable<PlayerCandidate> GetPlayerCandidates(string streamUrl)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in GetWindowsVlcPaths())
            {
                yield return new PlayerCandidate("VLC", path, [streamUrl]);
            }

            yield return new PlayerCandidate("VLC", "vlc", [streamUrl]);
            yield return new PlayerCandidate("mpv", "mpv", [streamUrl]);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return new PlayerCandidate("VLC", "/Applications/VLC.app/Contents/MacOS/VLC", [streamUrl]);
            yield return new PlayerCandidate("VLC", "vlc", [streamUrl]);
            yield return new PlayerCandidate("mpv", "mpv", [streamUrl]);
            yield break;
        }

        yield return new PlayerCandidate("VLC", "vlc", [streamUrl]);
        yield return new PlayerCandidate("mpv", "mpv", [streamUrl]);
    }

    private static IEnumerable<string> GetWindowsVlcPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "VideoLAN", "VLC", "vlc.exe");
        }
    }

    private static bool TryStart(string fileName, IReadOnlyList<string> arguments, out string message)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = arguments.Count == 0
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            message = "Started.";
            return true;
        }
        catch (Win32Exception ex)
        {
            message = ex.Message;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private sealed record PlayerCandidate(string DisplayName, string FileName, IReadOnlyList<string> Arguments);
}
