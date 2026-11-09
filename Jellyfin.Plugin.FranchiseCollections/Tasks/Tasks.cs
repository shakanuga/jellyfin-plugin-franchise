using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FranchiseCollections.Tasks;

/// <summary>Registriert den Sync-Dienst im Jellyfin-DI-Container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<FranchiseSyncService>();
    }
}

/// <summary>Geplante Aufgabe "Franchise-Sammlungen aktualisieren" (Dashboard → Geplante Aufgaben, und Button auf der Einstellungsseite).</summary>
public class SyncFranchisesTask : IScheduledTask
{
    public const string TaskKey = "FranchiseCollectionsSync";

    private readonly FranchiseSyncService _service;

    public SyncFranchisesTask(FranchiseSyncService service)
    {
        _service = service;
    }

    public string Name => "Franchise-Sammlungen aktualisieren";

    public string Key => TaskKey;

    public string Description => "Gleicht die Franchise-Definitionen mit der Bibliothek ab und aktualisiert Sammlungen und Watch-Order-Playlists.";

    public string Category => "Franchise Collections";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.SyncAsync(progress, cancellationToken);

    // Kein fester Zeitplan nötig – läuft nach jedem Bibliotheks-Scan (siehe unten). Trigger lassen sich im Dashboard ergänzen.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

/// <summary>Läuft automatisch am Ende jedes Bibliotheks-Scans.</summary>
public class SyncAfterScanTask : ILibraryPostScanTask
{
    private readonly FranchiseSyncService _service;

    public SyncAfterScanTask(FranchiseSyncService service)
    {
        _service = service;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.SyncAfterLibraryScan != true)
        {
            return Task.CompletedTask;
        }

        return _service.SyncAsync(progress, cancellationToken);
    }
}
