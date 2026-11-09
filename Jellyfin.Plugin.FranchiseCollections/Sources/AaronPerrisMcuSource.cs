using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Model;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Sources;

/// <summary>
/// MCU-Reihenfolge von https://aaronperris.com/mcu/ (Datei mcu.json): Filme und einzelne Serienfolgen
/// mit Erscheinungsdatum, chronologischer Position und "Sacred Timeline"-Position.
/// Wird höchstens alle 6 Stunden neu geladen; ist die Seite nicht erreichbar, wird die zuletzt gespeicherte Kopie benutzt.
/// </summary>
public class AaronPerrisMcuSource
{
    public const string SourceId = "aaronperris-mcu";
    private const string Url = "https://aaronperris.com/mcu/mcu.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AaronPerrisMcuSource> _logger;
    private List<GuideItem>? _items;
    private DateTime _loadedUtc;

    public AaronPerrisMcuSource(IHttpClientFactory httpClientFactory, ILogger<AaronPerrisMcuSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GuideItem>?> GetItemsAsync(string cacheFolder, List<string> errors, CancellationToken ct)
    {
        if (_items is not null && DateTime.UtcNow - _loadedUtc < MaxAge)
        {
            return _items;
        }

        var cachePath = Path.Combine(cacheFolder, "_cache-aaronperris-mcu.json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var json = await client.GetStringAsync(new Uri(Url), timeout.Token).ConfigureAwait(false);
            var items = Parse(json);
            if (items.Count < 50)
            {
                throw new InvalidDataException($"nur {items.Count} Einträge – Format geändert?");
            }

            Directory.CreateDirectory(cacheFolder);
            await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
            _items = items;
            _loadedUtc = DateTime.UtcNow;
            _logger.LogInformation("Franchise Collections: MCU-Liste von aaronperris.com geladen ({Count} Einträge)", items.Count);
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Franchise Collections: aaronperris.com nicht erreichbar");
            if (File.Exists(cachePath))
            {
                try
                {
                    var items = Parse(await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false));
                    var date = File.GetLastWriteTime(cachePath).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
                    errors.Add($"aaronperris.com nicht erreichbar ({ex.Message}) – benutze gespeicherte MCU-Liste vom {date}.");
                    _items = items;
                    _loadedUtc = DateTime.UtcNow - MaxAge + TimeSpan.FromMinutes(30); // in 30 Minuten wieder versuchen
                    return items;
                }
                catch (Exception cacheEx)
                {
                    _logger.LogWarning(cacheEx, "Franchise Collections: gespeicherte MCU-Liste unlesbar");
                }
            }

            errors.Add($"aaronperris.com nicht erreichbar ({ex.Message}) und noch keine gespeicherte Kopie – MCU-Playlists bleiben unverändert.");
            return null;
        }
    }

    public static List<GuideItem> Parse(string json)
        => JsonSerializer.Deserialize<List<GuideItem>>(json) ?? new List<GuideItem>();

    /// <summary>Wandelt die Liste in Einträge um. Unveröffentlichtes (Datum in der Zukunft) wird immer weggelassen.</summary>
    public static List<FranchiseEntry> ToEntries(IReadOnlyList<GuideItem> items, string? order, bool canonOnly, DateOnly today)
    {
        var indexed = items.Select((item, i) => (item, i))
            .Where(x => !canonOnly || x.item.Canon)
            .Where(x => x.item.ReleaseDate is DateOnly d && d <= today)
            .ToList();

        IEnumerable<(GuideItem item, int i)> sorted = (order ?? "release").ToLowerInvariant() switch
        {
            "chronological" or "chronologisch" or "timeline" => indexed.OrderBy(x => x.item.TimelineOrder).ThenBy(x => x.i),
            "sacred" or "sacred-timeline" or "sacredtimeline" => indexed.OrderBy(x => x.item.SacredTimeline).ThenBy(x => x.i),
            _ => indexed.OrderBy(x => x.item.ReleaseDate).ThenBy(x => x.i)
        };

        var result = new List<FranchiseEntry>();
        foreach (var (item, _) in sorted)
        {
            var year = item.ReleaseDate?.Year;
            if (!string.IsNullOrWhiteSpace(item.Show) && item.Season is int s && item.Episode is int e)
            {
                var code = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", s, e);
                result.Add(new FranchiseEntry { Series = string.Empty, Title = item.Show, Year = year, From = code, To = code, FromSource = true });
            }
            else if (!string.IsNullOrWhiteSpace(item.Title))
            {
                result.Add(new FranchiseEntry { Movie = string.Empty, Title = item.Title, Year = year, FromSource = true });
            }
        }

        return result;
    }

    public sealed class GuideItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDateRaw { get; set; }

        [JsonPropertyName("show")]
        public string? Show { get; set; }

        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("episode")]
        public int? Episode { get; set; }

        [JsonPropertyName("canon")]
        public bool Canon { get; set; }

        [JsonPropertyName("timeline_order")]
        public double TimelineOrder { get; set; }

        [JsonPropertyName("sacred_timeline")]
        public double SacredTimeline { get; set; }

        [JsonIgnore]
        public DateOnly? ReleaseDate =>
            DateOnly.TryParseExact(ReleaseDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
