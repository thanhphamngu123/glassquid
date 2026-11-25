using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>
/// Cửa sổ hộp thoại tùy chọn nguồn xuất hiện ở giữa màn hình với phong cách kính mờ Liquid Glass của thanh Dock.
/// </summary>
public partial class PowerDialogWindow : GlassWindow
{
    private bool _glassRevealPending;
    private bool _isClosing;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(36, appearance.DockCornerRadius);
    }

    public PowerDialogWindow()
    {
        InitializeComponent();

        PanelCornerRadius = new CornerRadius(36);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        CenterOnScreen();
        GlassSurface.PrepareRevealForWindow(this);
    }

    private void CenterOnScreen()
    {
        if (Content is not UIElement content) return;

        var workArea = SystemParameters.WorkArea;
        content.Measure(new Size(workArea.Width, workArea.Height));
        var desired = content.DesiredSize;

        Width = Math.Ceiling(desired.Width);
        Height = Math.Ceiling(desired.Height);
        SizeToContent = SizeToContent.Manual;

        Left = Math.Round(workArea.Left + (workArea.Width - Width) / 2);
        Top = Math.Round(workArea.Top + (workArea.Height - Height) / 2);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        AnimateEntrance();
    }

    private void AnimateEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);

        PopupVisualRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        PopupScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1.0, duration) { EasingFunction = ease });

        PopupScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1.0, duration) { EasingFunction = ease });

        PopupSlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, duration) { EasingFunction = ease });
    }

    private void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(160);

        var fadeAnim = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        fadeAnim.Completed += (_, _) => Close();

        PopupVisualRoot.BeginAnimation(OpacityProperty, fadeAnim);
        PopupScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.0, 0.95, duration) { EasingFunction = ease });
        PopupScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.0, 0.95, duration) { EasingFunction = ease });
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && FindVisualChild<PhoneBorder>(btn) is { RenderTransform: ScaleTransform st })
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var anim = new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void OnTileMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && FindVisualChild<PhoneBorder>(btn) is { RenderTransform: ScaleTransform st })
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var desc = FindVisualChild<T>(child);
            if (desc is not null) return desc;
        }
        return null;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        CloseWithAnimation();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithAnimation();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        Close();
        try { LockWorkStation(); } catch { }
    }

    private void OnSleepClick(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            SetSuspendState(false, true, true);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private void OnShutDownClick(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/l")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
