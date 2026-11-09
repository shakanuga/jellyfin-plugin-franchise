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
    private readonly ILogger<FranchiseSyncService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FranchiseSyncService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IPlaylistManager playlistManager,
        IProviderManager providerManager,
        IUserManager userManager,
        ILogger<FranchiseSyncService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _playlistManager = playlistManager;
        _providerManager = providerManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Franchise Collections: Synchronisation läuft bereits – übersprungen.");
            return;
        }

        try
        {
            await SyncInternalAsync(progress, cancellationToken).ConfigureAwait(false);
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

            var ownerId = ResolvePlaylistOwner(config);
            var collectionIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var playlistItemsByDef = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            var existingBoxSets = LoadExistingBoxSets();

            for (var i = 0; i < order.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var def = order[i];
                var rep = new DefinitionReport { Id = def.Id, Name = def.Name };
                report.Definitions.Add(rep);

                try
                {
                    var resolved = Resolve(def, index, config, collectionIds, playlistItemsByDef, rep);
                    playlistItemsByDef[def.Id] = resolved.PlaylistItems;

                    if (def.Collection)
                    {
                        var boxSetId = await SyncCollectionAsync(def, resolved.CollectionItems, config, state, existingBoxSets, ct).ConfigureAwait(false);
                        if (boxSetId is Guid id)
                        {
                            collectionIds[def.Id] = id;
                            rep.CollectionItems = resolved.CollectionItems.Count;
                        }
                    }

                    if (def.Playlist)
                    {
                        if (ownerId == Guid.Empty)
                        {
                            rep.Error = "Kein Benutzer für die Playlist gefunden.";
                        }
                        else if (await SyncPlaylistAsync(def, resolved.PlaylistItems, ownerId, config, state, ct).ConfigureAwait(false))
                        {
                            rep.PlaylistItems = resolved.PlaylistItems.Count;
                        }
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
    }

    private static Resolved Resolve(
        FranchiseDefinition def,
        LibraryIndex index,
        PluginConfiguration config,
        IReadOnlyDictionary<string, Guid> collectionIds,
        IReadOnlyDictionary<string, List<Guid>> playlistItemsByDef,
        DefinitionReport rep)
    {
        var result = new Resolved();
        var inCollection = new HashSet<Guid>();
        var inPlaylist = new HashSet<Guid>();

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

        foreach (var entry in def.Entries)
        {
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

                found = childFound;
                if (!found)
                {
                    // Unter-Sammlung ist leer – deren fehlende Einträge stehen bereits im Bericht der Unter-Sammlung
                    rep.EntriesTotal--;
                    continue;
                }
            }
            else
            {
                rep.Missing.Add("Ungültiger Eintrag ohne movie/series/tmdbCollection/collection: " + entry.Describe());
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

            foreach (var childId in def.Entries.Where(e => e.Collection is not null).Select(e => e.Collection!))
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

    private Guid ResolvePlaylistOwner(PluginConfiguration config)
    {
        if (Guid.TryParse(config.PlaylistOwnerUserId, out var configured) && _userManager.GetUserById(configured) is not null)
        {
            return configured;
        }

        var first = _userManager.Users.FirstOrDefault();
        return first?.Id ?? Guid.Empty;
    }

    private Playlist? FindPlaylist(FranchiseDefinition def, string name, Guid ownerId, SyncState state)
    {
        if (state.Playlists.TryGetValue(def.Id, out var idText)
            && Guid.TryParse(idText, out var id)
            && _libraryManager.GetItemById(id) is Playlist byId)
        {
            return byId;
        }

        return _playlistManager.GetPlaylists(ownerId)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> SyncPlaylistAsync(
        FranchiseDefinition def,
        List<Guid> items,
        Guid ownerId,
        PluginConfiguration config,
        SyncState state,
        CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(def.PlaylistName) ? $"{def.Name} – Reihenfolge" : def.PlaylistName!;
        var playlist = FindPlaylist(def, name, ownerId, state);

        if (items.Count == 0)
        {
            return false;
        }

        if (playlist is null)
        {
            _logger.LogInformation("Franchise Collections: lege Playlist \"{Name}\" mit {Count} Einträgen an", name, items.Count);
            var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = items,
                MediaType = Jellyfin.Data.Enums.MediaType.Video,
                UserId = ownerId,
                Public = config.PlaylistsPublic
            }).ConfigureAwait(false);

            state.Playlists[def.Id] = created.Id;
            return true;
        }

        var current = playlist.LinkedChildren
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value)
            .ToList();

        var needsUpdate = !current.SequenceEqual(items)
                          || !string.Equals(playlist.Name, name, StringComparison.Ordinal)
                          || playlist.OpenAccess != config.PlaylistsPublic;

        if (needsUpdate)
        {
            _logger.LogInformation("Franchise Collections: aktualisiere Playlist \"{Name}\" ({Count} Einträge)", name, items.Count);
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlist.Id,
                UserId = playlist.OwnerUserId,
                Name = name,
                Ids = items,
                Public = config.PlaylistsPublic
            }).ConfigureAwait(false);
        }

        state.Playlists[def.Id] = playlist.Id.ToString("N");
        return true;
    }
}
