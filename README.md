# M3U Playlist Player

A small cross-platform .NET web app for downloading an M3U playlist, searching and filtering channels, and launching streams on your laptop.

## Run

```powershell
dotnet run
```

Open the local URL printed by .NET, usually `http://localhost:5293`.

## Playlist API

The app can build the playlist URL from API details in `appsettings.json`:

```json
"Playlist": {
  "Host": "http://prohelix.top",
  "Username": "testjeary",
  "Password": "62462F"
}
```

That resolves to the standard M3U endpoint:

```text
http://prohelix.top/get.php?username=...&password=...&type=m3u_plus&output=mpegts
```

## Playback

The in-page video player uses HLS (`.m3u8`) playback when the provider supports it. MPEG-TS (`.ts`) streams are not supported by most desktop browsers, so the app uses `.m3u8` URLs for browser playback where it can.

The external launch button tries VLC first and then mpv. It does not fall back to opening the stream URL in the browser, because that usually downloads the stream instead of playing it.

On Linux, install one of:

```bash
sudo apt install vlc
sudo apt install mpv
```

On Windows, the app checks the normal VLC install folders. To force a specific player path, set `Player:Command` in `appsettings.json`, for example:

```json
"Player": {
  "Command": "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe"
}
```

## TV Guide

Turn on the `TV guide` filter to load Now/Next programme data from the provider's XMLTV feed. The guide feed is large, so the app only loads it when the toggle is enabled and caches it for `GuideCacheMinutes`.

## Desktop App

An Avalonia desktop app is being built in `M3UPlaylistPlayer.Desktop`. It uses LibVLCSharp so streams and movies can play inside the app window instead of opening an external VLC window.

Run it with:

```powershell
dotnet run --project .\M3UPlaylistPlayer.Desktop\M3UPlaylistPlayer.Desktop.csproj
```

The first version supports live TV, movies, search, category filtering, and embedded playback. Chromecast/casting is planned as a later step once local playback is stable.

When the desktop app starts it also runs a small local API for the LG companion app:

```text
http://localhost:5055
```

Check the reachable network URL with:

```powershell
Invoke-WebRequest -Uri http://localhost:5055/api/status -UseBasicParsing
```

## LG webOS Companion

The Phase 2 prototype lives in `webos-companion`. It is a simple webOS app that connects to the desktop API, lists live channels and movies, and asks the desktop app to proxy selected streams as HLS for the TV.

The current laptop URL for the TV app is:

```text
http://192.168.50.99:5055
```

See `webos-companion/README.md` for packaging notes.

TV playback requires FFmpeg on the desktop machine. The desktop app uses it to turn raw provider streams into local HLS playback URLs such as `/api/hls/.../index.m3u8`.

## Gateway Deployment

The Kubernetes deployment runs the headless gateway project in `M3UPlaylistPlayer.Gateway`; the Avalonia GUI is not part of the container image.

Build locally:

```powershell
dotnet run --project .\M3UPlaylistPlayer.Gateway\M3UPlaylistPlayer.Gateway.csproj
```

Deploy through ArgoCD by applying the project and application manifests:

```powershell
kubectl apply -f .\argocd\project.yaml
kubectl apply -f .\argocd\application.yaml
```

The workflow in `.github/workflows/docker-build.yml` publishes the gateway image to:

```text
ghcr.io/ljearypoiynt/m3u-playlist-player/gateway
```

For Cloudflare Tunnel, point the hostname at the in-cluster service:

```yaml
- hostname: m3u.poiynt.com
  service: http://m3u-gateway-service.m3u-playlist-player.svc.cluster.local:5055
```
