using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FranchiseCollections.Configuration;

/// <summary>
/// Einstellungen des Plugins. Wird von Jellyfin als XML gespeichert.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>IDs der mitgelieferten Definitionen, die aktiv sind.</summary>
    public string[] EnabledBuiltIns { get; set; } = Array.Empty<string>();

    /// <summary>Beim ersten Start werden alle mitgelieferten Definitionen aktiviert; danach entscheidet der Benutzer.</summary>
    public bool BuiltInsInitialized { get; set; }

    /// <summary>Wird beim Start vom Plugin befüllt, damit die Einstellungsseite die mitgelieferten Definitionen anzeigen kann.</summary>
    public BuiltInInfo[] BuiltInCatalog { get; set; } = Array.Empty<BuiltInInfo>();

    /// <summary>Eigene Definitionen als JSON (ein Objekt oder ein Array). Gleiche ID überschreibt eine mitgelieferte Definition.</summary>
    public string CustomDefinitionsJson { get; set; } = string.Empty;

    /// <summary>Benutzer, dem die Watch-Order-Playlists gehören. Leer = erster Benutzer (meist der Admin).</summary>
    public string PlaylistOwnerUserId { get; set; } = string.Empty;

    /// <summary>Playlists für alle Benutzer sichtbar machen.</summary>
    public bool PlaylistsPublic { get; set; } = true;

    /// <summary>Nach jedem Bibliotheks-Scan automatisch synchronisieren.</summary>
    public bool SyncAfterLibraryScan { get; set; } = true;

    /// <summary>Sammlungen auch anlegen, wenn (noch) nichts davon in der Bibliothek ist.</summary>
    public bool CreateEmptyCollections { get; set; }

    /// <summary>Titel+Jahr als Rückfallebene, wenn keine ID passt.</summary>
    public bool MatchByTitleAndYear { get; set; } = true;

    /// <summary>Ergebnis des letzten Laufs als JSON (für die Einstellungsseite).</summary>
    public string LastReportJson { get; set; } = string.Empty;
}

/// <summary>Kurzinfo zu einer mitgelieferten Definition.</summary>
public class BuiltInInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>IDs der Definitionen, die diese als Unter-Sammlung enthalten.</summary>
    public string[] IncludedBy { get; set; } = Array.Empty<string>();
}
