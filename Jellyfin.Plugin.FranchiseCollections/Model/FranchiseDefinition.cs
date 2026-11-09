using System.Collections.Generic;

namespace Jellyfin.Plugin.FranchiseCollections.Model;

/// <summary>
/// Eine Franchise-Definition, z. B. "MCU Phase 1" oder "Akte X".
/// Aus den Einträgen (in Reihenfolge) entstehen die Sammlung und optional eine Watch-Order-Playlist.
/// </summary>
public class FranchiseDefinition
{
    /// <summary>Eindeutige ID, z. B. "mcu-phase-1". Wird für Verschachtelung und Bilddateien benutzt.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Anzeigename der Sammlung.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mitgelieferte Definition beim ersten Start (bzw. wenn sie neu dazukommt) automatisch aktivieren.</summary>
    public bool DefaultEnabled { get; set; } = true;

    /// <summary>Kurzbeschreibung für die Einstellungsseite.</summary>
    public string? Description { get; set; }

    /// <summary>Sortiername (z. B. "Marvel 01" damit Phasen richtig sortiert sind).</summary>
    public string? SortName { get; set; }

    /// <summary>Beschreibungstext der Sammlung in Jellyfin.</summary>
    public string? Overview { get; set; }

    /// <summary>Sammlung anlegen (Standard: ja).</summary>
    public bool Collection { get; set; } = true;

    /// <summary>Watch-Order-Playlist anlegen (Standard: nein).</summary>
    public bool Playlist { get; set; }

    /// <summary>Name der Playlist. Standard: "&lt;Name&gt; – Reihenfolge".</summary>
    public string? PlaylistName { get; set; }

    /// <summary>Sortierung innerhalb der Sammlung: "PremiereDate" (Standard) oder "SortName".</summary>
    public string? DisplayOrder { get; set; }

    /// <summary>Optional: TMDb-Collection-ID, damit Jellyfin beim Aktualisieren der Metadaten Bilder von TMDb holen kann.</summary>
    public string? TmdbCollectionId { get; set; }

    /// <summary>Optional: Poster-URL.</summary>
    public string? Poster { get; set; }

    /// <summary>Optional: Hintergrundbild-URL.</summary>
    public string? Backdrop { get; set; }

    /// <summary>Einträge in Schau-Reihenfolge.</summary>
    public List<FranchiseEntry> Entries { get; set; } = new();

    /// <summary>
    /// Kleine Korrekturen ohne die ganze Liste zu kopieren: Titel ("Werewolf by Night"), Serien ("Agents of S.H.I.E.L.D.")
    /// oder einzelne Folgen/Staffeln ("Loki S02", "What If...? S01E09"), die weggelassen werden.
    /// </summary>
    public List<string> Exclude { get; set; } = new();
}

/// <summary>
/// Ein Eintrag. Genau eins von Movie / Series / TmdbCollection / Collection sollte gesetzt sein.
/// IDs als "imdb:tt0371746", "tmdb:1726", "tvdb:77398"; mehrere mit "|" trennen.
/// </summary>
public class FranchiseEntry
{
    /// <summary>Ein Film.</summary>
    public string? Movie { get; set; }

    /// <summary>Eine Serie (ganz oder mit From/To nur ein Ausschnitt).</summary>
    public string? Series { get; set; }

    /// <summary>Erste Folge des Ausschnitts, z. B. "S01E01" oder "S06" (= ab Staffel 6).</summary>
    public string? From { get; set; }

    /// <summary>Letzte Folge des Ausschnitts, z. B. "S05E20" oder "S05" (= bis Ende Staffel 5).</summary>
    public string? To { get; set; }

    /// <summary>Alle Filme einer TMDb-Collection, die in der Bibliothek sind (nach Erscheinungsdatum).</summary>
    public string? TmdbCollection { get; set; }

    /// <summary>ID einer anderen Definition → wird als Unter-Sammlung eingehängt; in der Playlist wird ihre Reihenfolge übernommen.</summary>
    public string? Collection { get; set; }

    /// <summary>
    /// Automatische Quelle, die beim Synchronisieren geladen wird und sich selbst in Einträge auflöst.
    /// Aktuell: "aaronperris-mcu" (https://aaronperris.com/mcu/ – MCU inkl. Disney+-/Netflix-Serien, folgengenau).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Reihenfolge der Quelle: "release" (Erscheinen), "chronological" oder "sacred" (Sacred Timeline).</summary>
    public string? Order { get; set; }

    /// <summary>Nur Kanon-Titel der Quelle (Standard: ja). Nein = auch Werbespots, alte Zeichentrickserien usw.</summary>
    public bool CanonOnly { get; set; } = true;

    /// <summary>
    /// Nur Titel übernehmen, die auch in dieser anderen Definition stecken (z. B. "mcu").
    /// So bestimmt die Quelle nur die Reihenfolge, und was dazugehört, bestimmt die Sammlung.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Titel (Originaltitel) – für den Bericht und als Rückfallebene beim Abgleich.</summary>
    public string? Title { get; set; }

    /// <summary>Erscheinungsjahr – Rückfallebene beim Abgleich.</summary>
    public int? Year { get; set; }

    /// <summary>Freitext-Notiz, wird ignoriert.</summary>
    public string? Note { get; set; }

    /// <summary>Nur in der Playlist, nicht in der Sammlung (z. B. ein Film, der schon eine eigene Sammlung hat).</summary>
    public bool PlaylistOnly { get; set; }

    /// <summary>Nur in der Sammlung, nicht in der Playlist.</summary>
    public bool CollectionOnly { get; set; }

    /// <summary>Intern: Eintrag wurde aus einer automatischen Quelle erzeugt (Abgleich nur über Namen).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FromSource { get; set; }

    public string Describe()
    {
        var label = Title
            ?? (string.IsNullOrEmpty(Movie) ? null : Movie)
            ?? (string.IsNullOrEmpty(Series) ? null : Series)
            ?? (Source is not null ? "Quelle " + Source : null)
            ?? (TmdbCollection is not null ? "TMDb-Collection " + TmdbCollection : null)
            ?? Collection
            ?? "?";
        if (Year is not null)
        {
            label += $" ({Year})";
        }

        if (Series is not null && (From is not null || To is not null))
        {
            label += $" [{From ?? "Anfang"}–{To ?? "Ende"}]";
        }

        return label;
    }
}
