# Contributing to Labby

Thanks for your interest! Labby is a personal home-lab dashboard, but issues and pull requests are welcome.

## Getting started

- **Prerequisites:** .NET 9 SDK. No database setup needed — Labby creates its SQLite history DB on first run.
- **Build & run:** `dotnet run` from the repo root, then open the printed localhost URL. Integrations you haven't configured (QNAP, weather station, media services, …) degrade gracefully to "not configured" cards, so the app boots with an empty `appsettings.json`.
- **Configuration:** copy `.env.example` for the full list of settings; each integration is optional and documented in the README.

## Guidelines

- Keep new integrations optional: hide pages/cards when unconfigured rather than erroring.
- Secrets stay out of the repo — config comes from environment variables or `appsettings.*.json` (gitignored variants).
- There's no test suite; verify changes by running the app. `dotnet build` must pass with zero warnings.
- Match the existing style: file-scoped namespaces, primary constructors, scoped `.razor.css` for component styles.

## Reporting issues

Open an issue with what you expected, what happened, and relevant log output (`docker logs labby` or console output). For QNAP/media integrations, include the device model and firmware/app version — API quirks vary by version.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
