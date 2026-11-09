using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FranchiseCollections.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FranchiseCollections.Api;

/// <summary>
/// Franchise-Seite: liefert die HTML-Seite und die Daten (mit Gesehen-Status des angemeldeten Benutzers).
/// Die Seite verlinkt nur auf die normalen Jellyfin-Detailseiten – Abspielen macht Jellyfin selbst.
/// </summary>
[ApiController]
[Route("FranchiseCollections")]
public class FranchiseController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public FranchiseController(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>Die Franchise-Seite (z. B. per Custom Tabs als iframe eingebunden).</summary>
    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces("text/html")]
    public IActionResult GetPage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("franchises.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    /// <summary>Alle Franchises mit Inhalten, Reihenfolgen und Fortschritt des angemeldeten Benutzers.</summary>
    [HttpGet("Data")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetData()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        var model = PageModel.Load(Path.Combine(plugin.DefinitionsFolder, "_model.json"));
        if (model is null)
        {
            return new { ready = false };
        }

        var items = new Dictionary<Guid, object>();
        var seriesCache = new Dictionary<Guid, List<Episode>>();

        List<Episode> EpisodesOf(Guid seriesId)
        {
            if (!seriesCache.TryGetValue(seriesId, out var list))
            {
                list = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        AncestorIds = new[] { seriesId },
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        Recursive = true,
                        IsVirtualItem = false
                    })
                    .OfType<Episode>()
                    .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber.HasValue)
                    .OrderBy(e => e.ParentIndexNumber)
                    .ThenBy(e => e.IndexNumber)
                    .ToList();
                seriesCache[seriesId] = list;
            }

            return list;
        }

        bool Played(BaseItem item) => _userDataManager.GetUserData(user, item)?.Played == true;

        void AddMedia(Guid id)
        {
            if (items.ContainsKey(id) || _libraryManager.GetItemById(id) is not { } item)
            {
                return;
            }

            if (item is Series series)
            {
                var eps = EpisodesOf(id);
                var played = eps.Count(Played);
                var next = eps.FirstOrDefault(e => !Played(e));
                items[id] = new
                {
                    id,
                    type = "Series",
                    name = series.Name,
                    year = series.ProductionYear,
                    endYear = series.EndDate?.Year,
                    overview = series.Overview,
                    episodes = eps.Count,
                    played,
                    nextEpisodeId = next?.Id,
                    nextEpisode = next is null ? null : $"S{next.ParentIndexNumber:00}E{next.IndexNumber:00}"
                };
            }
            else
            {
                var data = _userDataManager.GetUserData(user, item);
                var percent = data?.Played == true
                    ? 100
                    : item.RunTimeTicks is > 0 && data?.PlaybackPositionTicks > 0
                        ? (int)(100 * data.PlaybackPositionTicks / item.RunTimeTicks.Value)
                        : 0;
                items[id] = new
                {
                    id,
                    type = item is Movie ? "Movie" : item.GetType().Name,
                    name = item.Name,
                    year = item.ProductionYear,
                    overview = item.Overview,
                    runtimeMinutes = item.RunTimeTicks.HasValue ? (int?)(item.RunTimeTicks.Value / TimeSpan.TicksPerMinute) : null,
                    played = data?.Played == true,
                    percent
                };
            }
        }

        /// <summary>Reihenfolge in Blöcke zusammenfassen: Film | Serie Staffel X Folge a–b.</summary>
        List<object> BuildBlocks(List<Guid> order)
        {
            var blocks = new List<object>();
            var i = 0;
            while (i < order.Count)
            {
                var item = _libraryManager.GetItemById(order[i]);
                if (item is Episode ep && ep.SeriesId != Guid.Empty)
                {
                    var group = new List<Episode> { ep };
                    var j = i + 1;
                    while (j < order.Count
                           && _libraryManager.GetItemById(order[j]) is Episode next
                           && next.SeriesId == ep.SeriesId
                           && next.ParentIndexNumber == ep.ParentIndexNumber)
                    {
                        group.Add(next);
                        j++;
                    }

                    AddMedia(ep.SeriesId);
                    var playedCount = group.Count(Played);
                    var firstUnplayed = group.FirstOrDefault(e => !Played(e)) ?? group[0];
                    blocks.Add(new
                    {
                        kind = "episodes",
                        seriesId = ep.SeriesId,
                        seriesName = ep.SeriesName,
                        season = ep.ParentIndexNumber,
                        from = group[0].IndexNumber,
                        to = group[^1].IndexNumber,
                        count = group.Count,
                        played = playedCount,
                        targetId = firstUnplayed.Id,
                        targetCode = $"S{firstUnplayed.ParentIndexNumber:00}E{firstUnplayed.IndexNumber:00}",
                        year = group[0].PremiereDate?.Year ?? group[0].ProductionYear
                    });
                    i = j;
                }
                else if (item is not null)
                {
                    AddMedia(item.Id);
                    blocks.Add(new { kind = "movie", id = item.Id });
                    i++;
                }
                else
                {
                    i++;
                }
            }

            return blocks;
        }

        var defs = new List<object>();
        foreach (var d in model.Defs)
        {
            foreach (var id in d.Media)
            {
                AddMedia(id);
            }

            defs.Add(new
            {
                id = d.Id,
                name = d.Name,
                overview = d.Overview,
                kicker = d.Kicker,
                collectionId = d.CollectionId,
                artworkItemId = d.ArtworkItemId,
                children = d.Children,
                media = d.Media,
                hasCollection = d.HasCollection,
                scope = d.Scope,
                order = d.HasOrder ? BuildBlocks(d.Order) : null,
                orderName = d.OrderName
            });
        }

        // Oberste Ebene: Sammlungen, die nicht Unter-Sammlung sind, + Reihenfolgen ohne eigene Sammlung und ohne "scope"
        var childIds = new HashSet<string>(model.Defs.SelectMany(d => d.Children), StringComparer.OrdinalIgnoreCase);
        var defIds = new HashSet<string>(model.Defs.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        var top = model.Defs
            .Where(d => !childIds.Contains(d.Id) && (d.HasCollection || d.Scope is null || !defIds.Contains(d.Scope)))
            .Where(d => d.Media.Count > 0 || d.Children.Count > 0 || d.HasOrder)
            .Select(d => d.Id)
            .ToList();

        return new
        {
            ready = true,
            generated = model.GeneratedUtc,
            top,
            defs,
            items = items.Values
        };
    }
}
