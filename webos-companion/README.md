# LG webOS Companion

This is the Phase 2 prototype TV app. It connects to the desktop app's local API and lets the TV browse live channels and movies.

## Before Opening On The TV

1. Start the desktop app:

```powershell
dotnet run --project ..\M3UPlaylistPlayer.Desktop\M3UPlaylistPlayer.Desktop.csproj
```

2. Check the API from the laptop:

```powershell
Invoke-WebRequest -Uri http://localhost:5055/api/status -UseBasicParsing
```

3. Use the `localUrl` value on the TV. On this machine it is currently:

```text
http://192.168.50.99:5055
```

If Windows asks about firewall access, allow the app on private networks. The TV and laptop must be on the same Wi-Fi/LAN.

## webOS Packaging

Install the LG webOS TV SDK/CLI, then package this folder:

```powershell
ares-package .
```

With the TV in Developer Mode and paired with the CLI:

```powershell
ares-install .\com.local.m3uplaylistplayer_0.1.5_all.ipk
ares-launch com.local.m3uplaylistplayer
```

## Playback Modes

The TV app has a playback toggle:

- `HLS proxy` asks the desktop app to convert the provider stream into local HLS with FFmpeg.
- `Direct` sends the raw provider URL to the TV video player, which is useful for testing whether your LG model can play `.ts` streams directly.

For HLS proxy playback, the desktop machine needs FFmpeg available on `PATH`.

You can check this with:

```powershell
ffmpeg -version
```

If playback starts slowly, that is usually FFmpeg preparing the first few HLS segments.
