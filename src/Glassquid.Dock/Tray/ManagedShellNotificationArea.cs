using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ManagedShell.WindowsTray;

namespace Glassquid.Dock.Tray;

/// <summary>Hosts ManagedShell's tray services for live app icon metadata and callbacks.</summary>
internal sealed class ManagedShellNotificationArea : IDisposable
{
    private static ManagedShellNotificationArea? _instance;
    public static ManagedShellNotificationArea Instance => _instance ??= new ManagedShellNotificationArea();

    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly TrayService _trayService;
    private readonly ExplorerTrayService _explorerTrayService;
    private readonly NotificationArea _notificationArea;
    private bool _initializeQueued;
    private bool _initialized;
    private bool _disposed;

    public ObservableCollection<NotifyIcon> AllIcons => _notificationArea.TrayIcons;

    private ManagedShellNotificationArea()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _trayService = new TrayService();
        _explorerTrayService = new ExplorerTrayService();
        _notificationArea = new NotificationArea(_trayService, _explorerTrayService);
    }

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initializeQueued || _disposed) return;
            _initializeQueued = true;
        }

        // Defer shell enumeration until the dock has returned to the dispatcher. The
        // ManagedShell service can synchronously query Explorer while it initializes.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            lock (_gate)
            {
                if (_disposed || _initialized) return;
                _initialized = true;
            }

            try
            {
                _notificationArea.InitializeForExplorerSnapshot();
                if (_notificationArea.IsFailed)
                {
                    Debug.WriteLine("[Glassquid ManagedShell tray] NotificationArea initialization failed.");
                }

                var icons = _notificationArea.TrayIcons;
                TrayIconContextMenuRelay.Log($"ManagedShell Explorer snapshot loaded {icons.Count} tray icons.");
                foreach (var icon in icons)
                {
                    TrayIconContextMenuRelay.Log(
                        $"Tray callback: title='{icon.Title}', path='{icon.Path}', hwnd=0x{icon.HWnd.ToInt64():X}, uid={icon.UID}, guid={icon.GUID}, callback=0x{icon.CallbackMessage:X}, version={icon.Version}.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid ManagedShell tray] Initialization failed: {ex}");
            }
        }));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_initialized)
        {
            _notificationArea.Dispose();
        }
    }
}
