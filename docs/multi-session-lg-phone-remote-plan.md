# Multi-Session LG TV and Phone Remote Plan

## Goal

Allow multiple people or TVs to use the desktop app at the same time without storing playlist credentials on the desktop. Each LG TV app supplies its own playlist and EPG details when it starts. The desktop app creates an in-memory session and acts as a local helper for playlist parsing, guide lookup, HLS proxying, and phone remote commands.

## Core Idea

The LG app owns the playlist configuration.

1. User enters playlist URL and optional EPG URL in the LG app.
2. LG app sends those details to the desktop app.
3. Desktop app creates a temporary in-memory session.
4. Desktop app returns a session ID and phone remote URL.
5. LG app shows a QR code for the phone remote URL.
6. Phone scans the QR code and controls that specific TV session.

The desktop app should not expose the full playlist URL or credentials through status endpoints, logs, UI screens, or the phone remote.

## Session Model

Each session represents one active TV/client.

Suggested session fields:

```text
SessionId
DeviceName
PlaylistUrl
EpgUrl
CreatedAt
LastSeenAt
PlaybackMode
CurrentHlsSessionId
PlaylistClient
GuideCache
MediaCache
RemoteCommandSequence
LastRemoteCommand
```

The session ID should be generated server-side. A GUID is acceptable for this local-network app. A stronger future option is a 32-byte random token encoded as URL-safe text.

## API Shape

### Create Session

```http
POST /api/sessions
```

Request:

```json
{
  "deviceName": "Living Room TV",
  "playlistUrl": "hidden credential-bearing URL",
  "epgUrl": "optional XMLTV URL",
  "playbackMode": "hls"
}
```

Response:

```json
{
  "sessionId": "8f2f2ac4-6f5a-4a6d-8f3d-3cb5a5e9b8af",
  "remoteUrl": "http://192.168.50.99:5055/remote?session=8f2f2ac4-6f5a-4a6d-8f3d-3cb5a5e9b8af",
  "expiresAt": "2026-06-26T10:00:00+01:00"
}
```

### Session Status

```http
GET /api/sessions/{sessionId}/status
```

Safe response only:

```json
{
  "status": "connected",
  "deviceName": "Living Room TV",
  "sourceHost": "line.helixtech.top",
  "playlistLoaded": true,
  "epgLoaded": true,
  "channelCount": 1402,
  "movieCount": 0,
  "lastRefresh": "10:42"
}
```

### Media

```http
GET /api/sessions/{sessionId}/media?kind=live&query=bbc&group=&region=uk&skip=0&limit=80
```

This replaces the current global `/api/media` for TV and phone use.

### Categories

```http
GET /api/sessions/{sessionId}/categories?kind=live
```

### Guide

```http
GET /api/sessions/{sessionId}/guide?ids=live-123,live-456
```

Uses the session-specific EPG.

### Play

```http
GET /api/sessions/{sessionId}/play/{itemId}?kind=live
```

Starts HLS proxy playback for that session.

### Stop

```http
POST /api/sessions/{sessionId}/stop
```

Stops the session's current HLS process and clears playback state.

### Phone Remote Commands

```http
POST /api/sessions/{sessionId}/remote/command
GET /api/sessions/{sessionId}/remote/commands?after=0
```

Commands:

```json
{
  "type": "play",
  "itemId": "live-123",
  "kind": "live",
  "playbackMode": "hls",
  "item": {
    "id": "live-123",
    "name": "BBC One HD",
    "group": "UK General"
  }
}
```

```json
{
  "type": "stop"
}
```

## Phone Remote URL

The QR code should point to:

```text
http://192.168.50.99:5055/remote?session={sessionId}
```

The phone remote should refuse to work without a valid session parameter.

## LG App Changes

### Startup Flow

1. Show saved server URL, playlist URL, and EPG URL fields.
2. User presses connect.
3. LG app posts to `/api/sessions`.
4. Store returned `sessionId` in memory.
5. Use session-scoped APIs for media, guide, playback, stop, and commands.
6. Show QR code for the phone remote URL.

### Storage

The LG app may store playlist and EPG URLs in `localStorage` for convenience. The desktop app should only keep them in memory for the active session.

### Playback

The LG app should call:

```text
/api/sessions/{sessionId}/play/{itemId}
```

instead of:

```text
/api/play/{itemId}
```

For Direct playback, it can still play the stream URL directly if the item includes it.

### Stop Button

The remote Stop key and phone Stop command should call:

```text
/api/sessions/{sessionId}/stop
```

## Phone Remote Changes

1. Read `session` from the query string.
2. Load safe session status.
3. Use session-scoped media/category/guide endpoints.
4. Send play/stop commands to the session command endpoint.
5. Keep the same filters: Live/Movies, UK only, category, search, HLS/Direct.

## Desktop App Changes

### New Services

Add a session manager:

```text
SessionManager
  CreateSession(...)
  GetSession(sessionId)
  TouchSession(sessionId)
  RemoveSession(sessionId)
  CleanupExpiredSessions()
```

Add a session object:

```text
PlaybackSession
  SessionId
  Settings
  XtreamClient or playlist client
  HlsStreamService
  Caches
  Remote command queue
```

The existing `LocalApiServer` should delegate session-specific work to `SessionManager`.

### Keep Legacy Endpoints Temporarily

Keep the current global endpoints for now so the current desktop and TV app do not break while the session work is being built:

```text
/api/media
/api/guide
/api/play
/api/remote/command
```

Once the LG and phone remote are fully session-based, these can either be removed or treated as a default local session for desktop-only use.

## Privacy and Safety

Do:

- Store playlist and EPG URLs only in memory on the desktop.
- Redact credentials from errors and logs.
- Show only safe source metadata, such as host name and load status.
- Expire inactive sessions.
- Stop HLS processes when sessions expire.
- Use session-scoped command queues.

Do not:

- Return playlist URLs from status APIs.
- Show full credential-bearing URLs in the phone remote.
- Log request bodies for `/api/sessions`.
- Share one global remote command queue across users.

## Session Expiry

Recommended first version:

```text
Expire sessions after 12 hours of inactivity.
Touch session on any TV or phone API request.
Run cleanup every 10 minutes.
```

If the desktop app restarts, sessions are lost. The LG app can reconnect and resend its playlist details.

## QR Code

Use a small client-side QR library in the LG app or generate the QR code from the desktop app.

Recommended first version:

- Desktop returns `remoteUrl`.
- LG app renders QR code client-side.
- Also show the URL as text for manual entry.

## Implementation Phases

### Phase 1: Session Infrastructure

- Add `SessionManager`.
- Add in-memory `PlaybackSession`.
- Add `POST /api/sessions`.
- Add safe session status endpoint.
- Add expiry cleanup.
- Keep existing global endpoints unchanged.

### Phase 2: Session-Scoped Media and Guide

- Add session-scoped media endpoint.
- Add session-scoped category endpoint.
- Add session-scoped guide endpoint.
- Make each session use its own playlist and EPG settings.
- Confirm two sessions can load different playlists independently.

### Phase 3: Session-Scoped Playback

- Add session-scoped play endpoint.
- Add session-scoped stop endpoint.
- Make HLS processes belong to a specific session.
- Ensure session expiry stops any running HLS process.

### Phase 4: LG App Session Onboarding

- Add playlist URL and EPG URL setup UI.
- Send details to `POST /api/sessions`.
- Store returned `sessionId` in memory.
- Move media, guide, play, and stop calls to session endpoints.
- Preserve current filters and playback behavior.

### Phase 5: QR Phone Pairing

- Generate phone remote URL with session ID.
- Show QR code on the LG app.
- Update phone remote to require `?session=...`.
- Route all phone API calls through session endpoints.

### Phase 6: Polish and Hardening

- Redact credentials in all displayed errors.
- Add better session reconnect handling.
- Add friendly expired-session UI on TV and phone.
- Add a "Reset playlist details" option on the LG app.
- Test two LG app sessions against the same desktop server.

## Test Plan

### Desktop API

- Create a session with a playlist URL and EPG URL.
- Confirm status shows safe metadata only.
- Confirm media loads for that session.
- Confirm guide data uses that session's EPG.
- Confirm expired sessions are removed.

### Multi-Session

- Create two sessions with different playlist or EPG URLs.
- Confirm session A media does not appear in session B.
- Confirm phone commands for session A do not affect session B.

### LG App

- Connect with playlist and EPG details.
- Browse live channels.
- Search by channel name and now/next guide text.
- Play HLS.
- Stop with remote Stop key.
- Reconnect after desktop restart.

### Phone Remote

- Scan QR code.
- Confirm phone loads the correct session.
- Search/filter from phone.
- Tap a channel and confirm TV plays it.
- Press Stop and confirm TV stops.
- Try an invalid/expired session and confirm a clear error appears.

## Open Questions

- Should the LG app require the EPG URL, or should it derive XMLTV from Xtream credentials when possible?
  Answer: derive XMLTV from Xtream credentials when possible. Keep EPG URL as an optional override.
- Should playlist details be saved permanently on the LG app or typed every time?
  Answer: save playlist details permanently on the LG app using local storage.
- Should the desktop app expose a small screen listing active sessions without sensitive URLs?
  Answer: yes. Expose only safe metadata, never credential-bearing URLs.
- Should the phone remote be installable as a PWA later?
  Answer: yes. Add manifest/service worker after the session-based remote flow is stable.

## Preferred First Build

Start with phases 1 and 2. That gives us the important foundation: multiple in-memory sessions, each with its own playlist and EPG, without breaking the current app while the LG and phone clients are migrated.

## Implementation Status

### Completed: 2026-06-25

- Added `SessionManager` for in-memory session creation, lookup, safe listing, and inactivity cleanup.
- Added `PlaybackSession` with per-session `XtreamClient`, source host metadata, playback mode, expiry, and safe media count tracking.
- Added `POST /api/sessions`.
- Added `GET /api/sessions`.
- Added `GET /api/sessions/{sessionId}/status`.
- Added `GET /api/sessions/{sessionId}/media`.
- Added `GET /api/sessions/{sessionId}/categories`.
- Added `GET /api/sessions/{sessionId}/guide`.
- Added `GET /api/sessions/{sessionId}/play/{itemId}`.
- Added `GET /api/sessions/{sessionId}/hls/{hlsSessionId}/{fileName}`.
- Added `POST /api/sessions/{sessionId}/stop`.
- Added `GET /api/sessions/{sessionId}/stop` for client compatibility.
- Added `POST /api/sessions/{sessionId}/remote/command`.
- Added `GET /api/sessions/{sessionId}/remote/commands`.
- Added playlist URL parsing for Xtream-style `get.php` URLs.
- Added optional per-session EPG/XMLTV URL override.
- Added per-session HLS process ownership and cleanup.
- Added per-session remote command queues.
- Added separate HLS temp workspaces per session.
- Updated the LG app to save playlist and EPG URLs locally.
- Updated the LG app to create an in-memory desktop session when a playlist URL is configured.
- Updated the LG app to use session-scoped media, categories, guide, play, stop, and remote-command polling.
- Updated the phone remote to require `?session=...`.
- Updated the phone remote to use session-scoped media, categories, guide, and commands.
- Added `/sessions` safe active-session page.
- Moved LG gateway, playlist, and EPG fields into a dedicated setup screen.
- Added phone setup flow with temporary setup links.
- Added setup QR SVG generation from the desktop server.
- Added `/setup?code=...` phone setup page.
- Added TV polling for phone-submitted playlist details.
- Added setup-to-remote handoff so the phone setup page redirects to the session remote URL once the TV creates its session.
- Kept the existing global endpoints unchanged for the current LG app and phone remote.

### Verified: 2026-06-25

- Created two separate sessions from the same playlist URL.
- Loaded session-scoped UK BBC media.
- Loaded session-scoped categories.
- Loaded session-scoped guide data with now/next titles, descriptions, and times.
- Sent and read a session-scoped phone remote play command.
- Started session-scoped HLS playback and loaded the session-scoped manifest.
- Stopped session-scoped playback.
- Verified the session phone remote page loads.
- Verified the safe active-session page loads.
- Verified setup link creation.
- Verified local QR SVG generation.
- Verified the phone setup page loads.
- Verified phone setup submission can be polled by the TV.
- Verified setup link can publish a session remote URL.
- Confirmed safe session status exposes source host and counts, not credential-bearing playlist URLs.
- Packaged and installed LG app version `0.1.20`.
- Packaged and installed LG app version `0.1.21`.
- Packaged and installed LG app version `0.1.22`.

### Next Up

- Test the migrated session flow on the real LG TV.
- Add QR rendering for the phone remote URL on the TV.
- Add PWA manifest/service worker for the phone remote after session pairing is stable.
