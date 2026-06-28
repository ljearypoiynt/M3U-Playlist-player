using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M3UPlaylistPlayer.Desktop.Models;

namespace M3UPlaylistPlayer.Desktop.Services;

public sealed class CuratedListStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _lock = new();
    private readonly string _path;

    public CuratedListStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "M3UPlaylistPlayer");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "curated-lists.json");
    }

    public IReadOnlyList<CuratedList> GetLists(XtreamSettings settings, MediaKind kind)
    {
        var key = BuildKey(settings);
        var safeKind = kind.ToString().ToLowerInvariant();

        lock (_lock)
        {
            return Load()
                .Where(list => string.Equals(list.AccountKey, key, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(list.Kind, safeKind, StringComparison.OrdinalIgnoreCase))
                .Select(list => list.ToCuratedList())
                .ToArray();
        }
    }

    public void Save(XtreamSettings settings, CuratedList list)
    {
        var key = BuildKey(settings);

        lock (_lock)
        {
            var lists = Load()
                .Where(existing => !string.Equals(existing.AccountKey, key, StringComparison.OrdinalIgnoreCase) ||
                                   !string.Equals(existing.Id, list.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            lists.Add(StoredCuratedList.From(key, list));
            File.WriteAllText(_path, JsonSerializer.Serialize(lists, JsonOptions));
        }
    }

    private IReadOnlyList<StoredCuratedList> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredCuratedList>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildKey(XtreamSettings settings)
    {
        var raw = $"{settings.SourceHost.Trim().ToLowerInvariant()}|{settings.Username.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private sealed record StoredCuratedList(
        string AccountKey,
        string Id,
        string Kind,
        string Name,
        IReadOnlyList<string> ItemIds,
        DateTimeOffset CreatedAt)
    {
        public CuratedList ToCuratedList()
        {
            return new CuratedList(Id, Kind, Name, ItemIds, CreatedAt);
        }

        public static StoredCuratedList From(string accountKey, CuratedList list)
        {
            return new StoredCuratedList(accountKey, list.Id, list.Kind, list.Name, list.ItemIds, list.CreatedAt);
        }
    }
}
