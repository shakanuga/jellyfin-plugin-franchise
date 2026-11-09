using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Tasks;

/// <summary>
/// Wenn jemand einen Film/eine Folge aus einer unserer Playlists zu Ende schaut (oder als gesehen/ungesehen markiert),
/// werden die Playlists kurz danach aktualisiert – das Gesehene verschwindet, oben steht das Nächste.
/// Mehrere Ereignisse kurz hintereinander werden zu einem Lauf zusammengefasst.
/// </summary>
public sealed class WatchedListener : IHostedService, IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(20);

    private readonly IUserDataManager _userDataManager;
    private readonly FranchiseSyncService _service;
    private readonly ILogger<WatchedListener> _logger;
    private readonly Timer _timer;

    public WatchedListener(IUserDataManager userDataManager, FranchiseSyncService service, ILogger<WatchedListener> logger)
    {
        _userDataManager = userDataManager;
        _service = service;
        _logger = logger;
        _timer = new Timer(_ => RunSync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed))
        {
            return;
        }

        if (Plugin.Instance?.Configuration.RemoveWatchedFromPlaylists != true || e.Item is null || !_service.IsManaged(e.Item.Id))
        {
            return;
        }

        // (neu) starten – so werden mehrere Ereignisse gebündelt
        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private void RunSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _service.SyncAsync(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Franchise Collections: Aktualisierung nach Wiedergabe fehlgeschlagen");
            }
        });
    }
}
