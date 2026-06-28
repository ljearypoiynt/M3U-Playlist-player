using System.Collections.Concurrent;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class SetupLinkManager
{
    private readonly ConcurrentDictionary<string, SetupLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(30);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    public SetupLink Create(string? deviceName, DateTimeOffset now)
    {
        CleanupExpired(now);

        var link = new SetupLink(
            Guid.NewGuid().ToString("D"),
            string.IsNullOrWhiteSpace(deviceName) ? "LG TV" : deviceName.Trim(),
            now,
            now.Add(_lifetime));
        _links[link.Id] = link;
        return link;
    }

    public bool TryGet(string id, out SetupLink link)
    {
        CleanupExpired(DateTimeOffset.Now);
        return _links.TryGetValue(id, out link!);
    }

    public bool Submit(string id, SetupConfiguration configuration)
    {
        if (!TryGet(id, out var link))
        {
            return false;
        }

        link.Submit(configuration, DateTimeOffset.Now);
        return true;
    }

    public bool AttachSession(string id, SetupSession session)
    {
        if (!TryGet(id, out var link))
        {
            return false;
        }

        link.AttachSession(session, DateTimeOffset.Now);
        return true;
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        if (now - _lastCleanup < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanup = now;
        foreach (var link in _links.Values)
        {
            if (link.ExpiresAt <= now)
            {
                _links.TryRemove(link.Id, out _);
            }
        }
    }
}

public sealed class SetupLink
{
    private readonly object _lock = new();
    private SetupConfiguration? _configuration;
    private SetupSession? _session;

    public SetupLink(string id, string deviceName, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        DeviceName = deviceName;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string Id { get; }

    public string DeviceName { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public bool IsSubmitted
    {
        get
        {
            lock (_lock)
            {
                return _configuration is not null;
            }
        }
    }

    public SetupConfiguration? Configuration
    {
        get
        {
            lock (_lock)
            {
                return _configuration;
            }
        }
    }

    public SetupSession? Session
    {
        get
        {
            lock (_lock)
            {
                return _session;
            }
        }
    }

    public DateTimeOffset? SessionAttachedAt { get; private set; }

    public void Submit(SetupConfiguration configuration, DateTimeOffset submittedAt)
    {
        lock (_lock)
        {
            _configuration = configuration;
            SubmittedAt = submittedAt;
        }
    }

    public void AttachSession(SetupSession session, DateTimeOffset attachedAt)
    {
        lock (_lock)
        {
            _session = session;
            SessionAttachedAt = attachedAt;
        }
    }
}

public sealed record SetupConfiguration(string PlaylistUrl, string? EpgUrl);

public sealed record SetupSession(string SessionId, string RemoteUrl);
