using M3UPlaylistPlayer.Options;
using M3UPlaylistPlayer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PlaylistOptions>(builder.Configuration.GetSection("Playlist"));
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(nameof(PlaylistService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("M3UPlaylistPlayer/1.0");
});
builder.Services.AddSingleton<M3UPlaylistParser>();
builder.Services.AddSingleton<IPlaylistService, PlaylistService>();
builder.Services.AddSingleton<IGuideService, XmlTvGuideService>();
builder.Services.AddSingleton<IStreamLauncher, StreamLauncher>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
