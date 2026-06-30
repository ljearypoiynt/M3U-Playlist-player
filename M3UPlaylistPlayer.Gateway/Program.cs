using M3UPlaylistPlayer.Desktop.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(_ => LoadXtreamSettings(builder.Configuration));
builder.Services.AddSingleton<XtreamClient>();
builder.Services.AddSingleton(serviceProvider =>
    new LocalApiServer(
        serviceProvider.GetRequiredService<XtreamClient>(),
        builder.Configuration["Gateway:Urls"]));
builder.Services.AddHostedService<GatewayHostedService>();

using var host = builder.Build();
await host.RunAsync();

static XtreamSettings LoadXtreamSettings(IConfiguration configuration)
{
    var playlistUrl = FirstNonEmpty(configuration["Xtream:PlaylistUrl"], configuration["Playlist:Url"]);
    if (!string.IsNullOrWhiteSpace(playlistUrl) &&
        XtreamSettings.TryFromPlaylistUrl(
            playlistUrl,
            FirstNonEmpty(configuration["Xtream:EpgUrl"], configuration["Playlist:EpgUrl"]),
            out var settings,
            out _))
    {
        return settings;
    }

    return new XtreamSettings(
        FirstNonEmpty(configuration["Xtream:Host"], configuration["Playlist:Host"]),
        FirstNonEmpty(configuration["Xtream:Username"], configuration["Playlist:Username"], "unused"),
        FirstNonEmpty(configuration["Xtream:Password"], configuration["Playlist:Password"], "unused"),
        FirstNonEmpty(configuration["Xtream:EpgUrl"], configuration["Playlist:EpgUrl"]));
}

static string FirstNonEmpty(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

internal sealed class GatewayHostedService : IHostedService
{
    private readonly LocalApiServer _server;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(LocalApiServer server, ILogger<GatewayHostedService> logger)
    {
        _server = server;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _server.StartAsync(cancellationToken);
        _logger.LogInformation("M3U gateway listening on {Url}", _server.Url);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _server.StopAsync();
    }
}
