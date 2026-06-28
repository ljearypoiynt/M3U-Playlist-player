using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using M3UPlaylistPlayer.Desktop.Models;
using M3UPlaylistPlayer.Desktop.Services;

namespace M3UPlaylistPlayer.Desktop;

public partial class MainWindow : Window
{
    private readonly LibVLC _libVlc;
    private MediaPlayer? _mediaPlayer;
    private readonly XtreamClient _client;
    private readonly LocalApiServer _localApiServer;
    private IReadOnlyList<MediaItem> _allItems = [];
    private IReadOnlyList<MediaItem> _visibleItems = [];
    private readonly List<RendererDiscoverer> _rendererDiscoverers = [];
    private readonly Dictionary<RendererDiscoverer, string> _rendererNames = [];
    private readonly List<CastTarget> _castTargets = [new("This computer", "Local", null)];
    private CancellationTokenSource? _loadCancellation;
    private bool _isReady;
    private bool _updatingCastTargets;

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");

        _client = new XtreamClient(LoadSettings());
        _localApiServer = new LocalApiServer(_client);
        _isReady = true;
        PlayerView.AttachedToVisualTree += (_, _) => EnsureMediaPlayer();
        Opened += async (_, _) =>
        {
            await StartLocalApiAsync();
            UpdateCastTargets("Cast target: this computer.");
            RefreshCastTargets("Scanning for cast devices...");
            await LoadMediaAsync(MediaKind.Live);
        };
        Closed += (_, _) => DisposePlayer();
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await LoadMediaAsync(GetSelectedKind());
    }

    private async void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        await LoadMediaAsync(GetSelectedKind());
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        ApplyFilters(refreshCategories: true);
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        ApplyFilters(refreshCategories: false);
    }

    private void OnMediaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaItem item)
        {
            return;
        }

        NowPlayingTitle.Text = item.Name;
        NowPlayingMeta.Text = string.IsNullOrWhiteSpace(item.Group) ? item.Url : $"{item.Group}\n{item.Url}";
    }

    private void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        PlaySelected();
    }

    private void OnPlayClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PlaySelected();
    }

    private void OnPauseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _mediaPlayer?.Pause();
        PlaybackStatusText.Text = "Paused";
    }

    private void OnResumeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _mediaPlayer?.Play();
        PlaybackStatusText.Text = "Playing";
    }

    private void OnStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _mediaPlayer?.Stop();
        PlaybackStatusText.Text = "Stopped";
    }

    private void OnRescanCastClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshCastTargets("Scanning for cast devices...");
    }

    private void OnCastTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || _updatingCastTargets || _mediaPlayer is null)
        {
            return;
        }

        var target = GetSelectedCastTarget();
        var success = _mediaPlayer.SetRenderer(target.Renderer);
        PlaybackStatusText.Text = success
            ? target.IsLocal ? "Playback target: this computer." : $"Playback target: {target.Name}."
            : $"Could not switch playback target to {target.Name}.";
    }

    private async Task LoadMediaAsync(MediaKind kind)
    {
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        try
        {
            SetBusy(true, $"Loading {GetKindLabel(kind).ToLowerInvariant()}...");
            _allItems = await _client.GetMediaAsync(kind, cancellationToken);
            ApplyFilters(refreshCategories: true);
            StatusText.Text = $"{_allItems.Count:N0} {GetKindLabel(kind).ToLowerInvariant()} loaded.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load media: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyFilters(bool refreshCategories)
    {
        var selectedCategory = CategoryCombo.SelectedItem as string;

        if (refreshCategories)
        {
            var categories = new[] { "All categories" }
                .Concat(_allItems
                    .Select(item => item.Group)
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase))
                .ToArray();

            CategoryCombo.SelectionChanged -= OnCategoryChanged;
            CategoryCombo.ItemsSource = categories;
            CategoryCombo.SelectedItem = categories.Contains(selectedCategory) ? selectedCategory : categories[0];
            CategoryCombo.SelectionChanged += OnCategoryChanged;
            selectedCategory = CategoryCombo.SelectedItem as string;
        }

        var query = SearchBox.Text?.Trim();
        var filtered = _allItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(selectedCategory) && selectedCategory != "All categories")
        {
            filtered = filtered.Where(item => string.Equals(item.Group, selectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Group.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _visibleItems = filtered.ToArray();
        MediaList.ItemsSource = _visibleItems;
        StatusText.Text = $"{_visibleItems.Count:N0} of {_allItems.Count:N0} items shown.";
    }

    private void PlaySelected()
    {
        EnsureMediaPlayer();

        if (MediaList.SelectedItem is not MediaItem item)
        {
            PlaybackStatusText.Text = "Select a stream or movie first.";
            return;
        }

        using var media = new Media(_libVlc, new Uri(item.Url));
        var target = GetSelectedCastTarget();
        _mediaPlayer!.SetRenderer(target.Renderer);
        _mediaPlayer!.Play(media);
        NowPlayingTitle.Text = item.Name;
        NowPlayingMeta.Text = string.IsNullOrWhiteSpace(item.Group) ? item.Url : $"{item.Group}\n{item.Url}";
        PlaybackStatusText.Text = target.IsLocal
            ? $"Playing {GetKindLabel(item.Kind).ToLowerInvariant()} inside the app."
            : $"Casting {GetKindLabel(item.Kind).ToLowerInvariant()} to {target.Name}.";
    }

    private MediaKind GetSelectedKind()
    {
        return KindCombo?.SelectedIndex == 1 ? MediaKind.Movies : MediaKind.Live;
    }

    private static string GetKindLabel(MediaKind kind)
    {
        return kind == MediaKind.Movies ? "Movies" : "Live";
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        RefreshButton.IsEnabled = !isBusy;
        KindCombo.IsEnabled = !isBusy;
        SearchBox.IsEnabled = !isBusy;
        CategoryCombo.IsEnabled = !isBusy;
        PlayButton.IsEnabled = !isBusy;

        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    private static XtreamSettings LoadSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
        if (!File.Exists(settingsPath))
        {
            return new XtreamSettings("http://line.helixtech.top", "testjeary", "62462F");
        }

        var settings = JsonSerializer.Deserialize<XtreamSettingsFile>(File.ReadAllText(settingsPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return new XtreamSettings(
            settings?.Host ?? "http://line.helixtech.top",
            settings?.Username ?? "testjeary",
            settings?.Password ?? "62462F");
    }

    private void DisposePlayer()
    {
        _loadCancellation?.Cancel();
        StopRendererDiscovery();
        _localApiServer.StopAsync().GetAwaiter().GetResult();
        _mediaPlayer?.Stop();
        PlayerView.MediaPlayer = null;
        _mediaPlayer?.Dispose();
        _libVlc.Dispose();
        _loadCancellation?.Dispose();
    }

    private void EnsureMediaPlayer()
    {
        if (_mediaPlayer is not null)
        {
            return;
        }

        _mediaPlayer = new MediaPlayer(_libVlc);
        PlayerView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.SetRenderer(GetSelectedCastTarget().Renderer);
    }

    private void RefreshCastTargets(string? status = null)
    {
        StopRendererDiscovery();
        _castTargets.RemoveAll(target => !target.IsLocal);
        UpdateCastTargets(status);

        foreach (var rendererName in GetRendererNames())
        {
            try
            {
                var discoverer = new RendererDiscoverer(_libVlc, rendererName);
                discoverer.ItemAdded += OnRendererItemAdded;
                discoverer.ItemDeleted += OnRendererItemDeleted;

                if (discoverer.Start())
                {
                    _rendererDiscoverers.Add(discoverer);
                    _rendererNames[discoverer] = rendererName;
                }
                else
                {
                    discoverer.Dispose();
                }
            }
            catch (Exception ex)
            {
                PlaybackStatusText.Text = $"Cast discovery issue: {ex.Message}";
            }
        }
    }

    private IEnumerable<string> GetRendererNames()
    {
        var advertised = (_libVlc.RendererList ?? [])
            .Select(renderer => renderer.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);

        return advertised
            .Concat(["microdns_renderer", "upnp_renderer"])
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void OnRendererItemAdded(object? sender, RendererDiscovererItemAddedEventArgs e)
    {
        if (!e.RendererItem.CanRenderVideo)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_castTargets.Any(target => string.Equals(target.Name, e.RendererItem.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var source = sender is RendererDiscoverer discoverer && _rendererNames.TryGetValue(discoverer, out var rendererName)
                ? rendererName
                : e.RendererItem.Type;
            _castTargets.Add(new CastTarget(e.RendererItem.Name, source, e.RendererItem));
            var count = _castTargets.Count - 1;
            UpdateCastTargets($"{count} cast device{(count == 1 ? string.Empty : "s")} found.");
        });
    }

    private void OnRendererItemDeleted(object? sender, RendererDiscovererItemDeletedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _castTargets.RemoveAll(target =>
                target.Renderer is not null &&
                string.Equals(target.Renderer.Name, e.RendererItem.Name, StringComparison.OrdinalIgnoreCase));

            var count = _castTargets.Count - 1;
            UpdateCastTargets($"{count} cast device{(count == 1 ? string.Empty : "s")} found.");
        });
    }

    private void UpdateCastTargets(string? status = null)
    {
        var selectedName = (CastTargetCombo.SelectedItem as CastTarget)?.Name;
        _updatingCastTargets = true;
        CastTargetCombo.ItemsSource = null;
        CastTargetCombo.ItemsSource = _castTargets.ToArray();
        CastTargetCombo.SelectedItem = _castTargets.FirstOrDefault(target => target.Name == selectedName) ?? _castTargets[0];
        _updatingCastTargets = false;

        if (!string.IsNullOrWhiteSpace(status))
        {
            PlaybackStatusText.Text = status;
        }
    }

    private CastTarget GetSelectedCastTarget()
    {
        return CastTargetCombo?.SelectedItem as CastTarget ?? _castTargets[0];
    }

    private void StopRendererDiscovery()
    {
        foreach (var discoverer in _rendererDiscoverers)
        {
            discoverer.ItemAdded -= OnRendererItemAdded;
            discoverer.ItemDeleted -= OnRendererItemDeleted;
            discoverer.Stop();
            discoverer.Dispose();
        }

        _rendererDiscoverers.Clear();
        _rendererNames.Clear();
    }

    private async Task StartLocalApiAsync()
    {
        try
        {
            await _localApiServer.StartAsync();
            PlaybackStatusText.Text = $"Local TV API running for LG app: {_localApiServer.DisplayUrl}";
        }
        catch (Exception ex)
        {
            PlaybackStatusText.Text = $"Local TV API failed to start: {ex.Message}";
        }
    }

    private sealed class XtreamSettingsFile
    {
        public string? Host { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
