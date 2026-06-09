# Jellyfin.Plugin.LetterboxdSocial

> **Proof of concept — not production ready.**
> This plugin is experimental and should not be used on live Jellyfin servers. It scrapes a third-party website, has no official API backing, and may break without warning if Letterboxd changes its HTML structure or access policies. Use at your own risk.

A Jellyfin server plugin that brings your Letterboxd social feed into your media library. When you open a movie in Jellyfin, the plugin shows a **Letterboxd Friends** widget on the detail page — displaying ratings, watched status, and reviews from the Letterboxd accounts you configure.

---

## What It Does

- Shows a **Letterboxd Friends** widget on movie detail pages in Jellyfin Web.
- Displays each configured friend's star rating, watched status, and review text (if they wrote one).
- Spoiler warnings are shown for reviews marked as containing spoilers, with a reveal toggle.
- Friend avatars are fetched from their Letterboxd profiles.
- Data is cached locally in SQLite so the widget loads instantly from cache on repeat visits.
- A background scheduled task refreshes the cache hourly.
- On-demand checks run when you open a movie that has missing friends in the cache, so new ratings surface without waiting for the next scheduled run.

## How It Works

The plugin has no access to Letterboxd's API (there isn't a public one). Instead it scrapes public Letterboxd pages:

- `/username/films/` and subsequent pages for ratings and watched status
- `/username/film/slug/` for full review text when available
- RSS feeds for recent review text
- Profile pages for avatars

Scraped data is stored in a local SQLite database under the Jellyfin plugin data folder. The Jellyfin Web frontend receives a small JavaScript widget that is injected into movie detail pages and reads from this cache via a plugin API endpoint.

Letterboxd imposes anti-scraping measures on paginated film pages. The plugin uses browser-like headers and a realistic referrer chain to access deeper pages, but this is inherently fragile — Letterboxd can and does block or change these patterns.

## Requirements

- Jellyfin 10.11.x
- .NET 9 runtime (included in the Jellyfin server environment)
- Configured Letterboxd accounts must have **public profiles**

## Installing via Plugin Repository

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**.
2. Add the following URL as a new repository:
   ```
   https://raw.githubusercontent.com/dcm2610/jellyfin-letterboxd-social/main/manifest.json
   ```
3. Go to **Dashboard → Plugins → Catalogue**, find **Letterboxd Social**, and install it.
4. Restart Jellyfin when prompted.

## Installing Manually

1. Download the latest zip from the [Releases](https://github.com/dcm2610/jellyfin-letterboxd-social/releases) page.
2. Extract the zip into your Jellyfin plugins folder (e.g. `/var/lib/jellyfin/plugins/LetterboxdSocial` on Linux).
3. Restart Jellyfin.

## Configuration

1. Go to **Dashboard → Plugins → Letterboxd Social**.
2. Add one entry per Letterboxd account you want to track:
   - **Letterboxd Username** — the username as it appears in their profile URL (e.g. `letterboxd.com/username`)
   - **Display Name** — the name shown on the widget card
3. Optionally adjust:
   - **Max Film Pages Per User** — how many pages of a user's film grid to check during on-demand searches (default: 3). Higher values find older films but are slower.
   - **Request Delay (ms)** — pause between Letterboxd requests to reduce the chance of rate limiting (default: 1000ms).
4. Save, then go to **Dashboard → Scheduled Tasks** and run **Refresh Letterboxd Social Reviews** manually to populate the cache immediately.

## Scheduled Tasks

| Task | Default interval | Purpose |
|------|-----------------|---------|
| Refresh Letterboxd Social Reviews | Every hour | Full scrape of all configured users |
| Reset Letterboxd Direct Check Cache | Manual only | Clears on-demand miss markers — useful if you suspect stale results |

## Known Limitations

- **Scraping only** — no official API. Any Letterboxd site change can break scraping silently.
- **Public profiles only** — private Letterboxd accounts cannot be read.
- **Ratings-only entries on older pages** — if a friend watched a film a long time ago and never wrote a review, it may sit beyond the reachable page range.
- **On-demand widget update** — the widget reliably shows cached data. Triggering a live on-demand search and updating the widget in the same page load without a refresh is not fully reliable (work in progress).
- **No official Jellyfin endorsement** — this is a personal project, not affiliated with Jellyfin or Letterboxd.

## Building From Source

```powershell
# Using the local .NET SDK bundled with the repo
.\.dotnet\dotnet.exe publish .\Jellyfin.Plugin.LetterboxdSocial.csproj -c Release -o .\dist\LetterboxdSocial
```

Copy `dist\LetterboxdSocial\` into your Jellyfin plugins directory and restart.
