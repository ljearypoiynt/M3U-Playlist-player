using System.Collections.Concurrent;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _sessionLifetime = TimeSpan.FromHours(12);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    public PlaybackSession CreateSession(string? deviceName, XtreamSettings settings, string? playbackMode)
    {
        CleanupExpiredSessions();

        var now = DateTimeOffset.Now;
        var id = Guid.NewGuid().ToString("D");
        var session = new PlaybackSession(id, deviceName ?? "LG TV", settings, playbackMode ?? "hls", now, _sessionLifetime);
        _sessions[id] = session;
        return session;
    }

    public bool TryGetSession(string id, out PlaybackSession session)
    {
        CleanupExpiredSessions();

        if (_sessions.TryGetValue(id, out session!))
        {
            session.Touch(DateTimeOffset.Now, _sessionLifetime);
            return true;
        }

        session = null!;
        return false;
    }

    public IReadOnlyList<PlaybackSession> GetSessions()
    {
        CleanupExpiredSessions();
        return _sessions.Values
            .OrderByDescending(session => session.LastSeenAt)
            .ToArray();
    }

    public void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastCleanup < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanup = now;
        foreach (var session in _sessions.Values)
        {
            if (session.ExpiresAt <= now && _sessions.TryRemove(session.Id, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }
}
