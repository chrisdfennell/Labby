---
name: verify
description: Build, run, and drive Labby (Blazor Server) locally to verify changes end-to-end over HTTP.
---

# Verifying Labby

Blazor Server app; the surface is HTTP on a local port. No test suite — verify by running.

## Build & run

```bash
cd "/f/Programming/C#/Labby"
dotnet build Labby.csproj -v q
# auth on (login screen) — set Auth__Password; auth off — omit it:
Auth__Password=test123 ASPNETCORE_URLS=http://127.0.0.1:5199 ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --no-launch-profile --no-build   # run in background, wait for /healthz = 200
```

QNAP/weather creds aren't needed to boot; unconfigured integrations degrade to "Not configured" cards. The two `Dashboard:Services` tiles in appsettings.json probe real LAN IPs, so health history populates within seconds of startup.

## Driving the login form (static SSR)

The form posts to `/login` with fields `_handler=login`, `Model.Username`, `Model.Password`, and `__RequestVerificationToken`. Token + antiforgery cookie come from GET /login:

```bash
TOKEN=$(curl -s -c /tmp/j.txt http://127.0.0.1:5199/login | grep -o 'name="__RequestVerificationToken" value="[^"]*"' | sed 's/.*value="//;s/"$//')
curl -s -b /tmp/j.txt -c /tmp/j.txt http://127.0.0.1:5199/login \
  --data-urlencode "_handler=login" --data-urlencode "__RequestVerificationToken=$TOKEN" \
  --data-urlencode "Model.Username=labby" --data-urlencode "Model.Password=test123" \
  -w "%{http_code} %{redirect_url}\n" -o /dev/null      # expect 302 → /
```

## Weather history / charts

Charts need rows in the SQLite history DB (`data/labby.db` under the content root).
Seed synthetic data with python stdlib (schema: `weather(observed_at INTEGER PRIMARY KEY, temp_f, feels_like_f, humidity, barometer, wind_mph, wind_gust_mph, daily_rain_in)`, unix seconds). Charts prerender, so `curl /weather` shows the SVG (`<polyline`, `data-chart=` meta) without a browser. Delete the seeded DB afterwards or it pollutes real history.

- Screenshots: headless Chrome works — `& "C:\Program Files\Google\Chrome\Application\chrome.exe" --headless=new --screenshot=out.png --window-size=1440,2000 --virtual-time-budget=15000 <url>` (the virtual-time budget lets the Blazor circuit connect, so even interactive-only content renders).
- Hover/tooltip/range buttons: `npm install playwright` (no browser download needed) + `chromium.launch({ executablePath: <chrome.exe> })`, then mouse.move over `.lab-chart` and read `.chart-tooltip` text.
- Alert webhook: run a python `http.server` handler logging POST bodies, set `Alerts__WebhookUrl` to it, point `Dashboard__Services__0__Url` at a second local listener, kill/restart that listener and watch for the 🔴/🟢 POSTs (30s poll cadence, transitions only).

## Worth checking after auth-adjacent changes

- `/healthz` → 200 "ok" with no cookie (must stay anonymous).
- `GET /` with no cookie → 302 to /login (auth on) or 200 (auth off).
- `POST /_blazor/negotiate?negotiateVersion=1` with the auth cookie → 200 JSON (interactive circuit alive).
- `POST /logout` without antiforgery token → 400; with token from any authenticated page → 302 /login.
- `?returnUrl=%2F%2Fevil.com` on login → must redirect to `/`, not off-host.
