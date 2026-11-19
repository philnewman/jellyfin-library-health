# Jellyfin Library Health

A read-only health report for your Jellyfin music library. It adds a **Library Health** page to the dashboard that lists metadata problems, grouped by type, each linking to the album, track or artist so you can fix it in Jellyfin's own editor.

**It never changes your library.** The only thing it saves is the list of findings you mark as "not a problem".

## What it checks

| Check | What it finds |
|---|---|
| Artist spelled more than one way | `Keb' Mo'` / `Keb Mo'`, `Hüsker Dü` / `Husker Du`, `The BoDeans` / `BoDeans`. Empty leftover artist entries are labelled as safe to ignore. |
| Album name doesn't match its tracks | The album's own name differs from the album tag on its tracks. |
| Album artist doesn't match its tracks | None of the tracks credit the album artist. |
| Track numbers used twice | Two tracks claim the same disc and track number. |
| Missing track numbers | Tracks with no track number, or 0. |
| Tracks with no artist | Empty artist field. |
| Possible duplicates | Same title and nearly the same length within one album. |
| Misfiled tracks | A track in another artist's folder, or loose at the top of the library. Folders like "Various Artists" are skipped (configurable). |
| Garbled titles | `Don_t` for `Don't`, copy numbers like `(2)`, extra spaces, a file name as the title. |
| Albums without cover art | No primary image. |
| Missing genre or year | Tracks without a genre or year. |

Artist and folder names are compared ignoring case, accents, punctuation, word order, a leading "The", and small typos.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   - Name: `Library Health`
   - URL: `https://raw.githubusercontent.com/philnewman/jellyfin-library-health/main/manifest.json`
2. Open **Catalog**, find **Library Health** under General, install it and restart Jellyfin.
3. Open **Library Health** in the dashboard menu.

The first visit scans the library (a few seconds for about 12,000 tracks). **Scan again** refreshes it after you've fixed things.

## API

All endpoints need an administrator.

- `GET /LibraryHealth/Report` — the last report; `?rescan=true` scans again.
- `POST /LibraryHealth/Dismissed?key=KEY&dismissed=true|false` — hide a finding or bring it back.

## Build

Targets .NET 10 and Jellyfin 12.2. CI builds on every push; pushing a `v*` tag attaches the zip to a release and adds it to `manifest.json`.

```bash
dotnet publish Jellyfin.Plugin.LibraryHealth -c Release -o out
```

## License

MIT
