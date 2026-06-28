using System.Collections.Concurrent;
using System.Diagnostics;
using M3UPlaylistPlayer.Desktop.Models;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class HlsStreamService : IDisposable
{
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rootDirectory;

    public HlsStreamService(string? workspaceName = null)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(workspaceName)
            ? Path.Combine(Path.GetTempPath(), "M3UPlaylistPlayer", "hls")
            : Path.Combine(Path.GetTempPath(), "M3UPlaylistPlayer", "hls", SanitizeSessionId(workspaceName));
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task<HlsSession> StartAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var sessionId = SanitizeSessionId(item.Id);
        if (_sessions.TryGetValue(sessionId, out var existing) && !existing.Process.HasExited)
        {
            return existing;
        }

        Stop(sessionId);

        var directory = Path.Combine(_rootDirectory, sessionId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        var playlistPath = Path.Combine(directory, "index.m3u8");
        var process = StartFfmpeg(item.Url, directory, playlistPath);
        var session = new HlsSession(sessionId, directory, playlistPath, process);
        _sessions[sessionId] = session;

        await WaitForPlaylistAsync(session, cancellationToken);
        return session;
    }

    public bool TryGetFile(string sessionId, string fileName, out string path, out string contentType)
    {
        path = string.Empty;
        contentType = "application/octet-stream";

        if (!_sessions.TryGetValue(SanitizeSessionId(sessionId), out var session))
        {
            return false;
        }

        var safeFileName = Path.GetFileName(fileName);
        path = Path.Combine(session.Directory, safeFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        contentType = Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.apple.mpegurl"
            : "video/mp2t";

        return true;
    }

    public void Dispose()
    {
        foreach (var sessionId in _sessions.Keys)
        {
            Stop(sessionId);
        }
    }

    public bool Stop(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        session.Process.Dispose();
        return true;
    }

    public void StopAll()
    {
        foreach (var sessionId in _sessions.Keys)
        {
            Stop(sessionId);
        }
    }

    private static Process StartFfmpeg(string inputUrl, string directory, string playlistPath)
    {
        var segmentPath = Path.Combine(directory, "segment_%05d.ts");
        var arguments = string.Join(" ", [
            "-hide_banner",
            "-loglevel warning",
            "-reconnect 1",
            "-reconnect_streamed 1",
            "-reconnect_delay_max 2",
            "-i", Quote(inputUrl),
            "-c:v libx264",
            "-preset veryfast",
            "-tune zerolatency",
            "-profile:v main",
            "-pix_fmt yuv420p",
            "-c:a aac",
            "-b:a 128k",
            "-ac 2",
            "-f hls",
            "-hls_time 3",
            "-hls_list_size 8",
            "-hls_flags delete_segments+append_list+omit_endlist",
            "-hls_segment_filename", Quote(segmentPath),
            Quote(playlistPath)
        ]);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg could not be started.");
    }

    private static async Task WaitForPlaylistAsync(HlsSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(20);
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
            {
                var error = await session.Process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"FFmpeg stopped before creating playback. {error}".Trim());
            }

            if (File.Exists(session.PlaylistPath) && new FileInfo(session.PlaylistPath).Length > 0)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out while preparing TV playback.");
    }

    private static string SanitizeSessionId(string value)
    {
        var safe = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());

        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed record HlsSession(string Id, string Directory, string PlaylistPath, Process Process);
