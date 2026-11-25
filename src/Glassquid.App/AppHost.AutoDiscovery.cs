using System.Windows.Threading;
using Glassquid.AppServices;

namespace Glassquid.App;

internal sealed partial class AppHost
{
    private static readonly TimeSpan InstalledAppsRescanInterval = TimeSpan.FromMinutes(15);
    private const int InstalledAppsImportBatchSize = 12;

    private DispatcherTimer? _installedAppsScanTimer;
    private bool _installedAppsScanInProgress;
    private int _pendingInstalledAppsNotice;

    private void StartInstalledAppsAutoDiscovery()
    {
        if (_installedAppsScanTimer is not null) return;

        _installedAppsScanTimer = new DispatcherTimer(DispatcherPriority.Background, _application.Dispatcher)
        {
            Interval = InstalledAppsRescanInterval,
        };
        _installedAppsScanTimer.Tick += OnInstalledAppsScanTimerTick;
        _installedAppsScanTimer.Start();
        _ = _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _ = ScanInstalledAppsAsync()));
    }

    private void StopInstalledAppsAutoDiscovery()
    {
        if (_installedAppsScanTimer is null) return;
        _installedAppsScanTimer.Stop();
        _installedAppsScanTimer.Tick -= OnInstalledAppsScanTimerTick;
        _installedAppsScanTimer = null;
    }

    private void OnInstalledAppsScanTimerTick(object? sender, EventArgs e) => _ = ScanInstalledAppsAsync();

    private async Task ScanInstalledAppsAsync()
    {
        if (_shuttingDown || _disposed || _installedAppsScanInProgress || _services is null) return;
        _installedAppsScanInProgress = true;

        try
        {
            var pruned = await Task.Run(() => _services.Maintenance.PruneMissingTargets());
            if (pruned > 0)
                AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Đã tự động dọn {pruned} ứng dụng đã gỡ khỏi thư viện.");
            if (_shuttingDown || _disposed || _services is null) return;

            // Directory enumeration and shell display-name lookup run off the UI thread.
            var scan = await Task.Run(AppServiceHost.ScanStartMenu);
            if (_shuttingDown || _disposed || _services is null) return;

            var added = 0;
            var failures = 0;
            foreach (var batch in scan.Shortcuts.Chunk(InstalledAppsImportBatchSize))
            {
                if (_shuttingDown || _disposed || _services is null) break;
                var currentBatch = batch;
                var currentServices = _services;
                var result = await Task.Run(() => currentServices.ImportNewStartMenuShortcuts(currentBatch));
                added += result.Added.Count;
                failures += result.Failures.Count;

                // Let the window pump input between batches when many apps are discovered.
                await Task.Yield();
            }

            if (added > 0)
            {
                AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Tự động thêm {added} ứng dụng mới từ menu Bắt đầu.");
                if (_launcher?.IsVisible == true)
                    _launcher.NotifyAutomaticallyAddedApps(added);
                else
                    _pendingInstalledAppsNotice += added;
            }

            if (failures > 0)
                AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Không thể tự động thêm {failures} lối tắt trong menu Bắt đầu.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Tự quét ứng dụng đã cài thất bại: {ex.Message}");
        }
        finally
        {
            _installedAppsScanInProgress = false;
        }
    }

    private void NotifyPendingInstalledApps()
    {
        if (_pendingInstalledAppsNotice <= 0 || _launcher?.IsVisible != true) return;
        var count = _pendingInstalledAppsNotice;
        _pendingInstalledAppsNotice = 0;
        _launcher.NotifyAutomaticallyAddedApps(count);
    }
}
