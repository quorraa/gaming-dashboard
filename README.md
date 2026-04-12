# Gaming Dashboard

Touch-first LAN dashboard for a Windows gaming PC, designed for landscape use on phones and tablets.

## Projects

- `src/Monitor.Server`
  - Windows-only local dashboard server
  - reads HWiNFO, Core Audio, WMI, processes, and network telemetry from the gaming PC
  - serves the browser UI
- `src/Monitor.DiscordRelay`
  - hosted Discord companion service
  - keeps the Discord bot token off the gaming PC
  - connects to Discord once and exposes a small HTTP API the dashboard can poll

## Current architecture

- `Monitor.Server` stays on the gaming PC
- `Monitor.DiscordRelay` runs on an always-on Linux host
- the browser only talks to `Monitor.Server`
- `Monitor.Server` talks server-to-server to the relay for Discord data

## Local dashboard run

```powershell
dotnet run --project .\src\Monitor.Server
```

For LAN access:

```powershell
$env:ASPNETCORE_URLS="http://0.0.0.0:5103"
dotnet run --project .\src\Monitor.Server
```

Then open `http://<gaming-pc-lan-ip>:5103`.

Client routes:

- `http://<gaming-pc-lan-ip>:5103/studio/` for the richer Studio client
- `http://<gaming-pc-lan-ip>:5103/vanilla/` for the dependency-less Vanilla client

## Dashboard Discord settings

Open the dashboard `⚙` drawer and fill the `Discord Relay` section.

Minimum working fields:

- `Relay URL`
- `Guild ID`
- `Tracked user ID`

Optional:

- `Relay API key`
- `Messages channel ID`
- `Fallback voice channel ID`
- `Favorite user IDs`

The local dashboard no longer needs a Discord bot token.

User preferences are stored outside the app folder so portable updates do not wipe them:

```text
%LocalAppData%\GamingDashboard\dashboard.user.json
```

On first run, the app will migrate any legacy `dashboard.user.json` from the old app directory into that location.

Studio theme media:

- local image/video wallpapers are uploaded to the gaming PC and cached by the Studio service worker on the viewing device
- Pexels photo/video search is available in Studio after saving a Pexels API key locally

## Relay configuration

The bot token belongs on the relay host, not on the gaming PC.

Environment variables:

```bash
DiscordRelay__BotToken=your_discord_bot_token
DiscordRelay__ApiKey=shared_dashboard_api_key
DiscordRelay__StartupDelayMs=2500
DiscordRelay__MessageCacheSeconds=4
ASPNETCORE_URLS=http://0.0.0.0:8080
```

Health check:

```bash
curl http://127.0.0.1:8080/health
```

Discord endpoint:

```bash
curl -H "X-Relay-Key: your_api_key" \
  "http://127.0.0.1:8080/api/discord?guildId=123&trackedUserId=456"
```

## Oracle Ubuntu deploy with Podman

SSH in:

```powershell
ssh -i <path-to-private-key> <relay-user>@<relay-host>
```

Clone and build:

```bash
cd /opt
git clone https://github.com/quorraa/gaming-dashboard.git
cd gaming-dashboard/src/Monitor.DiscordRelay
sudo podman build -t gaming-dashboard-relay .
```

Run the relay:

```bash
sudo podman run -d \
  --name gaming-dashboard-relay \
  --restart=always \
  -p 8080:8080 \
  -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
  -e DiscordRelay__BotToken='YOUR_BOT_TOKEN' \
  -e DiscordRelay__ApiKey='YOUR_SHARED_API_KEY' \
  gaming-dashboard-relay
```

Check it:

```bash
curl http://127.0.0.1:8080/health
```

## Discord developer portal setup

1. Open https://discord.com/developers/applications
2. Create a new application or open the one you want to use
3. Go to `Bot`
4. Create the bot user if it does not exist yet
5. Copy the bot token and store it only on the relay host
6. Enable these intents:
   - `Server Members`
   - `Presence`
   - `Message Content` only if you want the `Latest` block
7. Invite the bot to your server with permission to view the relevant channels
8. Enable Discord `Developer Mode`
9. Copy:
   - your `Guild ID`
   - your own `User ID` for `Tracked user ID`
   - optional `Messages channel ID`
   - optional `Fallback voice channel ID`
   - optional `Favorite user IDs`
10. Put the bot token on the relay host
11. Put the relay URL and IDs into the dashboard drawer

## Portable build

Build a self-contained Windows package:

```powershell
.\scripts\publish-portable.ps1
```

That publishes to `.\artifacts\publish\win-x64-Release` and also creates a zip in `.\artifacts`.

Prepare a target machine:

```powershell
.\scripts\install-prereqs.ps1
```

What the install script does:

- installs HWiNFO through `winget` unless it is already installed
- creates a Windows Firewall allow rule for TCP `5103`

## HWiNFO setup

Run once after installing HWiNFO:

```powershell
.\scripts\configure-hwinfo-autostart.ps1 -RestartIfRunning
```

What the helper does:

- enables `SensorsOnly=1`
- enables `OpenSensors=1`
- enables `ServerRole=1`
- enables `SensorsSM=1`
- enables `MinimalizeSensors=1`
- enables `MinimalizeMainWnd=1`
- disables startup dialogs

The published Windows package is self-contained, so it does not require a separate .NET install.
