using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Einmal pro Lauf aufgebauter Index über alle Filme und Serien der Bibliothek (nach Provider-IDs und Titel+Jahr).
/// </summary>
public sealed class LibraryIndex
{
    private static readonly Regex EpisodeCode = new(@"^\s*S(?<s>\d{1,3})\s*(?:E(?<e>\d{1,4}))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILibraryManager _libraryManager;
    private readonly Dictionary<string, List<BaseItem>> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BaseItem>> _byTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BaseItem>> _byTmdbCollection = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, List<Episode>> _episodes = new();

    public LibraryIndex(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var item in items)
        {
            var kind = item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : "series";
            foreach (var (provider, value) in item.ProviderIds)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (string.Equals(provider, "TmdbCollection", StringComparison.OrdinalIgnoreCase))
                {
                    Add(_byTmdbCollection, value.Trim(), item);
                    continue;
                }

                Add(_byId, $"{kind}|{provider}:{value.Trim()}", item);
            }

            if (item.ProductionYear is int year)
            {
                foreach (var title in new[] { item.Name, item.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
                {
                    Add(_byTitle, $"{kind}|{Normalize(title!)}|{year}", item);
                }
            }
        }

        MovieCount = items.Count(i => i is MediaBrowser.Controller.Entities.Movies.Movie);
        SeriesCount = items.Count - MovieCount;
    }

    public int MovieCount { get; }

    public int SeriesCount { get; }

    public BaseItem? FindMovie(string refs, string? title, int? year, bool allowTitle) => Find("movie", refs, title, year, allowTitle);

    public BaseItem? FindSeries(string refs, string? title, int? year, bool allowTitle) => Find("series", refs, title, year, allowTitle);

    public IReadOnlyList<BaseItem> MoviesInTmdbCollection(string id)
    {
        return _byTmdbCollection.TryGetValue(id.Trim(), out var list)
            ? list.Where(i => i is MediaBrowser.Controller.Entities.Movies.Movie)
                .OrderBy(i => i.PremiereDate ?? DateTime.MaxValue)
                .ThenBy(i => i.ProductionYear ?? int.MaxValue)
                .ToList()
            : Array.Empty<BaseItem>();
    }

    /// <summary>Folgen einer Serie in Staffel-/Folgen-Reihenfolge, gefiltert auf den Bereich From–To. Specials (Staffel 0) nur, wenn From in Staffel 0 beginnt.</summary>
    public IReadOnlyList<Episode> GetEpisodes(BaseItem series, string? from, string? to)
    {
        if (!_episodes.TryGetValue(series.Id, out var all))
        {
            all = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { series.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                })
                .OfType<Episode>()
                .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
                .GroupBy(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value))
                .Select(g => g.First())
                .OrderBy(e => e.ParentIndexNumber)
                .ThenBy(e => e.IndexNumber)
                .ToList();
            _episodes[series.Id] = all;
        }

        var start = ParseCode(from, true) ?? (1, 0);
        var end = ParseCode(to, false) ?? (int.MaxValue, int.MaxValue);

        return all.Where(e =>
        {
            var key = (e.ParentIndexNumber!.Value, e.IndexNumber!.Value);
            return Compare(key, start) >= 0 && Compare(key, end) <= 0;
        }).ToList();
    }

    /// <summary>"S05E20" → (5, 20); "S05" → (5, 0) als Start bzw. (5, ∞) als Ende.</summary>
    public static (int Season, int Episode)? ParseCode(string? code, bool isStart)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var m = EpisodeCode.Match(code);
        if (!m.Success)
        {
            throw new FormatException($"Ungültige Folgenangabe \"{code}\" – erwartet z. B. S01E05 oder S03.");
        }

        var season = int.Parse(m.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (m.Groups["e"].Success)
        {
            return (season, int.Parse(m.Groups["e"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return isStart ? (season, 0) : (season, int.MaxValue);
    }

    private static int Compare((int S, int E) a, (int S, int E) b)
        => a.S != b.S ? a.S.CompareTo(b.S) : a.E.CompareTo(b.E);

    private BaseItem? Find(string kind, string refs, string? title, int? year, bool allowTitle)
    {
        foreach (var raw in refs.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = raw.IndexOf(':', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var provider = raw[..idx].Trim();
            var value = raw[(idx + 1)..].Trim();
            if (_byId.TryGetValue($"{kind}|{provider}:{value}", out var hits) && hits.Count > 0)
            {
                return hits[0];
            }
        }

        if (allowTitle && !string.IsNullOrWhiteSpace(title) && year is int y)
        {
            // ±1 Jahr, weil Erscheinungsjahre je nach Land/Quelle schwanken
            foreach (var candidateYear in new[] { y, y - 1, y + 1 })
            {
                if (_byTitle.TryGetValue($"{kind}|{Normalize(title)}|{candidateYear}", out var hits) && hits.Count > 0)
                {
                    return hits[0];
                }
            }
        }

        return null;
    }

    private static void Add(Dictionary<string, List<BaseItem>> dict, string key, BaseItem item)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<BaseItem>();
            dict[key] = list;
        }

        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    private static string Normalize(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
