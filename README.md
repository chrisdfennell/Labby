# Labby 🏠

A Blazor Server web app for your home lab: service dashboard, QNAP NAS stats and file browser, Container Station control, and live readings from an Ambient Weather station.

## Pages

| Page | What it shows |
|---|---|
| **Dashboard** (`/`) | Weather, NAS, and media-glance cards (auto-refreshing every 60s), plus tiles for every configured service with live up/down status, latency, a one-hour sparkline, uptime %, up/down duration, and a Wake-on-LAN button for down services with a configured MAC (polled every 30s) |
| **Storage** (`/storage`) | NAS model/firmware/uptime, CPU/RAM, temperatures, volume usage bars with a "full in ≈N days" projection, 24h CPU/RAM/temperature charts, and per-disk SMART health |
| **Files** (`/files`) | Browse QNAP shares, download/upload files, create folders, rename/move/delete, and generate signed 7-day share links that work without a login |
| **Containers** (`/containers`) | Embedded [Kontainr](https://github.com/chrisdfennell/Kontainr) dashboard (full Docker management), plus a Container Station tab with per-container CPU/RAM, start/stop/restart, and a logs viewer (needs the docker.sock mount from the compose file) |
| **Media** (`/media`) | Plex now-playing (via Tautulli), recently added (Plex), active downloads with speeds and pause/resume (qBittorrent + NZBGet), the Sonarr/Radarr download queue, add-download-by-link (magnet/NZB), stop-stream buttons, upcoming episodes/movies, pending Overseerr requests with approve/decline, search across Overseerr **and Radarr/Sonarr directly** (request via Overseerr, or add straight to an Arr with a chosen root folder and quality profile and search for it), **gaps in the library** (monitored movies with no file, aired episodes with no file grouped into seasons, and half-finished movie collections — each with a one-click search or add), the **ErsatzTV channel lineup** with what each channel is airing now and next (read from its XMLTV guide) plus stop-session and rebuild-playout buttons, and 30-day watch statistics (plays chart, top shows/movies/users) — auto-refreshing every 15s |
| **Weather** (`/weather`) | Full weather station readout auto-refreshing every 60s, today's high/low/peak-gust/rain/UV/solar and sunrise/sunset tiles, an animated rain radar (RainViewer) centered on the station, and 24h/48h/7d history charts — temperature, wind (with direction arrows and "wind from" tooltips), humidity, barometer, rain, solar radiation, indoor vs outdoor — logged to SQLite every 5 minutes |
| **Uptime** (`/uptime`) | Status-page view of every dashboard service: uptime % (24h/7d), a 30-day daily bar strip, and an outage log with durations — history persisted to SQLite |
| **Network** (`/network`) | Latency charts for pinged hosts (`Network:PingHosts`, 60s cadence, packet-loss %) and scheduled internet speed tests via bundled librespeed-cli (`Network:SpeedtestHours`, 0 = off; optional `MinDownloadMbps` slow-internet alert) |
| **Terminal** (`/terminal`) | Embeds any web terminal (e.g. a web-SSH container) via `Terminal:Url` / env `TERMINAL_URL` — browser-loaded iframe, hidden when unset |
| **VS Code** (`/vscode`) | Embeds a [code-server](https://github.com/coder/code-server) / VS Code Web instance via `VsCode:Url` / env `VSCODE_URL` — browser-loaded iframe |
| **Trends** (`/trends`) | Any stored metric charted over 24h/7d/30d: NAS CPU/RAM/temps, ping RTT, hashrate, speedtests, volume usage |
| **Notes** (`/notes`) | Markdown notes/runbooks persisted to SQLite, with live preview |
| **Wishlist** (`/wishlist`) | Embeds a wishlist app via `Wishlist:Url` / env `WISHLIST_URL` (append `?token=…` if it is token-protected) — browser-loaded iframe, hidden when unset. Loads the app's `wishlist-embed.js` helper so the frame grows to fit the list instead of scrolling inside a box |

Everywhere: **Ctrl+K / Cmd+K** opens a command palette that jumps to any page, service, or quick link. Labby also ships a web-app manifest, so "Add to Home Screen" on a phone gets a proper icon and standalone window (full PWA install requires HTTPS — put Labby behind a reverse proxy with a certificate if you want that).

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
  "Password": "...",
  "DropPath": "/Public/Drop" // where the Drop page uploads to; created on first drop
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

Probes run every 30s, and a service is only called DOWN (and alerted on) after `FailuresBeforeDown` failures **in a row** — 2 by default, so one dropped connection or timeout doesn't produce a DOWN/UP alert pair 30 seconds apart. Raise it globally, or per service for one flaky box:

```jsonc
"Dashboard": {
  "FailuresBeforeDown": 2,
  "Services": [
    // an ESP32 miner's web server drops overlapping requests — give it more rope
    { "Name": "NM Monitor", "Url": "http://192.168.1.34/", "Icon": "⛏️", "FailuresBeforeDown": 3 }
  ]
}
```

Each retained failure costs one poll interval of detection delay (2 → up to ~60s). Set it to `1` to alert on the very first bad probe. The sparkline and uptime % still count every failed probe, so suppressed blips remain visible even when no alert fires.

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
  "Sonarr":      { "Url": "http://192.168.1.50:8989", "ApiKey": "..." },  // upcoming episodes, queue, series search + add, missing episodes
  "Radarr":      { "Url": "http://192.168.1.50:7878", "ApiKey": "..." },  // upcoming movies, queue, movie search + add, missing movies + collection gaps
  "Overseerr":   { "Url": "http://192.168.1.50:5055", "ApiKey": "..." },  // pending requests, search + request
  "ErsatzTv":    { "Url": "http://192.168.1.50:8409" },                   // channel lineup with now/next (no API key — its API is unauthenticated)
  "Qbittorrent": { "Url": "http://192.168.1.50:8080", "Username": "admin", "Password": "" },
  "Nzbget":      { "Url": "http://192.168.1.50:6789", "Username": "nzbget", "Password": "" }
}
```

With Docker, use the `TAUTULLI_*` / `SONARR_*` / `RADARR_*` / `OVERSEERR_*` / `ERSATZTV_*` / `QBITTORRENT_*` / `NZBGET_*` variables in `.env`. API keys live in each app's settings UI (Sonarr/Radarr: Settings → General; Tautulli: Settings → Web Interface; Overseerr: Settings). An empty qBittorrent password works if its "bypass authentication for clients on whitelisted IPs" covers the Labby host.

### 6. Alerts (optional)

Labby posts a message whenever a dashboard service goes down or comes back. Two channels, use either or both:

```jsonc
"Alerts": {
  "WebhookUrl": "",      // e.g. https://ntfy.sh/my-homelab, Discord, or Slack
  "PushoverToken": "",   // Pushover application token (pushover.net/apps/build)
  "PushoverUser": ""     // your Pushover user key
}
```

Discord (`discord.com/api/webhooks/…`) and Slack (`hooks.slack.com/…`) URLs get their native JSON payloads; any other URL — an [ntfy](https://ntfy.sh) topic, a generic webhook — receives the message as a plain-text POST. For [Pushover](https://pushover.net), create an application (any name/icon) to get the token, grab your user key from the dashboard, and set both — alerts arrive as push notifications titled "Labby". With Docker: `LABBY_ALERT_WEBHOOK`, `LABBY_PUSHOVER_TOKEN`, `LABBY_PUSHOVER_USER` in `.env`. Alerts can be snoozed for maintenance windows from Settings (history still records them). They fire on state *changes* only (🔴 down with the error, 🟢 recovery with how long it was out).

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

### Pull-based updates (Docker Hub)

A GitHub Actions workflow builds and pushes `fennch/labby` (and the kontainr proxy image) on every commit to main. With the repo secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` set, the NAS never has to compile anything:

```
docker compose -f docker-compose.hub.yml pull
docker compose -f docker-compose.hub.yml up -d
```

### Deploy on push (CI hook)

To skip the manual pull, let the workflow tell the running Labby to update itself as soon as the new image is published. It runs exactly what Settings → **Updates** → *Update now* runs (a one-shot Watchtower), so there's no second update path to maintain.

1. Generate a secret: `openssl rand -hex 32`.
2. On the NAS, set `LABBY_DEPLOY_TOKEN` in `.env` (the compose files pass it through as `Updates__DeployToken`) and recreate the container. Settings → Updates then reads **CI deploy hook enabled**.
3. In GitHub → Settings → Secrets → Actions, add:
   - `LABBY_DEPLOY_URL` — Labby's base URL as reachable from GitHub's runners, e.g. `https://labby.example.org` (no trailing slash; the step appends `/api/deploy`).
   - `LABBY_DEPLOY_TOKEN` — the same secret.

With both set, every push to `main` builds, pushes, and then POSTs `/api/deploy`; Labby restarts on the new image about half a minute later. Leave either secret unset and the step prints "skipping" and passes, so forks and local-only deployments still get green builds.

`POST /api/deploy` is anonymous by necessity — CI has no session — so the bearer token is the entire authorization. With no token configured the endpoint answers 404 and does nothing, which is the default. It only exposes "pull the image this repo just published and restart", but it is reachable by anyone who can reach Labby, so treat the token like a password and don't publish the URL. Bad token → `401`, no Docker socket → `503`, accepted → `202`.

Check the whole path before wiring up secrets, from a machine **off** your LAN:

```
curl -i -X POST https://labby.example.org/api/deploy -H "Authorization: Bearer wrong"
```

`401` with a JSON body means everything between GitHub and Labby works. Anything else, read the `Location` header:

| Response | Cause |
|---|---|
| `404` + JSON | `Updates__DeployToken` unset, or the container wasn't recreated after setting it |
| `302` → `*.cloudflareaccess.com` | Cloudflare Access is in front — see below |
| `302` → `/login` | reaching an older image that predates this endpoint |
| timeout / refused | not publicly reachable at that hostname |

#### Behind Cloudflare Access

Access challenges GitHub's runners with a login redirect, which the deploy step reports as an error. Two ways through:

- **Service token** (keeps Access enforcing). Zero Trust → Access → Service Auth → create a service token; on the Labby application add a policy with action *Service Auth* including that token. Put the two values in the `CF_ACCESS_CLIENT_ID` and `CF_ACCESS_CLIENT_SECRET` repo secrets and the workflow sends them automatically.
- **Bypass policy** on just the hook. Create a second Access application for `labby.example.org/api/deploy` with a *Bypass* policy, leaving the rest of Labby protected. Simpler, and the endpoint still needs the bearer token — but it is then open to the internet, so pair it with a WAF rule if that matters.

### Restart and rebuild from Settings

Settings → **Labby container** drives the container Labby itself runs in over the mounted `docker.sock`:

- **Restart container** bounces it — a few seconds of downtime, and the page reconnects on its own.
- **Rebuild & recreate** builds the image from the source in the compose project's directory and recreates the container with it. Labby can't build itself, so the work runs in a detached `docker:cli` helper (container `labby-rebuild`) that gets the socket and the project directory bind-mounted, and runs `docker compose build` then `up -d` for Labby's own service. Which project that is comes from the compose labels on the running container, so nothing needs configuring. The build takes a few minutes; a failed build leaves the running Labby alone, and **Build log** shows what the helper printed.

Both need `/var/run/docker.sock` mounted (every compose file here does it). Rebuild also needs the source next to the compose file — on a `docker-compose.hub.yml` deployment there's nothing to build from, so use the update button instead.

### HTTPS via a reverse proxy

Labby serves plain HTTP; for HTTPS (which unlocks real PWA install and the clipboard API), front it with a proxy like nginx-proxy-manager:

1. In NPM, add a proxy host: domain of your choice → scheme `http`, forward host `192.168.86.57` (or your NAS IP), forward port `5123`. Enable *Websockets support* (Blazor needs it).
2. Attach a certificate (Let's Encrypt DNS challenge works for real domains; a self-signed cert works LAN-only).
3. Labby already honors `X-Forwarded-Proto`/`X-Forwarded-For`, so cookies and redirects behave behind the proxy.

## Health checks

`GET /healthz` returns `200 ok` without authentication, and the Docker image has a built-in `HEALTHCHECK` against it — `docker ps` shows the container as `healthy`/`unhealthy`, and you can point Uptime Kuma (or a Labby dashboard tile on another instance) at it.

> ⚠️ Labby can browse your NAS and stop containers. Enable the login (section 7) and keep it on your LAN — don't port-forward it.
