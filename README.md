# Labby 🏠

A Blazor Server web app for your home lab: service dashboard, QNAP NAS stats and file browser, Container Station control, and live readings from an Ambient Weather station.

## Pages

| Page | What it shows |
|---|---|
| **Dashboard** (`/`) | Weather strip, NAS quick stats, and tiles for every configured service with live up/down status and latency (polled every 30s) |
| **Storage** (`/storage`) | NAS model/firmware/uptime, CPU/RAM, temperatures, volume usage bars, and per-disk SMART health |
| **Files** (`/files`) | Browse QNAP shares and folders, download files through the app |
| **Containers** (`/containers`) | Embedded [Kontainr](https://github.com/chrisdfennell/Kontainr) dashboard (full Docker management), with the QNAP Container Station start/stop table as a second tab |
| **Weather** (`/weather`) | Full weather station readout, auto-refreshing every 60s |

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

`HealthUrl` is optional — use it when the probe should hit a different URL than the one the tile opens.

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

> ⚠️ There is no login on Labby itself yet — it can browse your NAS and stop containers, so keep it on your LAN (don't port-forward it) until auth is added.
