using System.Windows;
using System.Windows.Threading;
using Glassquid.Platform.Shell;

namespace Glassquid.App;

internal sealed partial class AppHost
{
    private static readonly TimeSpan LauncherWarmRetention = TimeSpan.FromSeconds(30);
    private PerformanceCoordinator? _performance;
    private PerformanceFullscreenMonitor? _performanceFullscreen;
    private DispatcherTimer? _performanceDeadlineTimer;
    private DateTimeOffset? _launcherHiddenSince;
    private bool _applyingPerformance, _performanceApplyPending, _launcherPerformanceSleeping;
    private bool _libraryDirtyWhileSleeping;
    private bool _launcherLibraryDirty;
    private bool _performanceActivationPending;
    private volatile bool _performancePointerInputSuppressed;
    private long _foregroundObservationSequence;

    internal PerformanceState RuntimePerformanceState => _performance?.State ?? PerformanceState.Active;
    internal long RuntimePerformanceGeneration => _performance?.Generation ?? 0;
    internal bool RuntimeManualGameSleep => _performance?.ManualSleep == true;
    internal bool RuntimeFullscreenFallback => _performanceFullscreen?.UsesFallback == true;
    internal object InspectPerformance() => new
    {
        State = RuntimePerformanceState.ToString(),
        Generation = RuntimePerformanceGeneration,
        SuppressionRequested = _performance?.IsSuppressionRequested == true,
        ManualSleep = RuntimeManualGameSleep,
        TrackerPaused = _services?.Running.IsSuspended == true,
        ReconcileCount = _services?.Running.ReconcileCount ?? 0,
        FullscreenFallback = RuntimeFullscreenFallback,
        DockSleeping = _dock?.IsPerformanceSleeping == true,
        LauncherSleeping = _launcherPerformanceSleeping,
    };

    private void InitializePerformanceCoordinator()
    {
        var now = DateTimeOffset.UtcNow;
        _performance = new PerformanceCoordinator(now);
        _performance.SetAutoSuspend(_data!.Settings.Current.Performance.AutoSuspendWhenFullscreen, now);
        _launcherHiddenSince = _launcher?.IsVisible == true ? null : now;
        // Fullscreen/sleep admission must run even while Normal/Render work and
        // input polling keep the lower Background queue busy.
        _performanceDeadlineTimer = new DispatcherTimer(DispatcherPriority.Input, _application.Dispatcher);
        _performanceDeadlineTimer.Tick += (_, _) => { _performanceDeadlineTimer.Stop(); ReconcilePerformance(); };
        _dock!.PerformanceSurfaceVisibilityChanged += OnPerformanceVisibilityChanged;
        _dock.IsVisibleChanged += OnPerformanceWindowVisibilityChanged;
        _launcher!.IsVisibleChanged += OnPerformanceWindowVisibilityChanged;
        _settings!.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
        _performanceFullscreen = new PerformanceFullscreenMonitor(OnPerformanceForegroundObserved);
        ReconcilePerformance();
    }

    private void OnPerformanceForegroundObserved(PerformanceFullscreenObservation observation)
    {
        var sequence = Interlocked.Increment(ref _foregroundObservationSequence);
        if (_application.Dispatcher.HasShutdownStarted) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || _shuttingDown || _performance is null || sequence != Volatile.Read(ref _foregroundObservationSequence)) return;
            var isDesktop = IsDesktopForeground();
            if (isDesktop)
            {
                _performance.ObserveForeground(fullscreen: false, belongsToCurrentProcess: false, DateTimeOffset.UtcNow);
                _performance.Advance(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1));
                WakeDocksForDesktop();
            }
            else if (observation.IsFullscreen is { } fullscreen)
            {
                _performance.ObserveForeground(fullscreen, observation.BelongsToCurrentProcess, DateTimeOffset.UtcNow);
            }
            ReconcilePerformance();
        }));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint window, [System.Runtime.InteropServices.Out] char[] className, int maxCount);

    private static bool IsDesktopForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) return true;
        if (handle == GetShellWindow() || handle == GetDesktopWindow()) return true;
        var name = new char[64];
        var len = GetClassName(handle, name, name.Length);
        if (len <= 0) return false;
        var className = new string(name, 0, len);
        return className is "Progman" or "WorkerW";
    }

    private void WakeDocksForDesktop()
    {
        _dock?.WakeForDesktop();
        _searchDock?.WakeForDesktop();
        _systemTrayBar?.WakeForDesktop();
        foreach (var sec in _secondaryDocks)
        {
            sec.Dock.WakeForDesktop();
            sec.Search?.WakeForDesktop();
            sec.Tray.WakeForDesktop();
        }
    }

    private void OnPerformanceVisibilityChanged(object? sender, EventArgs e) => ReconcilePerformance();
    private void OnPerformanceWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _launcher))
            _launcherHiddenSince = e.NewValue is true ? null : DateTimeOffset.UtcNow;
        ReconcilePerformance();
    }

    private void OnPerformanceSettingsChanged(object? sender, EventArgs e)
    {
        _performance?.SetAutoSuspend(_data!.Settings.Current.Performance.AutoSuspendWhenFullscreen, DateTimeOffset.UtcNow);
        ReconcilePerformance();
    }

    internal void SetManualGameSleep(bool sleeping)
    {
        _application.Dispatcher.VerifyAccess();
        _performance?.SetManualSleep(sleeping, DateTimeOffset.UtcNow);
        ReconcilePerformance();
    }

    private void RequestPerformanceActivation()
    {
        _performanceActivationPending = true;
        _performance?.RequestTemporaryActivation(DateTimeOffset.UtcNow);
        ReconcilePerformance();
    }

    private void SetPerformanceSessionSuspended(bool suspended)
    {
        if (_application.Dispatcher.HasShutdownStarted) return;
        _application.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _performance is null) return;
            _performance.SetSessionSuspended(suspended, DateTimeOffset.UtcNow);
            if (!suspended) _performanceFullscreen?.Refresh();
            ReconcilePerformance();
        }));
    }

    private void ReconcilePerformance()
    {
        if (_performance is null || _disposed || _shuttingDown) return;
        if (_applyingPerformance) { _performanceApplyPending = true; return; }
        _applyingPerformance = true;
        try
        {
            do
            {
                _performanceApplyPending = false;
                var now = DateTimeOffset.UtcNow;
                _performance.SetVisibleSurfaces(_dock?.IsPerformanceCaptureRequested == true,
                    _launcher?.IsVisible == true || _performanceActivationPending, now);
                var state = _performance.State;
                var sleeping = state == PerformanceState.Sleeping;
                var suppressed = _performance.IsSuppressionRequested;
                var temporary = _performance.IsTemporaryActivation;

                _dock?.SetPerformanceSleeping(suppressed);
                _systemTrayBar?.SetPerformanceSleeping(suppressed);
                _searchDock?.SetPerformanceSleeping(suppressed);
                _dock?.SetBackgroundCaptureSleeping(sleeping && !suppressed);
                foreach (var sec in _secondaryDocks)
                {
                    sec.Dock.SetPerformanceSleeping(suppressed);
                    sec.Tray.SetPerformanceSleeping(suppressed);
                    sec.Search?.SetPerformanceSleeping(suppressed);
                    sec.Dock.SetBackgroundCaptureSleeping(sleeping && !suppressed);
                }
                if (sleeping && suppressed && _launcher?.IsVisible == true && !_launcher.IsClosingContainer) HideLauncher();

                var launcherVisible = _launcher?.IsVisible == true;
                _performancePointerInputSuppressed = suppressed && !launcherVisible;
                var launcherShouldSleep = !launcherVisible && !temporary
                    && (suppressed || _launcherHiddenSince is { } hidden && now >= hidden + LauncherWarmRetention);
                if (_launcherPerformanceSleeping != launcherShouldSleep)
                {
                    _launcherPerformanceSleeping = launcherShouldSleep;
                    _launcher?.SetPerformanceSleeping(launcherShouldSleep);
                }
                _launcher?.SetBackgroundWorkSuspended(!launcherVisible && !temporary || sleeping && suppressed);
                _services?.Running.SetSuspended(sleeping || state == PerformanceState.WarmHidden || suppressed && !launcherVisible);
                if (!sleeping && _libraryDirtyWhileSleeping)
                {
                    _libraryDirtyWhileSleeping = false;
                    RefreshLibraryForPerformance();
                }
                if (!sleeping && _launcherLibraryDirty && (launcherVisible || _performanceActivationPending))
                { _launcherLibraryDirty = false; _launcher?.RefreshLibrary(); }
                if (state == PerformanceState.Waking) _performance.CompleteWake(now);
                SchedulePerformanceDeadline(now);
            } while (_performanceApplyPending);
        }
        finally { _applyingPerformance = false; }
    }

    private void SchedulePerformanceDeadline(DateTimeOffset now)
    {
        _performanceDeadlineTimer!.Stop();
        var deadline = _performance!.NextTransitionAt;
        if (!_launcherPerformanceSleeping && !_performance.IsTemporaryActivation && _launcherHiddenSince is { } hidden)
        {
            var launcherDeadline = hidden + LauncherWarmRetention;
            if (!deadline.HasValue || launcherDeadline < deadline) deadline = launcherDeadline;
        }
        if (deadline is not { } next) return;
        _performanceDeadlineTimer.Interval = next > now ? next - now : TimeSpan.FromMilliseconds(1);
        _performanceDeadlineTimer.Start();
    }

    private void RefreshLibraryForPerformance()
    {
        if (_disposed || _shuttingDown) return;
        if (_performance?.State == PerformanceState.Sleeping)
        { _libraryDirtyWhileSleeping = true; return; }
        _dock?.RefreshTiles();
        foreach (var sec in _secondaryDocks)
        {
            sec.Dock.RefreshTiles();
        }
        if (_launcher?.IsVisible == true || _performanceActivationPending) _launcher?.RefreshLibrary();
        else _launcherLibraryDirty = true;
    }

    private void DisposePerformanceCoordinator()
    {
        _performanceDeadlineTimer?.Stop();
        _performanceDeadlineTimer = null;
        _performanceFullscreen?.Dispose();
        _performanceFullscreen = null;
        if (_dock is not null)
        {
            _dock.PerformanceSurfaceVisibilityChanged -= OnPerformanceVisibilityChanged;
            _dock.IsVisibleChanged -= OnPerformanceWindowVisibilityChanged;
        }
        if (_launcher is not null) _launcher.IsVisibleChanged -= OnPerformanceWindowVisibilityChanged;
        if (_settings is not null) _settings.PerformanceSettingsChanged -= OnPerformanceSettingsChanged;
        _performance = null;
    }
}
