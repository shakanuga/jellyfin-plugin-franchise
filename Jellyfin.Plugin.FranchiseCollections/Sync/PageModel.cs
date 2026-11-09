using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Struktur der Franchises für die Franchise-Seite, wird bei jeder Synchronisation als _model.json gespeichert.
/// Enthält nur IDs – Gesehen-Status usw. wird beim Aufruf der Seite pro Benutzer ergänzt.
/// </summary>
public class PageModel
{
    public DateTime GeneratedUtc { get; set; }

    public List<PageDef> Defs { get; set; } = new();

    public static PageModel? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PageModel>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));
}

public class PageDef
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Overview { get; set; }

    public string? Kicker { get; set; }

    /// <summary>Jellyfin-Sammlung (für Bilder und Link).</summary>
    public Guid? CollectionId { get; set; }

    /// <summary>Titel, dessen Bilder als Hintergrund dienen.</summary>
    public Guid? ArtworkItemId { get; set; }

    /// <summary>Unter-Sammlungen in Reihenfolge.</summary>
    public List<string> Children { get; set; } = new();

    /// <summary>Filme und Serien dieser Definition (ohne Unter-Sammlungen) in Definitions-Reihenfolge.</summary>
    public List<Guid> Media { get; set; } = new();

    /// <summary>Hat eine Watch-Order (Filme und einzelne Folgen in Reihenfolge).</summary>
    public bool HasOrder { get; set; }

    public string? OrderName { get; set; }

    public List<Guid> Order { get; set; } = new();

    /// <summary>Gehört als Reihenfolge zu dieser anderen Definition (z. B. "mcu").</summary>
    public string? Scope { get; set; }

    public bool HasCollection { get; set; }
}
