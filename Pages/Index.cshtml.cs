using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using M3UPlaylistPlayer.Models;
using M3UPlaylistPlayer.Options;
using M3UPlaylistPlayer.Services;

namespace M3UPlaylistPlayer.Pages;

public class IndexModel(
    IPlaylistService playlistService,
    IGuideService guideService,
    IStreamLauncher streamLauncher,
    IOptions<PlaylistOptions> options) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string SourceUrl { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Group { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool UkOnly { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? PlayId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowGuide { get; set; }

    public IReadOnlyList<PlaylistEntry> Streams { get; private set; } = [];
    public IReadOnlyList<string> Groups { get; private set; } = [];
    public IReadOnlyDictionary<string, StreamGuide> Guide { get; private set; } =
        new Dictionary<string, StreamGuide>(StringComparer.OrdinalIgnoreCase);
    public PlaylistEntry? SelectedStream { get; private set; }
    public DateTimeOffset? DownloadedAt { get; private set; }
    public string SourceKind { get; private set; } = "M3U";
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? GuideMessage { get; private set; }

    public async Task OnGetAsync(bool refresh, CancellationToken cancellationToken)
    {
        SourceUrl = ResolveSourceUrl(SourceUrl);

        try
        {
            var playlist = await playlistService.GetPlaylistAsync(SourceUrl, refresh, cancellationToken);
            PopulateViewModel(playlist);

            if (ShowGuide)
            {
                try
                {
                    Guide = await guideService.GetGuideAsync(
                        SourceUrl,
                        Streams.Select(stream => stream.TvgId ?? string.Empty).ToArray(),
                        refresh,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Xml.XmlException)
                {
                    GuideMessage = $"The TV guide could not be loaded: {ex.Message}";
                }
            }

            if (!string.IsNullOrWhiteSpace(PlayId))
            {
                SelectedStream = playlist.Entries.FirstOrDefault(entry => entry.Id == PlayId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            ErrorMessage = ex is TaskCanceledException
                ? "The playlist download timed out. Try refreshing again."
                : ex.Message;
        }
    }

    public async Task<IActionResult> OnPostOpenAsync(string id, string sourceUrl, CancellationToken cancellationToken)
    {
        SourceUrl = ResolveSourceUrl(sourceUrl);

        try
        {
            var entry = await playlistService.FindEntryAsync(SourceUrl, id, cancellationToken);
            if (entry is null)
            {
                TempData["StatusMessage"] = "That stream was not found in the current playlist.";
            }
            else
            {
                var result = await streamLauncher.LaunchAsync(entry, cancellationToken);
                TempData[result.Success ? "StatusMessage" : "ErrorMessage"] = result.Message;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            TempData["ErrorMessage"] = ex is TaskCanceledException
                ? "The playlist download timed out before the player could open."
                : ex.Message;
        }

        return RedirectToPage(new
        {
            sourceUrl = SourceUrl,
            query = Query,
            group = Group,
            ukOnly = UkOnly,
            showGuide = ShowGuide
        });
    }

    private void PopulateViewModel(PlaylistResult playlist)
    {
        DownloadedAt = playlist.DownloadedAt;
        SourceKind = playlist.SourceKind;
        StatusMessage = TempData["StatusMessage"] as string;
        ErrorMessage = TempData["ErrorMessage"] as string;
        Groups = playlist.Entries
            .Select(entry => entry.GroupTitle)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filtered = playlist.Entries.AsEnumerable();

        if (UkOnly)
        {
            filtered = filtered.Where(entry => entry.IsUk);
        }

        if (!string.IsNullOrWhiteSpace(Group))
        {
            filtered = filtered.Where(entry => string.Equals(entry.GroupTitle, Group, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(Query))
        {
            filtered = filtered.Where(MatchesQuery);
        }

        Streams = filtered
            .OrderByDescending(entry => entry.IsUk)
            .ThenBy(entry => entry.GroupTitle)
            .ThenBy(entry => entry.Name)
            .ToArray();
    }

    private bool MatchesQuery(PlaylistEntry entry)
    {
        var queryRegex = BuildSearchRegex(Query);
        return queryRegex is null ||
               queryRegex.IsMatch($"{entry.Name} {entry.GroupTitle} {entry.TvgId}");
    }

    private static Regex? BuildSearchRegex(string? query)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Regex.Escape)
            .ToArray();

        return terms.Length == 0
            ? null
            : new Regex(string.Join(".*", terms), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ResolveSourceUrl(string? sourceUrl)
    {
        return string.IsNullOrWhiteSpace(sourceUrl)
            ? options.Value.GetDefaultSourceUrl()
            : sourceUrl.Trim();
    }
}
