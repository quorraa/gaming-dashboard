# LAN Dashboard

Single-service Windows LAN dashboard for a gaming PC, designed for an iPad Pro 11" landscape browser as a touch control surface.

## Stack choices

- Backend: `ASP.NET Core (.NET 9, Windows target)` for direct WMI, process, ping, and Core Audio access.
- Frontend: `vanilla HTML/CSS/JS` served from `wwwroot` for a fixed dashboard layout without a second build system.
- Transport: `raw WebSocket` for sub-second pushes and bidirectional mixer control.
- Windows access: `.NET + WMI + NAudio/Core Audio`.
- HWiNFO access: `shared memory` by default via `Global\HWiNFO_SENS_SM2` and `Global\HWiNFO_SM2_MUTEX`.
- Config: `appsettings.json` under `Dashboard`.

## Useful config

- `Dashboard:Ui:VisiblePanels`: choose which panels are shown.
- `Dashboard:Audio:VisibleSessionMatches`: limit mixer rows to named apps such as `Discord`, `Firefox`, `Spotify`, `Game`, `OBS`.
- `Dashboard:Audio:IncludeSystemSounds`: keep or remove the system sounds session.
- `Dashboard:Audio:MaxSessions`: cap the number of visible mixer rows after filtering.

## Implemented panels

- Temps from the HWiNFO Remote Sensor Monitor endpoint with configurable matching and thresholds.
- Temps from HWiNFO shared memory with configurable matching and thresholds. HTTP mode remains configurable as an alternate source.
- Discord tracked user, favorite users, voice roster, and latest messages through a bot client.
- Network throughput, ping, jitter, and sparkline history.
- System info from Windows APIs/WMI.
- Per-app audio sessions with live volume and mute control.
- Top processes by CPU and RAM.

## Discord notes

- This uses a bot token, not a user token.
- To populate presence and message content, enable `Guild Presences`, `Guild Voice States`, and `Message Content` intents in the Discord developer portal.
- Passive per-user speaking detection is not exposed cleanly here, so the UI currently shows `n/a` for speaking.

## Run

```powershell
dotnet run --project .\src\Monitor.Server
```

For LAN access:

```powershell
$env:ASPNETCORE_URLS="http://0.0.0.0:5055"
dotnet run --project .\src\Monitor.Server
```

Then open `http://<gaming-pc-lan-ip>:5055` on the iPad.

## Portable build

Build a self-contained Windows package:

```powershell
.\scripts\publish-portable.ps1
```

That publishes to `.\artifacts\publish\win-x64-Release` and also creates a zip in `.\artifacts`.

Prepare a target machine for the portable package:

```powershell
.\scripts\install-prereqs.ps1
```

What the install script does:

- installs HWiNFO through `winget` unless it is already installed
- creates a Windows Firewall allow rule for TCP `5103`

The published package is self-contained, so it does not require a separate .NET runtime on the target machine.

## HWiNFO setup

- In HWiNFO, ensure `Shared Memory Support` is enabled.
- The dashboard server runs on the same Windows machine, so it reads HWiNFO locally and the iPad never talks to HWiNFO directly.
- No special localhost signing or browser trust setup is needed for HWiNFO access in this mode.
