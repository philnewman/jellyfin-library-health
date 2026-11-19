using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.LibraryHealth.Services;

/// <summary>
/// Reads the music library and lists metadata problems. Nothing here writes to the library.
/// </summary>
public class HealthScanner
{
    private static readonly Regex UnderscoreApostrophe = new(@"[A-Za-z]_(s|t|re|ll|ve|d|m)\b", RegexOptions.Compiled);
    private static readonly Regex CopySuffix = new(@"\(\d\)\s*$", RegexOptions.Compiled);
    private static readonly Regex FileNameTitle = new(@"\.(mp3|flac|m4a|aac|ogg|opus|wav|wma)\s*$|^\d-\d\d\s", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HealthScanner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthReport? _last;

    public HealthScanner(ILibraryManager libraryManager, ILogger<HealthScanner> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>The last report, or a fresh one if there isn't one yet.</summary>
    public HealthReport GetReport(bool rescan)
    {
        _gate.Wait();
        try
        {
            if (rescan || _last is null)
            {
                var started = DateTime.UtcNow;
                _last = Scan();
                _logger.LogInformation(
                    "Library Health: scanned {Tracks} tracks in {Seconds:F1} s, {Count} findings",
                    _last.TrackCount,
                    (DateTime.UtcNow - started).TotalSeconds,
                    _last.Findings.Count);
            }

            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    private HealthReport Scan()
    {
        var config = Plugin.Instance?.Configuration;
        var shared = (config?.SharedFolderNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Norm)
            .ToHashSet();

        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => l.TrimEnd('/', '\\'))
            .OrderByDescending(l => l.Length)
            .ToList();

        var tracks = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            IsVirtualItem = false
        }).OfType<AudioItem>().ToList();

        var albums = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true
        }).OfType<MusicAlbum>().ToDictionary(a => a.Id);

        var artists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist }
        }).OfType<MusicArtist>().ToList();

        var findings = new List<Finding>();
        CheckArtistNames(tracks, artists, findings);
        foreach (var group in tracks.GroupBy(t => t.ParentId))
        {
            albums.TryGetValue(group.Key, out var album);
            CheckAlbum(album, group.ToList(), findings);
            CheckTitles(album, group.ToList(), findings);
        }

        CheckMisfiled(tracks, roots, shared, findings);
        foreach (var album in albums.Values)
        {
            if (!album.HasImage(ImageType.Primary, 0))
            {
                findings.Add(new Finding("artwork", album.Id.ToString("N"), album.Id, AlbumLabel(album), "No cover art."));
            }
        }

        var dismissed = (config?.DismissedKeys ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            f.Dismissed = dismissed.Contains(f.Key);
        }

        return new HealthReport
        {
            GeneratedAt = DateTime.UtcNow,
            TrackCount = tracks.Count,
            AlbumCount = albums.Count,
            ArtistCount = artists.Count,
            Findings = findings
        };
    }

    // One artist, several spellings: "Keb' Mo'" / "Keb Mo'", "Hüsker Dü" / "Husker Du", "The BoDeans" / "BoDeans".
    private static void CheckArtistNames(List<AudioItem> tracks, List<MusicArtist> artists, List<Finding> findings)
    {
        var trackCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            foreach (var name in TrackArtists(t).Distinct(StringComparer.Ordinal))
            {
                trackCounts[name] = trackCounts.GetValueOrDefault(name) + 1;
            }
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var a in artists)
        {
            if (!string.IsNullOrEmpty(a.Name))
            {
                ids.TryAdd(a.Name, a.Id);
            }
        }

        var names = trackCounts.Keys.Concat(ids.Keys).Distinct(StringComparer.Ordinal);
        foreach (var group in names.GroupBy(Norm).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var spellings = group
                .Select(n => new { Name = n, Count = trackCounts.GetValueOrDefault(n) })
                .OrderByDescending(s => s.Count)
                .ToList();
            // Empty leftover entries can't be fixed (and artists must never be deleted), so only
            // report names where at least two spellings are really in use.
            if (spellings.Count(x => x.Count > 0) < 2)
            {
                continue;
            }

            var main = spellings[0];
            var detail = string.Join(", ", spellings.Select(s => string.Create(
                CultureInfo.InvariantCulture,
                $"\"{s.Name}\" ({(s.Count == 0 ? "no tracks; an empty leftover entry, safe to ignore" : Plural(s.Count, "track"))})")));
            var linkId = ids.TryGetValue(main.Name, out var id) ? id : (Guid?)null;
            findings.Add(new Finding("artist-name", group.Key, linkId, main.Name, "Spelled " + group.Count() + " ways: " + detail + "."));
        }
    }

    private static void CheckAlbum(MusicAlbum? album, List<AudioItem> tracks, List<Finding> findings)
    {
        var first = tracks[0];
        var linkId = album?.Id ?? first.Id;
        var key = linkId.ToString("N");
        var label = album is not null ? AlbumLabel(album) : (string.IsNullOrEmpty(first.Album) ? "(no album) " + Path.GetDirectoryName(first.Path) : first.Album);
        var total = tracks.Count;

        var unnumbered = tracks.Where(t => t.IndexNumber is null or 0).ToList();
        if (unnumbered.Count > 0)
        {
            findings.Add(new Finding("track-number", key, linkId, label, Of(unnumbered.Count, total) + " have no track number" + Sample(unnumbered) + "."));
        }

        var clashes = tracks
            .Where(t => t.IndexNumber is > 0)
            .GroupBy(t => (Disc: t.ParentIndexNumber is null or 0 ? 1 : t.ParentIndexNumber.Value, Track: t.IndexNumber!.Value))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Disc).ThenBy(g => g.Key.Track)
            .ToList();
        if (clashes.Count > 0)
        {
            var shown = clashes.Take(4).Select(g => string.Create(
                CultureInfo.InvariantCulture,
                $"disc {g.Key.Disc} track {g.Key.Track}: {string.Join(" / ", g.Take(3).Select(t => Quote(t.Name)))}{(g.Count() > 3 ? $" and {g.Count() - 3} more" : string.Empty)}"));
            findings.Add(new Finding(
                "track-clash",
                key,
                linkId,
                label,
                Plural(clashes.Count, "track number") + " used more than once (" + string.Join("; ", shown) + (clashes.Count > 4 ? "; …" : string.Empty) + ")."));
        }

        var noArtist = tracks.Where(t => t.Artists is null || t.Artists.Count == 0 || t.Artists.All(string.IsNullOrWhiteSpace)).ToList();
        if (noArtist.Count > 0)
        {
            findings.Add(new Finding("no-artist", key, linkId, label, Of(noArtist.Count, total) + " have no artist" + Sample(noArtist) + "."));
        }

        var dupes = new List<string>();
        foreach (var g in tracks.GroupBy(t => Norm(t.Name)).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var lengths = g.Select(t => (t.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond).OrderBy(x => x).ToList();
            if (lengths[^1] - lengths[0] <= 3)
            {
                dupes.Add(Quote(g.First().Name) + " ×" + g.Count());
            }
        }

        if (dupes.Count > 0)
        {
            findings.Add(new Finding(
                "duplicate",
                key,
                linkId,
                label,
                "Same title and length more than once: " + string.Join(", ", dupes.Take(5)) + (dupes.Count > 5 ? ", …" : string.Empty) + ". Compare the files before removing anything."));
        }

        var noGenre = tracks.Count(t => t.Genres is null || t.Genres.Length == 0);
        var noYear = tracks.Count(t => t.ProductionYear is null or 0);
        if (noGenre > 0 || noYear > 0)
        {
            var parts = new List<string>();
            if (noGenre > 0)
            {
                parts.Add(Of(noGenre, total) + " have no genre");
            }

            if (noYear > 0)
            {
                parts.Add(Of(noYear, total) + " have no year");
            }

            findings.Add(new Finding("genre-year", key, linkId, label, string.Join("; ", parts) + "."));
        }

        if (album is null)
        {
            return;
        }

        // The album's own name or album artist doesn't match its tracks (a stale container name).
        var tagNames = tracks.Select(t => t.Album ?? string.Empty).Where(n => n.Length > 0).GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (tagNames is not null && !SameName(tagNames.Key, album.Name))
        {
            findings.Add(new Finding(
                "album-name",
                key,
                linkId,
                label,
                "The album is called " + Quote(album.Name) + " but its tracks say " + Quote(tagNames.Key) + " (" + Of(tagNames.Count(), total) + ")."));
        }

        var albumArtists = album.AlbumArtists ?? Array.Empty<string>();
        var trackNames = tracks.SelectMany(TrackArtists).Select(Norm).ToHashSet();
        if (albumArtists.Count > 0 && trackNames.Count > 0
            && !albumArtists.Any(a => trackNames.Contains(Norm(a)) || Norm(a) == "variousartists"))
        {
            findings.Add(new Finding(
                "album-artist",
                key + ":aa",
                linkId,
                label,
                "Album artist is " + string.Join(", ", albumArtists.Select(Quote)) + ", but none of its tracks credit them (tracks say "
                + string.Join(", ", tracks.SelectMany(t => t.Artists ?? Array.Empty<string>()).Distinct().Take(3).Select(Quote)) + ")."));
        }
    }

    // A track sitting in another artist's folder, or loose at the top of the library.
    private static void CheckMisfiled(List<AudioItem> tracks, List<string> roots, HashSet<string> shared, List<Finding> findings)
    {
        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.Path))
            {
                continue;
            }

            var root = roots.FirstOrDefault(r => t.Path.StartsWith(r + "/", StringComparison.Ordinal) || t.Path.StartsWith(r + "\\", StringComparison.Ordinal));
            if (root is null)
            {
                continue;
            }

            var relative = t.Path.Substring(root.Length + 1);
            var parts = relative.Split('/', '\\');
            if (parts.Length == 1)
            {
                findings.Add(new Finding("misfiled", t.Id.ToString("N"), t.Id, TrackLabel(t), "Sits loose at the top of the library, not in an artist folder."));
                continue;
            }

            var folder = parts[0];
            if (shared.Contains(Norm(folder)))
            {
                continue;
            }

            var names = TrackArtists(t).ToList();
            if (names.Count == 0 || names.Any(n => SameName(n, folder)))
            {
                continue;
            }

            findings.Add(new Finding(
                "misfiled",
                t.Id.ToString("N"),
                t.Id,
                TrackLabel(t),
                "By " + string.Join(", ", (t.Artists ?? Array.Empty<string>()).Select(Quote)) + " but filed in the " + Quote(folder) + " folder."));
        }
    }

    // Garbled titles, one finding per album so a badly ripped album is one entry, not twenty.
    private static void CheckTitles(MusicAlbum? album, List<AudioItem> tracks, List<Finding> findings)
    {
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            var name = t.Name ?? string.Empty;
            string? problem = null;
            if (name.Length > 0 && name != name.Trim())
            {
                problem = "extra spaces at the start or end";
            }
            else if (UnderscoreApostrophe.IsMatch(name))
            {
                problem = "an underscore where an apostrophe belongs";
            }
            else if (CopySuffix.IsMatch(name))
            {
                problem = "a copy number like \"(2)\"";
            }
            else if (FileNameTitle.IsMatch(name))
            {
                problem = "a file name as the title";
            }

            if (problem is not null)
            {
                if (!problems.TryGetValue(problem, out var list))
                {
                    problems[problem] = list = new List<string>();
                }

                list.Add(Quote(name));
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        var first = tracks[0];
        var linkId = album?.Id ?? first.Id;
        var label = album is not null ? AlbumLabel(album) : (string.IsNullOrEmpty(first.Album) ? TrackLabel(first) : first.Album);
        var detail = string.Join("; ", problems.Select(p =>
            Of(p.Value.Count, tracks.Count) + " with " + p.Key + " (" + string.Join(", ", p.Value.Take(3)) + (p.Value.Count > 3 ? ", …" : string.Empty) + ")"));
        findings.Add(new Finding("title", linkId.ToString("N"), linkId, label, char.ToUpperInvariant(detail[0]) + detail.Substring(1) + "."));
    }

    private static IEnumerable<string> TrackArtists(AudioItem t)
    {
        return (t.Artists ?? Array.Empty<string>()).Concat(t.AlbumArtists ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n));
    }

    /// <summary>Two names that differ only in case, accents, punctuation, word order, a leading "The" or a small typo.</summary>
    private static bool SameName(string a, string b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        if (na == nb)
        {
            return true;
        }

        if (na.Length > 3 && nb.Length > 3 && (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal)))
        {
            return true;
        }

        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count > 1 && ta.SetEquals(tb))
        {
            return true;
        }

        var longer = Math.Max(na.Length, nb.Length);
        return longer >= 8 && Distance(na, nb) <= longer / 8;
    }

    /// <summary>Lower case, no accents, no punctuation or spaces, "&amp;" as "and", no leading "The".</summary>
    internal static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var text = s.Trim().ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        if (text.StartsWith("the ", StringComparison.Ordinal))
        {
            text = text.Substring(4);
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static HashSet<string> Tokens(string s)
    {
        return s.Split(new[] { ' ', ',', ';', '/', '&', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm)
            .Where(x => x.Length > 0 && x != "and")
            .ToHashSet();
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private static string AlbumLabel(MusicAlbum album)
    {
        var artist = album.AlbumArtists?.FirstOrDefault();
        return string.IsNullOrEmpty(artist) ? album.Name : artist + " — " + album.Name;
    }

    private static string TrackLabel(AudioItem t)
    {
        var artist = t.Artists?.FirstOrDefault() ?? t.AlbumArtists?.FirstOrDefault();
        var name = Quote(t.Name);
        var where = string.IsNullOrEmpty(t.Album) ? string.Empty : " on " + t.Album;
        return (string.IsNullOrEmpty(artist) ? name : artist + " — " + name) + where;
    }

    private static string Sample(List<AudioItem> items)
    {
        if (items.Count > 3)
        {
            return string.Empty;
        }

        return " (" + string.Join(", ", items.Select(t => Quote(t.Name))) + ")";
    }

    private static string Of(int count, int total) => count == total
        ? (total == 1 ? "The only track" : "All " + total.ToString(CultureInfo.InvariantCulture) + " tracks")
        : count.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " tracks";

    private static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? string.Empty : "s");

    private static string Quote(string? s) => "\"" + (s ?? string.Empty) + "\"";
}

public class HealthReport
{
    public DateTime GeneratedAt { get; set; }

    public int TrackCount { get; set; }

    public int AlbumCount { get; set; }

    public int ArtistCount { get; set; }

    public List<Finding> Findings { get; set; } = new();
}

public class Finding
{
    public Finding(string kind, string subject, Guid? itemId, string label, string detail)
    {
        Kind = kind;
        Key = kind + ":" + subject;
        ItemId = itemId?.ToString("N");
        Label = label;
        Detail = detail;
    }

    /// <summary>Which check found it, e.g. "track-number".</summary>
    public string Kind { get; set; }

    /// <summary>Stable id used to dismiss it.</summary>
    public string Key { get; set; }

    /// <summary>The album, track or artist to open, if any.</summary>
    public string? ItemId { get; set; }

    public string Label { get; set; }

    public string Detail { get; set; }

    public bool Dismissed { get; set; }
}
