using M3UPlaylistPlayer.Desktop.Models;
using System.Text.Json;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class PlaybackSession : IDisposable
{
    private readonly Dictionary<MediaKind, int> _knownCounts = [];
    private readonly Dictionary<MediaKind, List<CuratedList>> _curatedLists = [];
    private readonly Dictionary<MediaKind, List<string>> _excludedCategories = [];
    private readonly Dictionary<MediaKind, List<string>> _selectedCategories = [];
    private readonly object _lock = new();
    private readonly object _remoteLock = new();
    private long _remoteCommandSequence;
    private RemoteCommand? _lastRemoteCommand;

    public PlaybackSession(
        string id,
        string deviceName,
        XtreamSettings settings,
        string playbackMode,
        IReadOnlyList<string>? excludedLiveCategories,
        IReadOnlyList<string>? selectedLiveCategories,
        DateTimeOffset createdAt,
        TimeSpan lifetime)
    {
        Id = id;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "LG TV" : deviceName.Trim();
        Settings = settings;
        PlaybackMode = string.Equals(playbackMode, "direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "hls";
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
        ExpiresAt = createdAt.Add(lifetime);
        Client = new XtreamClient(settings);
        HlsStreamService = new HlsStreamService(id);
        SetExcludedCategories(MediaKind.Live, excludedLiveCategories);
        SetSelectedCategories(MediaKind.Live, selectedLiveCategories);
    }

    public string Id { get; }

    public string DeviceName { get; }

    public XtreamSettings Settings { get; }

    public string PlaybackMode { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public XtreamClient Client { get; }

    public HlsStreamService HlsStreamService { get; }

    public string SourceHost => Settings.SourceHost;

    public bool HasCustomEpg => !string.IsNullOrWhiteSpace(Settings.EpgUrl);

    public void Touch(DateTimeOffset now, TimeSpan lifetime)
    {
        lock (_lock)
        {
            LastSeenAt = now;
            ExpiresAt = now.Add(lifetime);
        }
    }

    public void SetPlaybackMode(string? playbackMode)
    {
        PlaybackMode = string.Equals(playbackMode, "direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "hls";
    }

    public void RememberCount(MediaKind kind, int count)
    {
        lock (_lock)
        {
            _knownCounts[kind] = Math.Max(0, count);
        }
    }

    public int? GetKnownCount(MediaKind kind)
    {
        lock (_lock)
        {
            return _knownCounts.TryGetValue(kind, out var count) ? count : null;
        }
    }

    public IReadOnlyList<string> GetExcludedCategories(MediaKind kind)
    {
        lock (_lock)
        {
            return _excludedCategories.TryGetValue(kind, out var categories)
                ? categories.ToArray()
                : [];
        }
    }

    public void SetExcludedCategories(MediaKind kind, IReadOnlyList<string>? categories)
    {
        var safeCategories = (categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock)
        {
            _excludedCategories[kind] = safeCategories;
        }
    }

    public IReadOnlyList<string> GetSelectedCategories(MediaKind kind)
    {
        lock (_lock)
        {
            return _selectedCategories.TryGetValue(kind, out var categories)
                ? categories.ToArray()
                : [];
        }
    }

    public void SetSelectedCategories(MediaKind kind, IReadOnlyList<string>? categories)
    {
        var safeCategories = (categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock)
        {
            _selectedCategories[kind] = safeCategories;
        }
    }

    public IReadOnlyList<CuratedList> GetCuratedLists(MediaKind kind)
    {
        lock (_lock)
        {
            return _curatedLists.TryGetValue(kind, out var lists)
                ? lists.Select(list => list with
                {
                    ItemIds = list.ItemIds.ToArray()
                }).ToArray()
                : [];
        }
    }

    public CuratedList SaveCuratedList(MediaKind kind, string? name, IReadOnlyList<string>? itemIds)
    {
        return UpsertCuratedList(kind, null, name, itemIds);
    }

    public CuratedList UpsertCuratedList(MediaKind kind, string? id, string? name, IReadOnlyList<string>? itemIds)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "New list" : name.Trim();
        var safeItemIds = (itemIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToArray();
        var safeId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id.Trim();
        var now = DateTimeOffset.Now;

        lock (_lock)
        {
            if (!_curatedLists.TryGetValue(kind, out var lists))
            {
                lists = [];
                _curatedLists[kind] = lists;
            }

            var existingIndex = lists.FindIndex(existing => string.Equals(existing.Id, safeId, StringComparison.OrdinalIgnoreCase));
            var createdAt = existingIndex >= 0 ? lists[existingIndex].CreatedAt : now;
            var list = new CuratedList(
                safeId,
                kind.ToString().ToLowerInvariant(),
                safeName.Length > 80 ? safeName[..80] : safeName,
                safeItemIds,
                createdAt);

            if (existingIndex >= 0)
            {
                lists[existingIndex] = list;
                return list;
            }

            lists.Add(list);
            return list;
        }
    }

    public bool DeleteCuratedList(MediaKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_curatedLists.TryGetValue(kind, out var lists))
            {
                return false;
            }

            return lists.RemoveAll(list => string.Equals(list.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public void ImportCuratedList(MediaKind kind, CuratedList list)
    {
        lock (_lock)
        {
            if (!_curatedLists.TryGetValue(kind, out var lists))
            {
                lists = [];
                _curatedLists[kind] = lists;
            }

            if (lists.Any(existing => string.Equals(existing.Id, list.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            lists.Add(list);
        }
    }

    public bool TryGetCuratedList(MediaKind kind, string? id, out CuratedList list)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                _curatedLists.TryGetValue(kind, out var lists))
            {
                var match = lists.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    list = match;
                    return true;
                }
            }
        }

        list = null!;
        return false;
    }

    public RemoteCommand AcceptRemoteCommand(RemoteCommandRequest request)
    {
        var type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
        var kind = string.Equals(request.Kind, "movies", StringComparison.OrdinalIgnoreCase) ? "movies" : "live";
        var playbackMode = string.Equals(request.PlaybackMode, "direct", StringComparison.OrdinalIgnoreCase) ? "direct" : "hls";
        var item = request.Item.HasValue ? request.Item.Value.Clone() : (JsonElement?)null;

        lock (_remoteLock)
        {
            _remoteCommandSequence += 1;
            _lastRemoteCommand = new RemoteCommand(
                _remoteCommandSequence,
                type,
                request.ItemId,
                kind,
                playbackMode,
                item,
                DateTimeOffset.Now);

            return _lastRemoteCommand;
        }
    }

    public RemoteCommandResult GetRemoteCommandAfter(long after)
    {
        lock (_remoteLock)
        {
            if (_lastRemoteCommand is not null && _lastRemoteCommand.Sequence > after)
            {
                return new RemoteCommandResult(true, _lastRemoteCommand.Sequence, _lastRemoteCommand);
            }

            return new RemoteCommandResult(false, _remoteCommandSequence, null);
        }
    }

    public void Dispose()
    {
        HlsStreamService.Dispose();
    }
}

public sealed record RemoteCommandRequest(
    string? Type,
    string? ItemId,
    string? Kind,
    string? PlaybackMode,
    JsonElement? Item);

public sealed record RemoteCommand(
    long Sequence,
    string Type,
    string? ItemId,
    string Kind,
    string PlaybackMode,
    JsonElement? Item,
    DateTimeOffset CreatedAt);

public sealed record RemoteCommandResult(bool HasCommand, long Sequence, RemoteCommand? Command);

public sealed record CuratedList(
    string Id,
    string Kind,
    string Name,
    IReadOnlyList<string> ItemIds,
    DateTimeOffset CreatedAt);
