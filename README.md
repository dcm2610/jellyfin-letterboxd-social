# Jellyfin.Plugin.LetterboxdSocial

Jellyfin.Plugin.LetterboxdSocial scrapes configured public Letterboxd RSS feeds for review text and public Letterboxd films pages for ratings, caches friends' movie ratings and reviews in SQLite, and exposes them on Jellyfin movie detail pages.

## Build

This project targets Jellyfin 10.11.x and `net9.0`.

```powershell
dotnet publish -c Release -o .\dist\LetterboxdSocial
```

Copy the publish output folder into your Jellyfin plugins directory, then restart Jellyfin.

## GitHub Releases And Manifest

The repo root contains `manifest.json` for Jellyfin's plugin repository URL.

The manifest points at the release asset URL:

```text
https://github.com/dcm2610/jellyfin-letterboxd-social/releases/download/v1.0.0.37/LetterboxdSocial_1.0.0.37.zip
```

Upload `dist/LetterboxdSocial_1.0.0.37.zip` as that release asset. Jellyfin can use the raw GitHub URL for `manifest.json` as a permanent update manifest.

## Configure

Open Dashboard -> Plugins -> Letterboxd Social and add mappings for:

- Letterboxd username
- Display name

Run the scheduled task once manually from Dashboard -> Scheduled Tasks to populate the cache immediately.
