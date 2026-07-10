# Labby 🏠

A Blazor Server web app for your home lab: service dashboard, QNAP NAS stats and file browser, Container Station control, and live readings from an Ambient Weather station.

## Pages

| Page | What it shows |
|---|---|
| **Dashboard** (`/`) | Weather, NAS, and media-glance cards (auto-refreshing every 60s), plus tiles for every configured service with live up/down status, latency, a one-hour sparkline, uptime %, up/down duration, and a Wake-on-LAN button for down services with a configured MAC (polled every 30s) |
| **Storage** (`/storage`) | NAS model/firmware/uptime, CPU/RAM, temperatures, volume usage bars with a "full in ≈N days" projection, 24h CPU/RAM/temperature charts, and per-disk SMART health |
| **Files** (`/files`) | Browse QNAP shares and folders, download files through the app, upload files, and create folders |
| **Containers** (`/containers`) | Embedded [Kontainr](https://github.com/chrisdfennell/Kontainr) dashboard (full Docker management), plus a Container Station tab with per-container CPU/RAM, start/stop/restart, and a logs viewer (needs the docker.sock mount from the compose file) |
| **Media** (`/media`) | Plex now-playing (via Tautulli), recently added (Plex), active downloads with speeds and pause/resume (qBittorrent + NZBGet), the Sonarr/Radarr download queue, upcoming episodes/movies, and pending Overseerr requests — auto-refreshing every 15s |
| **Uptime** (`/uptime`) | Status-page view of every dashboard service: uptime % (24h/7d), a 30-day daily bar strip, and an outage log with durations — history persisted to SQLite |
| **Network** (`/network`) | Latency charts for pinged hosts (`Network:PingHosts`, 60s cadence, packet-loss %) and scheduled internet speed tests via bundled librespeed-cli (`Network:SpeedtestHours`, 0 = off; optional `MinDownloadMbps` slow-internet alert) |
| **Weather** (`/weather`) | Full weather station readout auto-refreshing every 60s, plus 24h/48h/7d history charts (temperature, wind, humidity, barometer) logged to a small SQLite file every 5 minutes |

## Setup

### 1. QNAP

Fill in the `Qnap` section. The app talks to the QTS web API (`authLogin.cgi`), File Station, and Container Station using one account.

```jsonc
"Qnap": {
  "Host": "192.168.1.50",   // NAS IP or hostname
  "Port": 8080,              // QTS web port (8080 http / 443 https by default)
  "UseHttps": false,
  "IgnoreCertificateErrors": true,
  "Username": "labby",
  "Password": "..."
}
```

Notes:
- Accounts with **2FA enabled cannot log in** through this API — create a dedicated account for Labby. Give it read access to the shares you want to browse, and Container Station access if you want start/stop.
- Rather than putting the password in `appsettings.json`, prefer user secrets during development:
  `dotnet user-secrets init` then `dotnet user-secrets set "Qnap:Password" "..."`
- The Container Station integration targets the v1 REST API. If your QTS runs Container Station 3 with v1 removed, the Containers page will say so.

### 2. Ambient Weather

Create an **API key** and **Application key** at [ambientweather.net/account](https://ambientweather.net/account), then:

```jsonc
"AmbientWeather": {
  "ApiKey": "...",
  "ApplicationKey": "...",
  "DeviceMac": ""            // optional; only needed if you have multiple stations
}
```

### 3. Kontainr (Containers page)

The docker-compose stack runs [Kontainr](https://github.com/chrisdfennell/Kontainr) alongside Labby on port **5124** and embeds it on the Containers page. Two things to know:

- `Kontainr:Url` (env `KONTAINR_URL`) is used as an **iframe `src` in your browser**, so it must be reachable from the device you're browsing on — use `http://<host-lan-ip>:5124` instead of `localhost` if you open Labby from other machines.
- Kontainr manages the Docker daemon whose socket it mounts (the machine running compose). To manage the QNAP's containers with it, add the NAS as a remote host inside Kontainr (it supports SSH/remote Docker hosts), or keep using the Container Station tab.

Leave `Kontainr:Url` empty to hide the embed and show only the Container Station table.

### 4. Dashboard services

Each entry becomes a tile with a health check (any HTTP response below 500 counts as "up", so auth-protected apps still show green):

```jsonc
"Dashboard": {
  "Services": [
    { "Name": "Plex",     "Url": "http://192.168.1.50:32400/web", "Icon": "🎬", "Description": "Media server" },
    { "Name": "Pi-hole",  "Url": "http://192.168.1.53/admin",     "Icon": "🕳️", "HealthUrl": "http://192.168.1.53/admin/login" },
    { "Name": "Router",   "Url": "http://192.168.1.1",            "Icon": "🌐" }
  ]
}
```

`HealthUrl` is optional — use it when the probe should hit a different URL than the one the tile opens. Add `"Mac": "AA:BB:CC:DD:EE:FF"` to a service and its tile grows a ⚡ wake button whenever it's down (Wake-on-LAN broadcast — works for machines whose BIOS/NIC have WoL enabled).

Plain bookmarks (no health checks) can sit in a strip above the service tiles:

```jsonc
"Dashboard": {
  "Links": [
    { "Name": "Router", "Url": "http://192.168.1.1", "Icon": "🌐" },
    { "Name": "Cloudflare", "Url": "https://dash.cloudflare.com", "Icon": "☁️" }
  ]
}
```

[NMMiner](https://github.com/NMminer1024/NMMiner)-style Bitcoin lottery miners get their own dashboard section with live hashrate, shares, best difficulty, uptime, and Wi-Fi signal:

```jsonc
"Dashboard": {
  "Miners": [
    { "Name": "NM Miner", "Url": "http://192.168.1.34" }
  ]
}
```

### 5. Media page (optional)

Each source is independent — configure the ones you run and their cards appear; the rest stay hidden:

```jsonc
"Media": {
  "Plex":        { "Url": "http://192.168.1.50:32400", "ApiKey": "..." }, // recently added (ApiKey = X-Plex-Token)
  "Tautulli":    { "Url": "http://192.168.1.50:8181", "ApiKey": "..." },  // Plex now-playing
  "Sonarr":      { "Url": "http://192.168.1.50:8989", "ApiKey": "..." },  // upcoming episodes
  "Radarr":      { "Url": "http://192.168.1.50:7878", "ApiKey": "..." },  // upcoming movies
  "Overseerr":   { "Url": "http://192.168.1.50:5055", "ApiKey": "..." },  // pending requests
  "Qbittorrent": { "Url": "http://192.168.1.50:8080", "Username": "admin", "Password": "" },
  "Nzbget":      { "Url": "http://192.168.1.50:6789", "Username": "nzbget", "Password": "" }
}
```

With Docker, use the `TAUTULLI_*` / `SONARR_*` / `RADARR_*` / `OVERSEERR_*` / `QBITTORRENT_*` / `NZBGET_*` variables in `.env`. API keys live in each app's settings UI (Sonarr/Radarr: Settings → General; Tautulli: Settings → Web Interface; Overseerr: Settings). An empty qBittorrent password works if its "bypass authentication for clients on whitelisted IPs" covers the Labby host.

### 6. Alerts (optional)

Labby posts a message whenever a dashboard service goes down or comes back. Two channels, use either or both:

```jsonc
"Alerts": {
  "WebhookUrl": "",      // e.g. https://ntfy.sh/my-homelab, Discord, or Slack
  "PushoverToken": "",   // Pushover application token (pushover.net/apps/build)
  "PushoverUser": ""     // your Pushover user key
}
```

Discord (`discord.com/api/webhooks/…`) and Slack (`hooks.slack.com/…`) URLs get their native JSON payloads; any other URL — an [ntfy](https://ntfy.sh) topic, a generic webhook — receives the message as a plain-text POST. For [Pushover](https://pushover.net), create an application (any name/icon) to get the token, grab your user key from the dashboard, and set both — alerts arrive as push notifications titled "Labby". With Docker: `LABBY_ALERT_WEBHOOK`, `LABBY_PUSHOVER_TOKEN`, `LABBY_PUSHOVER_USER` in `.env`. Alerts fire on state *changes* only (🔴 down with the error, 🟢 recovery with how long it was out).

With a webhook set and QNAP configured, Labby also checks **NAS health** every 15 minutes and alerts once when a condition appears and once when it clears: a disk's SMART health leaving "Good", a volume passing `Alerts:VolumeFullPercent` (default 90), or the CPU passing `Alerts:CpuTempC` (default 85°C; set either to 0 to disable).

Weather history lands in `data/labby.db` (override with `History:DatabasePath`); the compose files mount a `labby-data` volume so it survives rebuilds.

### 7. Login (optional)

Labby ships with a simple cookie login that is **off by default**. Set a password to turn it on:

```jsonc
"Auth": {
  "Username": "labby",   // default
  "Password": ""          // empty = no login screen
}
```

With Docker, set `LABBY_AUTH_USERNAME` / `LABBY_AUTH_PASSWORD` in `.env`. Once enabled, every page (and file downloads) requires signing in; the session cookie lasts 30 days and a logout button appears in the nav. `/healthz` stays open for health checks.

Labby can browse your NAS and stop containers, so even with the login enabled it's best kept on your LAN — the login protects against curious housemates, not the open internet (no HTTPS, no rate limiting).

## Run

### Locally

```
dotnet run
```

Then open the URL it prints.

### Docker

```
copy .env.example .env    # fill in your NAS + weather credentials
docker compose up -d --build
```

Labby is then at [http://localhost:5123](http://localhost:5123). Config comes from environment variables in `docker-compose.yml` (any `appsettings.json` key works with `__` as the separator, e.g. `Dashboard__Services__0__Name`). Dashboard tiles are easiest to edit in `appsettings.json` before building the image.

### On the QNAP itself (Container Station)

Container Station is Docker under the hood, but it can't build images from source — so build on your PC and ship the images over:

1. **Build and export on your PC** (from this folder):
   ```
   docker compose build
   docker save labby-labby labby-kontainr-proxy -o labby-images.tar
   ```
2. **Copy to the NAS**: drop `labby-images.tar`, `docker-compose.nas.yml`, and your `.env` into a share (e.g. `\\<nas>\Public\labby\`).
3. **Load and start over SSH** (enable SSH in QTS → Control Panel → Telnet/SSH):
   ```
   ssh youruser@<nas-ip>
   cd /share/Public/labby
   docker load -i labby-images.tar
   docker compose -f docker-compose.nas.yml up -d
   ```
   (Alternatively, paste the YAML into Container Station → **Applications → Create** and set the env values inline.)
4. **Adjust `.env` for the NAS**: `KONTAINR_URL=http://<nas-ip>:5124` (browser-loaded iframe URL), and `QNAP_HOST=<nas-ip>` still works from inside the containers.

Labby is then at `http://<nas-ip>:5123`. Notes:
- Ports 5123/5124 don't clash with QTS defaults (8080/443). Change them in the compose file if you use them.
- Images are built for `linux/amd64` — fine for Intel/AMD QNAPs. If your model is ARM, build with `docker buildx build --platform linux/arm64` instead.
- Kontainr mounts the NAS's Docker socket, so it manages Container Station's own containers — no remote-host setup needed.
- To update: rebuild + re-save the tar on the PC, `docker load` again, then `docker compose -f docker-compose.nas.yml up -d`.

## Health checks

`GET /healthz` returns `200 ok` without authentication, and the Docker image has a built-in `HEALTHCHECK` against it — `docker ps` shows the container as `healthy`/`unhealthy`, and you can point Uptime Kuma (or a Labby dashboard tile on another instance) at it.

> ⚠️ Labby can browse your NAS and stop containers. Enable the login (section 7) and keep it on your LAN — don't port-forward it.
