using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.FranchiseCollections.Model;

namespace Jellyfin.Plugin.FranchiseCollections.Sync;

/// <summary>
/// Lädt Definitionen aus drei Quellen (spätere überschreiben frühere bei gleicher ID):
/// 1. mitgelieferte (eingebettete JSON-Dateien), 2. *.json im Plugin-Ordner, 3. JSON aus der Einstellungsseite.
/// </summary>
public static class DefinitionLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false
    };

    public static List<FranchiseDefinition> LoadBuiltIns()
    {
        var result = new List<FranchiseDefinition>();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Definitions.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            result.AddRange(Parse(reader.ReadToEnd()));
        }

        return result;
    }

    /// <summary>Liest ein einzelnes Objekt, ein Array oder { "definitions": [...] }.</summary>
    public static List<FranchiseDefinition> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<FranchiseDefinition>();
        }

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("definitions", out var defs))
        {
            root = defs;
        }

        List<FranchiseDefinition> list = root.ValueKind switch
        {
            JsonValueKind.Array => root.Deserialize<List<FranchiseDefinition>>(JsonOptions) ?? new(),
            JsonValueKind.Object => new List<FranchiseDefinition> { root.Deserialize<FranchiseDefinition>(JsonOptions)! },
            _ => new List<FranchiseDefinition>()
        };

        return list.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Id)).ToList();
    }

    /// <summary>Lädt alle aktiven Definitionen. Fehler in einzelnen Quellen landen in <paramref name="errors"/>.</summary>
    public static Dictionary<string, FranchiseDefinition> LoadActive(
        IReadOnlyCollection<string> enabledBuiltIns,
        string definitionsFolder,
        string customJson,
        List<string> errors)
    {
        var all = new Dictionary<string, FranchiseDefinition>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var enabled = new HashSet<string>(enabledBuiltIns, StringComparer.OrdinalIgnoreCase);
            foreach (var def in LoadBuiltIns().Where(d => enabled.Contains(d.Id)))
            {
                all[def.Id] = def;
            }
        }
        catch (Exception ex)
        {
            errors.Add("Mitgelieferte Definitionen: " + ex.Message);
        }

        if (Directory.Exists(definitionsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(definitionsFolder, "*.json")
                         .Where(f => !Path.GetFileName(f).StartsWith('_'))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    foreach (var def in Parse(File.ReadAllText(file)))
                    {
                        all[def.Id] = def;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        try
        {
            foreach (var def in Parse(customJson))
            {
                all[def.Id] = def;
            }
        }
        catch (Exception ex)
        {
            errors.Add("Eigene Definitionen (Einstellungsseite): " + ex.Message);
        }

        // Unter-Sammlungen, die nur als Kind gebraucht werden, mitnehmen (z. B. "marvel" braucht "mcu").
        var builtInById = new Lazy<Dictionary<string, FranchiseDefinition>>(() =>
            LoadBuiltIns().GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase));
        var queue = new Queue<FranchiseDefinition>(all.Values);
        while (queue.Count > 0)
        {
            var def = queue.Dequeue();
            foreach (var childId in def.Entries.Select(e => e.Collection ?? e.Scope).Where(id => id is not null).Select(id => id!))
            {
                if (!all.ContainsKey(childId) && builtInById.Value.TryGetValue(childId, out var child))
                {
                    all[child.Id] = child;
                    queue.Enqueue(child);
                }
            }
        }

        return all;
    }
}
