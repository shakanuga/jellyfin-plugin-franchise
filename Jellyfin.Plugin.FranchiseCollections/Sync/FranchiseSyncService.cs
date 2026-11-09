using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using Jellyfin.Plugin.FranchiseCollections.Model;
using Jellyfin.Plugin.FranchiseCollections.Sources;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Gleicht die Definitionen mit der Bibliothek ab und legt Sammlungen + Watch-Order-Playlists an bzw. aktualisiert sie.
/// </summary>
public class FranchiseSyncService
{
    /// <summary>Provider-ID, mit der unsere Sammlungen markiert werden.</summary>
    public const string MarkerKey = "FranchiseCollections";

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IProviderManager _providerManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly AaronPerrisMcuSource _mcuSource;
    private readonly ILogger<FranchiseSyncService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _rerunRequested;
    private volatile HashSet<Guid> _managedItems = new();

    public FranchiseSyncService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IPlaylistManager playlistManager,
        IProviderManager providerManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        AaronPerrisMcuSource mcuSource,
        ILogger<FranchiseSyncService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _playlistManager = playlistManager;
        _providerManager = providerManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _mcuSource = mcuSource;
        _logger = logger;
    }

    /// <summary>Steht dieser Film / diese Folge in einer unserer Playlists? (für das Entfernen gesehener Einträge)</summary>
    public bool IsManaged(Guid itemId) => _managedItems.Contains(itemId);

    public async Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // läuft gerade – danach noch einmal, damit z. B. ein eben gesehener Film sicher verschwindet
            _rerunRequested = true;
            return;
        }

        try
        {
            do
            {
                _rerunRequested = false;
                await SyncInternalAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            while (_rerunRequested && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SyncInternalAsync(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin nicht initialisiert.");
        var config = plugin.Configuration;
        var stopwatch = Stopwatch.StartNew();
        var report = new SyncReport { StartedUtc = DateTime.UtcNow };
        var statePath = Path.Combine(plugin.DefinitionsFolder, "_state.json");
        var state = SyncState.Load(statePath);

        try
        {
            var definitions = DefinitionLoader.LoadActive(config.EnabledBuiltIns, plugin.DefinitionsFolder, config.CustomDefinitionsJson, report.Errors);
            var order = TopologicalOrder(definitions, report.Errors);
            _logger.LogInformation("Franchise Collections: {Count} Definitionen aktiv.", order.Count);

            var index = new LibraryIndex(_libraryManager);
            _logger.LogInformation("Franchise Collections: Bibliothek mit {Movies} Filmen und {Series} Serien indiziert.", index.MovieCount, index.SeriesCount);

            var users = ResolvePlaylistUsers(config);
            var aliases = ParseAliases(config.SeriesAliases);
            var collectionIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var playlistItemsByDef = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            var mediaIdsByDef = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            var managed = new HashSet<Guid>();
            var existingBoxSets = LoadExistingBoxSets();

            // Automatische Quellen nur laden, wenn eine aktive Definition sie braucht
            IReadOnlyList<AaronPerrisMcuSource.GuideItem>? mcuItems = null;
            if (order.Any(d => d.Entries.Any(e => string.Equals(e.Source, AaronPerrisMcuSource.SourceId, StringComparison.OrdinalIgnoreCase))))
            {
                mcuItems = await _mcuSource.GetItemsAsync(plugin.DefinitionsFolder, report.Errors, ct).ConfigureAwait(false);
            }

            for (var i = 0; i < order.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var def = order[i];
                var rep = new DefinitionReport { Id = def.Id, Name = def.Name };
                report.Definitions.Add(rep);

                try
                {
                    var entries = ExpandSources(def, mcuItems, rep, out var sourceFailed);
                    var resolved = Resolve(def, entries, index, config, aliases, collectionIds, playlistItemsByDef, mediaIdsByDef, rep);
                    playlistItemsByDef[def.Id] = resolved.PlaylistItems;
                    mediaIdsByDef[def.Id] = resolved.MediaIds;

                    if (def.Collection)
                    {
                        var boxSetId = await SyncCollectionAsync(def, resolved.CollectionItems, config, state, existingBoxSets, ct).ConfigureAwait(false);
                        if (boxSetId is Guid id)
                        {
                            collectionIds[def.Id] = id;
                            rep.CollectionItems = resolved.CollectionItems.Count;
                        }
                    }

                    if (def.Playlist && sourceFailed)
                    {
                        // Quelle nicht verfügbar → vorhandene Playlists nicht anfassen (nie leeren)
                        rep.Error = "Quelle nicht verfügbar – Playlists unverändert.";
                    }
                    else if (def.Playlist)
                    {
                        managed.UnionWith(resolved.PlaylistItems);
                        if (users.Count == 0)
                        {
                            rep.Error = "Kein Benutzer für die Playlist gefunden.";
                        }

                        foreach (var (userId, user) in users)
                        {
                            await SyncPlaylistForUserAsync(def, resolved.PlaylistItems, userId, user, config, state, ct).ConfigureAwait(false);
                        }

                        rep.PlaylistItems = resolved.PlaylistItems.Count;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Franchise Collections: Fehler bei {Id}", def.Id);
                    rep.Error = ex.Message;
                }

                progress.Report(100.0 * (i + 1) / Math.Max(1, order.Count));
            }

            _managedItems = managed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Franchise Collections: Synchronisation fehlgeschlagen");
            report.Errors.Add(ex.Message);
        }
        finally
        {
            report.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
            try
            {
                state.Save(statePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Franchise Collections: Zustand konnte nicht gespeichert werden");
            }

            // frische Instanz verwenden – die Einstellungsseite kann die Konfiguration während des Laufs ersetzt haben
            plugin.Configuration.LastReportJson = JsonSerializer.Serialize(report);
            plugin.SaveConfiguration();
        }
    }

    // ------------------------------------------------------------------ Abgleich

    private sealed class Resolved
    {
        public List<Guid> CollectionItems { get; } = new();

        public List<Guid> PlaylistItems { get; } = new();

        /// <summary>Alle Filme und Serien dieser Definition (inkl. Unter-Sammlungen) – für "scope".</summary>
        public HashSet<Guid> MediaIds { get; } = new();
    }

    /// <summary>Ersetzt "source"-Einträge durch die Einträge der Quelle und wendet "exclude" an.</summary>
    private static List<FranchiseEntry> ExpandSources(
        FranchiseDefinition def,
        IReadOnlyList<AaronPerrisMcuSource.GuideItem>? mcuItems,
        DefinitionReport rep,
        out bool sourceFailed)
    {
        sourceFailed = false;
        var result = new List<FranchiseEntry>();
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var entry in def.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Source))
            {
                result.Add(entry);
                continue;
            }

            if (!string.Equals(entry.Source, AaronPerrisMcuSource.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                rep.Missing.Add($"Unbekannte Quelle \"{entry.Source}\"");
                continue;
            }

            if (mcuItems is null)
            {
                sourceFailed = true;
                continue;
            }

            foreach (var generated in AaronPerrisMcuSource.ToEntries(mcuItems, entry.Order, entry.CanonOnly, today))
            {
                generated.Scope = entry.Scope;
                result.Add(generated);
            }
        }

        if (def.Exclude.Count == 0)
        {
            return result;
        }

        var rules = def.Exclude.Select(ExcludeRule.Parse).ToList();
        return result.Where(e => !rules.Any(r => r.Matches(e))).ToList();
    }

    private sealed class ExcludeRule
    {
        private static readonly System.Text.RegularExpressions.Regex CodeAtEnd =
            new(@"^(?<name>.*?)\s+(?<code>S\d{1,3}(E\d{1,4})?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private HashSet<string> _keys = new(StringComparer.Ordinal);
        private int? _season;
        private int? _episode;

        public static ExcludeRule Parse(string text)
        {
            var rule = new ExcludeRule();
            var name = text;
            var m = CodeAtEnd.Match(text);
            if (m.Success)
            {
                name = m.Groups["name"].Value;
                var code = LibraryIndex.ParseCode(m.Groups["code"].Value, true)!.Value;
                rule._season = code.Season;
                rule._episode = m.Groups["code"].Value.Contains('E', StringComparison.OrdinalIgnoreCase) ? code.Episode : null;
            }

            rule._keys = new HashSet<string>(LibraryIndex.NameKeys(name), StringComparer.Ordinal);
            return rule;
        }

        public bool Matches(FranchiseEntry e)
        {
            var label = e.Title ?? e.Movie ?? e.Series ?? e.Collection ?? string.Empty;
            if (!LibraryIndex.NameKeys(label).Any(_keys.Contains))
            {
                return false;
            }

            if (_season is null)
            {
                return true;
            }

            // Staffel/Folge kann nur bei einzelnen Folgen (aus Quellen) bzw. einstaffeligen Bereichen geprüft werden
            var from = LibraryIndex.ParseCode(e.From, true);
            var to = LibraryIndex.ParseCode(e.To, false);
            if (from is null || to is null || from.Value.Season != to.Value.Season || from.Value.Season != _season)
            {
                return false;
            }

            return _episode is null || (from.Value.Episode == _episode && to.Value.Episode == _episode);
        }
    }

    private static Dictionary<string, string> ParseAliases(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var idx = line.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var from = line[..idx].Trim();
            var to = line[(idx + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0)
            {
                result[LibraryIndex.Normalize(from)] = to;
            }
        }

        return result;
    }

    private static Resolved Resolve(
        FranchiseDefinition def,
        List<FranchiseEntry> entries,
        LibraryIndex index,
        PluginConfiguration config,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, Guid> collectionIds,
        IReadOnlyDictionary<string, List<Guid>> playlistItemsByDef,
        IReadOnlyDictionary<string, HashSet<Guid>> mediaIdsByDef,
        DefinitionReport rep)
    {
        var result = new Resolved();
        var inCollection = new HashSet<Guid>();
        var inPlaylist = new HashSet<Guid>();

        // fehlende Einträge aus Quellen zusammenfassen ("Agents of S.H.I.E.L.D. – 136 Folgen")
        var sourceMissing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scopesUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCollection(Guid id, FranchiseEntry e)
        {
            if (!e.PlaylistOnly && inCollection.Add(id))
            {
                result.CollectionItems.Add(id);
            }
        }

        void AddPlaylist(Guid id, FranchiseEntry e)
        {
            if (!e.CollectionOnly && inPlaylist.Add(id))
            {
                result.PlaylistItems.Add(id);
            }
        }

        string Alias(string? title)
            => title is not null && aliases.TryGetValue(LibraryIndex.Normalize(title), out var target) ? target : title ?? string.Empty;

        foreach (var entry in entries)
        {
            if (entry.FromSource)
            {
                HashSet<Guid>? scope = null;
                if (!string.IsNullOrWhiteSpace(entry.Scope))
                {
                    scopesUsed.Add(entry.Scope);
                    if (!mediaIdsByDef.TryGetValue(entry.Scope, out scope))
                    {
                        scope = new HashSet<Guid>();
                    }
                }

                if (entry.Movie is not null)
                {
                    var movie = index.FindMovie(string.Empty, Alias(entry.Title), entry.Year, allowTitle: true);
                    if (movie is null)
                    {
                        // mit "scope" lässt sich nicht sagen, ob ein unbekannter Titel dazugehört hätte → nicht melden
                        if (scope is null)
                        {
                            sourceMissing[$"{entry.Title} ({entry.Year})"] = 1;
                        }

                        continue;
                    }

                    if (scope is not null && !scope.Contains(movie.Id))
                    {
                        continue;
                    }

                    AddCollection(movie.Id, entry);
                    AddPlaylist(movie.Id, entry);
                    result.MediaIds.Add(movie.Id);
                }
                else
                {
                    var series = index.FindSeriesByName(Alias(entry.Title), entry.Year);
                    if (series is null)
                    {
                        if (scope is null)
                        {
                            sourceMissing[entry.Title ?? "?"] = sourceMissing.GetValueOrDefault(entry.Title ?? "?") + 1;
                        }

                        continue;
                    }

                    if (scope is not null && !scope.Contains(series.Id))
                    {
                        continue;
                    }

                    var episodes = index.GetEpisodes(series, entry.From, entry.To);
                    if (episodes.Count == 0)
                    {
                        var key = $"{series.Name} (Folgen)";
                        sourceMissing[key] = sourceMissing.GetValueOrDefault(key) + 1;
                        continue;
                    }

                    AddCollection(series.Id, entry);
                    result.MediaIds.Add(series.Id);
                    foreach (var ep in episodes)
                    {
                        AddPlaylist(ep.Id, entry);
                    }
                }

                continue;
            }

            rep.EntriesTotal++;
            var found = false;

            // "movie": "" bzw. "series": "" ist erlaubt → Abgleich nur über Titel + Jahr
            if (entry.Movie is not null)
            {
                var movie = index.FindMovie(entry.Movie, entry.Title, entry.Year, config.MatchByTitleAndYear);
                if (movie is not null)
                {
                    AddCollection(movie.Id, entry);
                    AddPlaylist(movie.Id, entry);
                    result.MediaIds.Add(movie.Id);
                    found = true;
                }
            }
            else if (entry.Series is not null)
            {
                var series = index.FindSeries(entry.Series, entry.Title, entry.Year, config.MatchByTitleAndYear);
                if (series is not null)
                {
                    var episodes = index.GetEpisodes(series, entry.From, entry.To);
                    var hasRange = entry.From is not null || entry.To is not null;

                    // Mit Bereich (z. B. Loki S02 in Phase 5) nur aufnehmen, wenn davon auch Folgen da sind
                    if (!hasRange || episodes.Count > 0)
                    {
                        AddCollection(series.Id, entry);
                        result.MediaIds.Add(series.Id);
                        foreach (var ep in episodes)
                        {
                            AddPlaylist(ep.Id, entry);
                        }

                        found = true;
                    }
                    else
                    {
                        rep.Missing.Add(entry.Describe() + " – Serie vorhanden, aber keine Folgen in diesem Bereich");
                        continue;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.TmdbCollection))
            {
                var movies = index.MoviesInTmdbCollection(entry.TmdbCollection);
                foreach (var movie in movies)
                {
                    AddCollection(movie.Id, entry);
                    AddPlaylist(movie.Id, entry);
                    result.MediaIds.Add(movie.Id);
                }

                found = movies.Count > 0;
            }
            else if (!string.IsNullOrWhiteSpace(entry.Collection))
            {
                var childFound = false;
                if (collectionIds.TryGetValue(entry.Collection, out var childBoxSet))
                {
                    AddCollection(childBoxSet, entry);
                    childFound = true;
                }

                if (playlistItemsByDef.TryGetValue(entry.Collection, out var childItems) && childItems.Count > 0)
                {
                    foreach (var id in childItems)
                    {
                        AddPlaylist(id, entry);
                    }

                    childFound = true;
                }

                if (mediaIdsByDef.TryGetValue(entry.Collection, out var childMedia))
                {
                    result.MediaIds.UnionWith(childMedia);
                }

                if (!childFound)
                {
                    // Unter-Sammlung ist leer – deren fehlende Einträge stehen bereits im Bericht der Unter-Sammlung
                    rep.EntriesTotal--;
                    continue;
                }

                found = true;
            }
            else
            {
                rep.Missing.Add("Ungültiger Eintrag ohne movie/series/tmdbCollection/collection/source: " + entry.Describe());
                continue;
            }

            if (found)
            {
                rep.EntriesFound++;
            }
            else
            {
                rep.Missing.Add(entry.Describe());
            }
        }

        // Bericht für Quellen
        var sourcePlaced = result.MediaIds.Count;
        if (entries.Any(e => e.FromSource))
        {
            rep.EntriesFound += sourcePlaced;
            rep.EntriesTotal += sourcePlaced + sourceMissing.Count;
            foreach (var (label, count) in sourceMissing.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                rep.Missing.Add(count > 1 ? $"{label} – {count} Folgen" : label);
            }

            // Was laut "scope" dazugehört, aber in der Quelle nicht gefunden wurde (z. B. Name weicht ab → Alias anlegen)
            foreach (var scopeId in scopesUsed)
            {
                if (!mediaIdsByDef.TryGetValue(scopeId, out var scopeMedia))
                {
                    continue;
                }

                foreach (var id in scopeMedia.Where(id => !result.MediaIds.Contains(id)))
                {
                    var name = index.GetName(id);
                    rep.Missing.Add($"{name} – in \"{scopeId}\", aber nicht in der Quellliste gefunden (ggf. Alias anlegen)");
                }
            }
        }

        return result;
    }

    private static List<FranchiseDefinition> TopologicalOrder(Dictionary<string, FranchiseDefinition> defs, List<string> errors)
    {
        var result = new List<FranchiseDefinition>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(FranchiseDefinition def)
        {
            if (done.Contains(def.Id))
            {
                return;
            }

            if (!visiting.Add(def.Id))
            {
                errors.Add($"Zirkuläre Verschachtelung bei \"{def.Id}\" – wird ignoriert.");
                return;
            }

            foreach (var childId in def.Entries.Select(e => e.Collection ?? e.Scope).Where(id => id is not null).Select(id => id!))
            {
                if (defs.TryGetValue(childId, out var child))
                {
                    Visit(child);
                }
                else
                {
                    errors.Add($"\"{def.Id}\" verweist auf unbekannte Sammlung \"{childId}\".");
                }
            }

            visiting.Remove(def.Id);
            done.Add(def.Id);
            result.Add(def);
        }

        foreach (var def in defs.Values.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            Visit(def);
        }

        return result;
    }

    // ------------------------------------------------------------------ Sammlungen

    private List<BoxSet> LoadExistingBoxSets()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            })
            .OfType<BoxSet>()
            .ToList();
    }

    private BoxSet? FindBoxSet(FranchiseDefinition def, SyncState state, List<BoxSet> existing)
    {
        if (state.Collections.TryGetValue(def.Id, out var idText)
            && Guid.TryParse(idText, out var id)
            && _libraryManager.GetItemById(id) is BoxSet byId)
        {
            return byId;
        }

        // Bewusst KEINE Suche nach Namen: eine selbst angelegte Sammlung "Star Trek" soll nicht übernommen
        // (und ihre Inhalte nicht entfernt) werden.
        return existing.FirstOrDefault(b => b.ProviderIds.TryGetValue(MarkerKey, out var marker)
                                            && string.Equals(marker, def.Id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Guid?> SyncCollectionAsync(
        FranchiseDefinition def,
        List<Guid> members,
        PluginConfiguration config,
        SyncState state,
        List<BoxSet> existing,
        CancellationToken ct)
    {
        var boxSet = FindBoxSet(def, state, existing);

        if (members.Count == 0 && !config.CreateEmptyCollections)
        {
            // nichts da → nicht anlegen; eine schon vorhandene Sammlung bleibt (leeren würde sie nur verwirren)
            return boxSet?.Id;
        }

        if (boxSet is null)
        {
            _logger.LogInformation("Franchise Collections: lege Sammlung \"{Name}\" an", def.Name);
            boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = def.Name,
                IsLocked = false,
                ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MarkerKey] = def.Id }
            }).ConfigureAwait(false);
            existing.Add(boxSet);
        }

        var current = boxSet.LinkedChildren
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value)
            .ToHashSet();
        var desired = members.ToHashSet();
        var toAdd = members.Where(m => !current.Contains(m)).ToList();
        var toRemove = current.Where(c => !desired.Contains(c)).ToList();

        if (toAdd.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            _logger.LogInformation("Franchise Collections: \"{Name}\" +{Added} / -{Removed}", def.Name, toAdd.Count, toRemove.Count);
            boxSet = _libraryManager.GetItemById(boxSet.Id) as BoxSet ?? boxSet;
        }

        state.Collections[def.Id] = boxSet.Id.ToString("N");
        await ApplyMetadataAsync(boxSet, def, ct).ConfigureAwait(false);
        await ApplyImagesAsync(boxSet, def, config, state, ct).ConfigureAwait(false);
        return boxSet.Id;
    }

    private static async Task ApplyMetadataAsync(BoxSet boxSet, FranchiseDefinition def, CancellationToken ct)
    {
        var changed = false;

        if (!string.Equals(boxSet.Name, def.Name, StringComparison.Ordinal))
        {
            boxSet.Name = def.Name;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(def.SortName) && !string.Equals(boxSet.ForcedSortName, def.SortName, StringComparison.Ordinal))
        {
            boxSet.ForcedSortName = def.SortName;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(def.Overview) && !string.Equals(boxSet.Overview, def.Overview, StringComparison.Ordinal))
        {
            boxSet.Overview = def.Overview;
            changed = true;
        }

        var displayOrder = string.Equals(def.DisplayOrder, "SortName", StringComparison.OrdinalIgnoreCase) ? "SortName" : "PremiereDate";
        if (!string.Equals(boxSet.DisplayOrder, displayOrder, StringComparison.Ordinal))
        {
            boxSet.DisplayOrder = displayOrder;
            changed = true;
        }

        if (!boxSet.ProviderIds.TryGetValue(MarkerKey, out var marker) || !string.Equals(marker, def.Id, StringComparison.Ordinal))
        {
            boxSet.ProviderIds[MarkerKey] = def.Id;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(def.TmdbCollectionId)
            && (!boxSet.ProviderIds.TryGetValue("Tmdb", out var tmdb) || tmdb != def.TmdbCollectionId))
        {
            boxSet.ProviderIds["Tmdb"] = def.TmdbCollectionId;
            changed = true;
        }

        // Name und Beschreibung sperren, damit ein Metadaten-Refresh sie nicht überschreibt
        var locked = boxSet.LockedFields ?? Array.Empty<MetadataField>();
        var wantLocked = new[] { MetadataField.Name, MetadataField.Overview };
        if (wantLocked.Any(f => !locked.Contains(f)))
        {
            boxSet.LockedFields = locked.Union(wantLocked).ToArray();
            changed = true;
        }

        if (changed)
        {
            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ Bilder

    private async Task ApplyImagesAsync(BaseItem item, FranchiseDefinition def, PluginConfiguration config, SyncState state, CancellationToken ct)
    {
        await ApplyImageAsync(item, def.Id, ImageType.Primary, "poster", def.Poster, config, state, ct).ConfigureAwait(false);
        await ApplyImageAsync(item, def.Id, ImageType.Backdrop, "backdrop", def.Backdrop, config, state, ct).ConfigureAwait(false);
    }

    private async Task ApplyImageAsync(
        BaseItem item,
        string defId,
        ImageType type,
        string suffix,
        string? url,
        PluginConfiguration config,
        SyncState state,
        CancellationToken ct)
    {
        var folder = Plugin.Instance!.DefinitionsFolder;
        var localFile = ImageExtensions
            .Select(ext => Path.Combine(folder, $"{defId}-{suffix}{ext}"))
            .FirstOrDefault(File.Exists);

        string? source = localFile is not null
            ? localFile + "@" + File.GetLastWriteTimeUtc(localFile).Ticks
            : string.IsNullOrWhiteSpace(url) ? null : url.Trim();

        if (source is null)
        {
            return;
        }

        // Jedes Bild nur einmal setzen (bzw. erneut, wenn sich URL/Datei ändert).
        // So wird die automatisch erzeugte Collage einmal ersetzt, spätere manuelle Änderungen in Jellyfin bleiben aber erhalten.
        var key = $"{defId}|{type}";
        if (state.Images.TryGetValue(key, out var previous) && previous == source)
        {
            return;
        }

        try
        {
            if (localFile is not null)
            {
                var mime = Path.GetExtension(localFile).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                await using var stream = File.OpenRead(localFile);
                await _providerManager.SaveImage(item, stream, mime, type, null, ct).ConfigureAwait(false);
            }
            else
            {
                await _providerManager.SaveImage(item, source, type, null, ct).ConfigureAwait(false);
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            state.Images[key] = source;
            _logger.LogInformation("Franchise Collections: Bild {Type} für \"{Name}\" gesetzt", type, item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Franchise Collections: Bild {Type} für \"{Name}\" konnte nicht gesetzt werden", type, item.Name);
        }
    }

    // ------------------------------------------------------------------ Playlists

    /// <summary>Ausgewählte Benutzer (bzw. der erste Benutzer, wenn keiner ausgewählt ist).</summary>
    private List<(Guid Id, Jellyfin.Database.Implementations.Entities.User User)> ResolvePlaylistUsers(PluginConfiguration config)
    {
        var result = new List<(Guid, Jellyfin.Database.Implementations.Entities.User)>();
        foreach (var text in config.PlaylistUserIds ?? Array.Empty<string>())
        {
            if (Guid.TryParse(text, out var id) && _userManager.GetUserById(id) is { } user && result.All(r => r.Item1 != id))
            {
                result.Add((id, user));
            }
        }

        if (result.Count == 0 && _userManager.GetUsers().FirstOrDefault() is { } first)
        {
            result.Add((first.Id, first));
        }

        return result;
    }

    private Playlist? FindPlaylist(FranchiseDefinition def, string name, Guid userId, SyncState state)
    {
        foreach (var key in new[] { $"{def.Id}|{userId:N}", def.Id })
        {
            if (state.Playlists.TryGetValue(key, out var idText)
                && Guid.TryParse(idText, out var id)
                && _libraryManager.GetItemById(id) is Playlist byId
                && byId.OwnerUserId == userId)
            {
                return byId;
            }
        }

        return _playlistManager.GetPlaylists(userId)
            .FirstOrDefault(p => p.OwnerUserId == userId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SyncPlaylistForUserAsync(
        FranchiseDefinition def,
        List<Guid> allItems,
        Guid userId,
        Jellyfin.Database.Implementations.Entities.User user,
        PluginConfiguration config,
        SyncState state,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = string.IsNullOrWhiteSpace(def.PlaylistName) ? $"{def.Name} – Reihenfolge" : def.PlaylistName!;
        var playlist = FindPlaylist(def, name, userId, state);

        var items = allItems;
        if (config.RemoveWatchedFromPlaylists)
        {
            items = allItems.Where(id =>
            {
                var item = _libraryManager.GetItemById(id);
                return item is null || _userDataManager.GetUserData(user, item)?.Played != true;
            }).ToList();
        }

        if (playlist is null)
        {
            if (items.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Franchise Collections: lege Playlist \"{Name}\" für {User} an ({Count} Einträge)", name, user.Username, items.Count);
            var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = items,
                MediaType = Jellyfin.Data.Enums.MediaType.Video,
                UserId = userId,
                Public = false
            }).ConfigureAwait(false);

            state.Playlists[$"{def.Id}|{userId:N}"] = created.Id;
            return;
        }

        var current = playlist.LinkedChildren
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value)
            .ToList();

        if (!current.SequenceEqual(items) || !string.Equals(playlist.Name, name, StringComparison.Ordinal))
        {
            _logger.LogInformation("Franchise Collections: aktualisiere Playlist \"{Name}\" für {User} ({Count} Einträge)", name, user.Username, items.Count);
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlist.Id,
                UserId = playlist.OwnerUserId,
                Name = name,
                Ids = items
            }).ConfigureAwait(false);
        }

        state.Playlists.Remove(def.Id);
        state.Playlists[$"{def.Id}|{userId:N}"] = playlist.Id.ToString("N");
    }
}
