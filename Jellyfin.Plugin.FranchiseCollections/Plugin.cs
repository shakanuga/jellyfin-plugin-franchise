using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using Jellyfin.Plugin.FranchiseCollections.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FranchiseCollections;

/// <summary>
/// Franchise-Sammlungen: große, verschachtelte Sammlungen (z. B. MCU mit Phasen)
/// und Watch-Order-Playlists, in denen Filme und Serienfolgen gemischt in der richtigen Reihenfolge stehen.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "4aa153a4-4b69-49df-b058-81da9a1685b2";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DefinitionsFolder = Path.Combine(applicationPaths.PluginConfigurationsPath, "FranchiseCollections");
        RefreshBuiltInCatalog();
    }

    /// <summary>Ordner für eigene *.json-Definitionen und Bilder (&lt;id&gt;-poster.jpg, &lt;id&gt;-backdrop.jpg).</summary>
    public string DefinitionsFolder { get; }

    private void RefreshBuiltInCatalog()
    {
        try
        {
            Directory.CreateDirectory(DefinitionsFolder);
            var builtIns = DefinitionLoader.LoadBuiltIns();
            var config = Configuration;
            config.BuiltInCatalog = builtIns
                .Select(d => new BuiltInInfo
                {
                    Id = d.Id,
                    Name = d.Name,
                    Description = d.Description ?? string.Empty,
                    IncludedBy = builtIns
                        .Where(p => p.Entries.Any(e => string.Equals(e.Collection, d.Id, StringComparison.OrdinalIgnoreCase)))
                        .Select(p => p.Id)
                        .ToArray(),
                    Json = System.Text.Json.JsonSerializer.Serialize(d, PrettyJson)
                })
                .ToArray();

            // Beim ersten Start – und wenn mit einem Update neue mitgelieferte Definitionen dazukommen – die "obersten"
            // aktivieren. Unter-Sammlungen werden automatisch mitgenommen.
            var children = new HashSet<string>(
                builtIns.SelectMany(d => d.Entries).Where(e => e.Collection is not null).Select(e => e.Collection!),
                StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(config.BuiltInsInitialized ? config.KnownBuiltIns : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var enabled = new List<string>(config.BuiltInsInitialized ? config.EnabledBuiltIns : Array.Empty<string>());
            foreach (var def in builtIns.Where(d => !known.Contains(d.Id)))
            {
                if (def.DefaultEnabled && !children.Contains(def.Id) && !enabled.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
                {
                    enabled.Add(def.Id);
                }
            }

            config.EnabledBuiltIns = enabled.ToArray();
            config.KnownBuiltIns = builtIns.Select(d => d.Id).ToArray();
            config.BuiltInsInitialized = true;

            SaveConfiguration();
        }
        catch (Exception)
        {
            // Darf den Start von Jellyfin nie verhindern.
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Plugin? Instance { get; private set; }

    public override string Name => "Franchise Collections";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Erstellt Franchise-Sammlungen (z. B. MCU mit Phasen, Star Trek, Akte X) mit Filmen und Serien sowie Watch-Order-Playlists.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
