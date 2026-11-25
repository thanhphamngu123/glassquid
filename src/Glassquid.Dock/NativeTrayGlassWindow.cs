using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>Dock material behind Explorer's transparent, interactive tray window.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window disposes its material surface in OnClosed.")]
internal sealed class NativeTrayGlassWindow : GlassWindow
{
    internal const double MotionInset = 10;
    private readonly LiquidGlassSurface _glass = new();
    private readonly System.Windows.Shapes.Path _outline = new() { StrokeThickness = 1 };
    private readonly Canvas _images = new();
    private readonly TranslateTransform _contentSlide = new();
    private double _panelWidth;
    private double _panelHeight;
    private double _motionOffset;

    internal double PaddingX { get; private set; }
    internal double PaddingY { get; private set; }
    internal int OffsetXFromHwnd { get; private set; }
    internal int OffsetYFromHwnd { get; private set; }
    internal bool NativeInputReady { get; private set; }

    internal NativeTrayGlassWindow(SystemTrayBarWindow dock, double width, double height,
        IReadOnlyList<NativeTrayGlassIcon> icons, double paddingX = 0, double paddingY = 0,
        int offsetXFromHwnd = 0, int offsetYFromHwnd = 0)
    {
        Owner = dock;
        Title = "Glassquid tray glass";
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = true;
        Topmost = true;
        Width = width;
        Height = height + MotionInset;
        Opacity = 0;
        var app = ThemeService.Current?.Appearance;
        var initialRadius = app is not null
            ? Math.Max(40, app.DockCornerRadius) * (DockSettingsHost.Current?.Settings.SizeScale ?? 1)
            : 40;
        PanelCornerRadius = new CornerRadius(initialRadius);
        if (dock.PrimaryDisplaySession is { } session) BindPrimaryDisplay(session);
        var content = new Grid { ClipToBounds = true };
        content.Children.Add(_glass);
        var chrome = new Grid { RenderTransform = _contentSlide };
        _outline.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(PanelBorderBrush)) { Source = this });
        chrome.Children.Add(_outline);
        chrome.Children.Add(_images);
        content.Children.Add(chrome);
        Content = content;
        UpdatePresentation(width, height, icons, paddingX, paddingY, offsetXFromHwnd, offsetYFromHwnd);
        _glass.IsBackdropActive = () => IsVisible;
        SizeChanged += (_, _) => { UpdateOutline(); SetMotionFrame(_motionOffset, Opacity); };
        PreviewMouseDown += (_, e) => { if (!NativeInputReady) e.Handled = true; };
        PreviewMouseUp += (_, e) => { if (!NativeInputReady) e.Handled = true; };
    }

    internal void UpdatePresentation(double width, double height, IReadOnlyList<NativeTrayGlassIcon> icons,
        double paddingX, double paddingY, int offsetXFromHwnd, int offsetYFromHwnd)
    {
        PaddingX = paddingX;
        PaddingY = paddingY;
        OffsetXFromHwnd = offsetXFromHwnd;
        OffsetYFromHwnd = offsetYFromHwnd;
        _panelWidth = width;
        _panelHeight = height;
        Width = width;
        Height = height + MotionInset;
        _glass.SetContainerTransitionBounds(new Rect(0, 0, width, height + MotionInset));
        _images.Children.Clear();
        foreach (var icon in icons)
        {
            var image = new Image
            {
                Source = icon.Image,
                Width = icon.Bounds.Width,
                Height = icon.Bounds.Height,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, icon.Bounds.Left + paddingX);
            Canvas.SetTop(image, icon.Bounds.Top + paddingY);
            _images.Children.Add(image);
        }
        UpdateOutline();
        SetMotionFrame(_motionOffset, Opacity);
    }

    internal void SetMotionFrame(double offset, double opacity)
    {
        if (_panelWidth <= 0 || _panelHeight <= 0) return;
        _motionOffset = Math.Clamp(offset, 0, MotionInset);
        _contentSlide.Y = _motionOffset;
        var bounds = new Rect(0, _motionOffset, _panelWidth, _panelHeight);
        var radius = PanelCornerRadius.TopLeft;
        var shape = SquircleGlassGeometry.Create(bounds, radius);
        _glass.SetPanelShape(bounds, radius, shape);
        SetSharedPanelGeometry(shape);
        Opacity = Math.Clamp(opacity, 0, 1);
        SetTransitionShadowShape(bounds, radius, Opacity);
    }

    internal void SetNativeInputReady(bool ready)
    {
        NativeInputReady = ready;
        IsHitTestVisible = !ready;
        if (NativeHandle != 0) TrayIconContextMenuRelay.SetNativeGlassInputTransparent(NativeHandle, ready);
    }

    internal nint NativeHandle => new WindowInteropHelper(this).Handle;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PanelCornerRadiusProperty)
        {
            UpdateOutline();
            if (_glass is not null) SetMotionFrame(_motionOffset, Opacity);
        }
    }

    private void UpdateOutline()
    {
        if (_outline is null || _panelWidth <= 1 || _panelHeight <= 1) return;
        var maxR = Math.Min(_panelWidth - 1, _panelHeight - 1) / 2.0;
        var r = Math.Min(Math.Max(0, PanelCornerRadius.TopLeft - .5), maxR);
        _outline.Data = SquircleGlassGeometry.Create(
            new Rect(.5, .5, _panelWidth - 1, _panelHeight - 1), r);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var scale = DockSettingsHost.Current?.Settings.SizeScale ?? 1;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        return Math.Max(40, appearance.DockCornerRadius) * scale;
    }

    protected override void OnClosed(EventArgs e)
    {
        _glass.Dispose();
        base.OnClosed(e);
    }

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;
    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius)
    {
        var maxR = Math.Min(bounds.Width, bounds.Height) / 2.0;
        return SquircleGlassGeometry.Create(bounds, Math.Min(radius, maxR));
    }
    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) => SquircleGlassGeometry.Contains(bounds, radius, point);
    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) => SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);
    protected override bool UsesSquirclePanelGeometryCore => true;
}

internal sealed record NativeTrayGlassIcon(ImageSource Image, Rect Bounds);
