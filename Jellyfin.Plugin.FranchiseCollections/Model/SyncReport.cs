using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FranchiseCollections.Model;

/// <summary>Ergebnis eines Synchronisationslaufs.</summary>
public class SyncReport
{
    public DateTime StartedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public List<string> Errors { get; set; } = new();

    public List<DefinitionReport> Definitions { get; set; } = new();
}

/// <summary>Ergebnis für eine Definition.</summary>
public class DefinitionReport
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Anzahl Einträge in der Sammlung (Filme, Serien, Unter-Sammlungen).</summary>
    public int CollectionItems { get; set; }

    /// <summary>Anzahl Einträge in der Playlist (Filme + Folgen).</summary>
    public int PlaylistItems { get; set; }

    /// <summary>Einträge der Definition gesamt / gefunden.</summary>
    public int EntriesTotal { get; set; }

    public int EntriesFound { get; set; }

    /// <summary>Was in der Bibliothek fehlt.</summary>
    public List<string> Missing { get; set; } = new();

    public string? Error { get; set; }
}
