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
        var ffmpeg = StartFfmpeg(item.Url, directory, playlistPath);
        var session = new HlsSession(sessionId, directory, playlistPath, ffmpeg.Process, ffmpeg.ErrorLines);
        _sessions[sessionId] = session;

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[HLS:{sessionId}] Starting FFmpeg. Item=\"{TruncateForLog(item.Name, 80)}\" Source={DescribeUrl(item.Url)}");
        try
        {
            await WaitForPlaylistAsync(session, cancellationToken);
            Console.WriteLine($"[HLS:{sessionId}] Playlist ready in {stopwatch.ElapsedMilliseconds}ms.");
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[HLS:{sessionId}] Playlist failed after {stopwatch.ElapsedMilliseconds}ms. {ex.GetType().Name}: {ex.Message}");
            Stop(sessionId);
            throw;
        }
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

    private static FfmpegProcess StartFfmpeg(string inputUrl, string directory, string playlistPath)
    {
        var segmentPath = Path.Combine(directory, "segment_%05d.ts");
        var arguments = string.Join(" ", [
            "-hide_banner",
            "-loglevel warning",
            "-nostdin",
            "-reconnect 1",
            "-reconnect_streamed 1",
            "-reconnect_delay_max 2",
            "-i", Quote(inputUrl),
            "-map 0:v:0?",
            "-map 0:a:0?",
            "-c copy",
            "-f hls",
            "-hls_time 3",
            "-hls_list_size 8",
            "-hls_flags delete_segments+omit_endlist",
            "-hls_segment_filename", Quote(segmentPath),
            Quote(playlistPath)
        ]);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false
        };

        var errorLines = new ConcurrentQueue<string>();
        var process = new Process
        {
            StartInfo = startInfo
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            errorLines.Enqueue(args.Data);
            while (errorLines.Count > 20 && errorLines.TryDequeue(out string? _))
            {
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg could not be started.");
        }

        process.BeginErrorReadLine();
        return new FfmpegProcess(process, errorLines);
    }

    private static async Task WaitForPlaylistAsync(HlsSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(30);
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
            {
                throw new InvalidOperationException($"FFmpeg stopped before creating playback. {GetRecentError(session)}".Trim());
            }

            if (File.Exists(session.PlaylistPath) &&
                new FileInfo(session.PlaylistPath).Length > 0 &&
                HasPlayableSegment(session.Directory))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Timed out while preparing TV playback. {GetRecentError(session)}".Trim());
    }

    private static bool HasPlayableSegment(string directory)
    {
        return Directory.EnumerateFiles(directory, "segment_*.ts")
            .Any(path => new FileInfo(path).Length > 0);
    }

    private static string GetRecentError(HlsSession session)
    {
        var error = string.Join(" ", session.ErrorLines.ToArray().TakeLast(6));
        return string.IsNullOrWhiteSpace(error)
            ? "FFmpeg did not report any error output."
            : RedactSensitiveText(error);
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

    private static string DescribeUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}"
            : "unknown";
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Untitled";
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string RedactSensitiveText(string value)
    {
        var redacted = value;
        redacted = System.Text.RegularExpressions.Regex.Replace(redacted, "([?&](?:username|password)=)[^&\\s]+", "$1redacted", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        redacted = System.Text.RegularExpressions.Regex.Replace(redacted, "(/(?:live|movie)/)[^/\\s]+/[^/\\s]+/", "$1redacted/redacted/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return redacted;
    }

    private sealed record FfmpegProcess(Process Process, ConcurrentQueue<string> ErrorLines);
}

public sealed record HlsSession(
    string Id,
    string Directory,
    string PlaylistPath,
    Process Process,
    ConcurrentQueue<string> ErrorLines);
