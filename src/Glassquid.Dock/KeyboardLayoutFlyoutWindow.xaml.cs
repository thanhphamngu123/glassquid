using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Xml.Linq;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>A Glassquid-styled keyboard layout flyout for the system tray dock.</summary>
public partial class KeyboardLayoutFlyoutWindow : GlassWindow
{
    private static readonly Lazy<Geometry> GridShortcutGeometry = new(LoadGridShortcutGeometry);
    private readonly SystemTrayBarWindow _dock;
    private readonly IReadOnlyList<KeyboardLayoutOption> _layouts;
    private readonly nint _targetWindow;
    private readonly nint _focusWindow;
    private bool _glassRevealPending;
    private bool _isClosing;
    private bool _closeQueued;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal KeyboardLayoutFlyoutWindow(
        SystemTrayBarWindow dock,
        nint targetWindow,
        nint focusWindow,
        IReadOnlyList<KeyboardLayoutOption> layouts)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(layouts);
        _dock = dock;
        _layouts = layouts;
        _targetWindow = targetWindow;
        _focusWindow = focusWindow;
        Owner = dock;

        InitializeComponent();
        GridShortcutIcon.Data = GridShortcutGeometry.Value;
        LayoutChoicesList.ItemsSource = layouts;
        LayoutChoicesList.Visibility = layouts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLayoutsText.Visibility = layouts.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
    }

    private static Geometry LoadGridShortcutGeometry()
    {
        const string resourceName = "Glassquid.Dock.Assets.Keyboard.ic_fluent_grid_24_regular.svg";
        using var stream = typeof(KeyboardLayoutFlyoutWindow).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The icon resource '{resourceName}' was not embedded.");
        var document = XDocument.Load(stream);
        var pathData = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "path")?
            .Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
            throw new InvalidDataException($"The icon resource '{resourceName}' does not contain an SVG path.");

        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndPosition();
        GlassSurface.PrepareRevealForWindow(this);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PrimaryDisplaySession is { IsAvailable: false }) Hide();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        AnimatePopupOpen();
    }

    private void MeasureContentAndPosition()
    {
        if (Content is not UIElement content) return;

        var workArea = WorkAreaDip;
        content.Measure(new Size(workArea.Width, workArea.Height));
        var desired = content.DesiredSize;
        Width = Math.Ceiling(desired.Width);
        Height = Math.Ceiling(desired.Height);
        SizeToContent = SizeToContent.Manual;

        var anchor = _dock.GetInputLanguageAnchorPoint();
        Left = Math.Clamp(anchor.X - 42, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - Width - 4));
        Top = _dock.IsTopPlacement
            ? Math.Clamp(anchor.Y + 12, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - Height - 4))
            : Math.Clamp(anchor.Y - Height - 12, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - Height - 4));
    }

    private void AnimatePopupOpen()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fromY = _dock.IsTopPlacement ? -14 : 14;
        PopupSlideTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private async void OnLayoutChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyboardLayoutOption option }) return;
        if (option.IsCurrent)
        {
            CloseAndRestoreForeground();
            return;
        }

        // Restore the app that had focus before asking it to change its input locale.
        // Posting while this Glassquid flyout is still foreground can be ignored by some apps.
        _isClosing = true;
        Close();
        var targetIsForeground = await RestoreTargetForegroundAsync();

        var focusWindow = IsWindow(_focusWindow) ? _focusWindow : _targetWindow;
        _ = KeyboardLayoutCatalog.RequestChange(focusWindow, option.Handle);
        await Task.Delay(180);
        if (!KeyboardLayoutCatalog.IsActive(_targetWindow, option.Handle)
            && _targetWindow != 0 && _targetWindow != _focusWindow)
        {
            // Some applications handle the request only on their top-level window.
            _ = KeyboardLayoutCatalog.RequestChange(_targetWindow, option.Handle);
            await Task.Delay(220);
        }

        if (!KeyboardLayoutCatalog.IsActive(_targetWindow, option.Handle) && targetIsForeground)
        {
            // The Win+Space fallback must only run after confirming that the intended app
            // owns the foreground thread. Otherwise it can switch Glassquid's own layout.
            var cycles = KeyboardLayoutCatalog.GetShortcutCycles(_targetWindow, option.Handle, _layouts);
            if (cycles > 0)
            {
                await KeyboardLayoutCatalog.SendWindowsSpaceCyclesAsync(cycles);
                await Task.Delay(220);
            }
        }

        _dock.RefreshInputLanguageStatus();
        if (!KeyboardLayoutCatalog.IsActive(_targetWindow, option.Handle))
        {
            _dock.ShowInputLanguageFeedback(targetIsForeground
                ? "Ứng dụng chưa nhận bố cục bàn phím. Hãy thử Win + Space trong ứng dụng đó."
                : "Không thể đưa ứng dụng cần đổi bàn phím lên trước. Hãy chọn lại ứng dụng rồi thử lại.");
        }
    }

    private async Task<bool> RestoreTargetForegroundAsync()
    {
        if (!IsWindow(_targetWindow)) return false;

        _ = SetForegroundWindow(_targetWindow);
        var targetThread = GetWindowThreadProcessId(_targetWindow, out _);
        if (targetThread == 0) return false;

        // SetForegroundWindow can report success before Explorer updates the foreground
        // window. Wait briefly and verify the foreground thread before sending Win+Space.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var foreground = GetForegroundWindow();
            if (foreground != 0 && GetWindowThreadProcessId(foreground, out _) == targetThread)
                return true;

            await Task.Delay(25);
        }

        return false;
    }

    private void OnMoreKeyboardSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:typing") { UseShellExecute = true });
            _isClosing = true;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Không mở được Cài đặt bàn phím.";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosing || _closeQueued) return;
        _closeQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _closeQueued = false;
            if (!_isClosing && IsVisible && !IsActive)
            {
                _isClosing = true;
                Close();
            }
        }));
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseAndRestoreForeground();
    }

    private void CloseAndRestoreForeground()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
        if (_targetWindow != 0) _ = SetForegroundWindow(_targetWindow);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
