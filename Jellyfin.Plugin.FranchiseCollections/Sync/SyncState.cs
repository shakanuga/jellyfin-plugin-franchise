using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Merkt sich, welche Jellyfin-Sammlung/Playlist zu welcher Definition gehört und welche Bilder schon gesetzt wurden.
/// Liegt als _state.json im Plugin-Ordner (nicht in der Plugin-Konfiguration, damit Speichern auf der Einstellungsseite nichts überschreibt).
/// </summary>
public class SyncState
{
    public Dictionary<string, string> Collections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Playlists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"&lt;defId&gt;|Primary" → Quelle (URL oder Pfad+Änderungszeit) des zuletzt gesetzten Bildes.</summary>
    public Dictionary<string, string> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SyncState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path), DefinitionLoader.JsonOptions);
                if (state is not null)
                {
                    // Vergleich ohne Groß-/Kleinschreibung wiederherstellen
                    return new SyncState
                    {
                        Collections = new Dictionary<string, string>(state.Collections ?? new(), StringComparer.OrdinalIgnoreCase),
                        Playlists = new Dictionary<string, string>(state.Playlists ?? new(), StringComparer.OrdinalIgnoreCase),
                        Images = new Dictionary<string, string>(state.Images ?? new(), StringComparer.OrdinalIgnoreCase)
                    };
                }
            }
        }
        catch (Exception)
        {
            // kaputte Datei → neu anfangen; Sammlungen werden über Markierung/Name wiedergefunden
        }

        return new SyncState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
