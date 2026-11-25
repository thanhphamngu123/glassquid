using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glassquid.Platform.Shell;
using Glassquid.UI.Theming;

namespace Glassquid.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    // A retained drawing above the material, below the existing content controls.
    // It has no layout, input, focus, HWND, capture, or shader responsibilities.
    private readonly DrawingVisual _pointerHighlightVisual = new();
    private GlassPointerHighlight? _pointerHighlight;

    private void InitializePointerHighlight()
    {
        AddVisualChild(_pointerHighlightVisual);
        _pointerHighlight = new GlassPointerHighlight(this, _pointerHighlightVisual);
    }

    internal GlassPointerHighlight? PointerHighlight => _pointerHighlight;

    internal bool CanShowPointerHighlight => !_performanceSleeping && IsLoaded && IsVisible
        && IsBackdropActive?.Invoke() != false && ThemeService.Current?.UseOpaquePanels != true
        && Window.GetWindow(this) is GlassWindow { IsVisible: true, SurfaceMode: GlassSurfaceMode.OpticalGlass };

    internal (Rect Bounds, double Radius, Geometry? Shape) PointerHighlightPanel =>
        (_panelShape ?? new Rect(RenderSize),
            _panelShape is null ? (Window.GetWindow(this) as GlassWindow)?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius,
            _panelGeometry ?? Clip);
}

/// <summary>Decorative pointer light. Mouse events only replace a target; one timer draws at most 60 Hz.</summary>
internal sealed class GlassPointerHighlight
{
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1d / 60);
    private static readonly TimeSpan RestInterval = TimeSpan.FromMilliseconds(200);
    private static readonly FullscreenOptions FullscreenPolicy = new() { IncludeShellState = true };
    private static long _fullscreenCheckedAt;
    private static bool _fullscreen;

    private readonly LiquidGlassSurface _surface;
    private readonly DrawingVisual _visual;
    private readonly DispatcherTimer _timer;
    private readonly RadialGradientBrush _body = CreateGradient(10, 3);
    private readonly RadialGradientBrush _rim = CreateGradient(112, 28);
    private readonly Pen _rimPen;
    private Window? _owner;
    private Geometry? _outline, _suppliedShape;
    private Rect _bounds;
    private double _radius;
    private Point _target, _position;
    private bool _inside, _hasPosition, _hasDrawing;
    private double _strength;
    private long _lastTick;

    internal GlassPointerHighlight(LiquidGlassSurface surface, DrawingVisual visual)
    {
        _surface = surface;
        _visual = visual;
        _rimPen = new Pen(_rim, 1.2);
        _timer = new DispatcherTimer(DispatcherPriority.Render, surface.Dispatcher) { Interval = FrameInterval };
        _timer.Tick += OnTick;
        surface.Loaded += OnLoaded;
        surface.Unloaded += OnUnloaded;
        surface.IsVisibleChanged += OnVisibilityChanged;
    }

    internal bool IsTimerRunning => _timer.IsEnabled;
    internal Point Position => _position;
    internal double Strength => _strength;
    internal long DrawCount { get; private set; }
    internal Func<bool> IsFullscreen { get; set; } = ReadFullscreen;

    private static RadialGradientBrush CreateGradient(byte center, byte middle) => new()
    {
        MappingMode = BrushMappingMode.Absolute,
        GradientStops = new GradientStopCollection
        {
            new(Color.FromArgb(center, 255, 255, 255), 0),
            new(Color.FromArgb(middle, 255, 255, 255), .55),
            new(Colors.Transparent, 1),
        },
    };

    private static bool ReadFullscreen()
    {
        // Shared cache is queried only while a light is active, never by an idle background loop.
        var now = Stopwatch.GetTimestamp();
        if (_fullscreenCheckedAt == 0 || Stopwatch.GetElapsedTime(_fullscreenCheckedAt, now) >= RestInterval)
        {
            _fullscreenCheckedAt = now;
            _fullscreen = FullscreenDetector.IsFullscreenActive(FullscreenPolicy);
        }
        return _fullscreen;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();
        _owner = Window.GetWindow(_surface);
        if (_owner is null) return;
        _owner.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove), handledEventsToo: true);
        _owner.MouseLeave += OnMouseLeave;
        _owner.IsVisibleChanged += OnVisibilityChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    internal void Detach()
    {
        Reset();
        if (_owner is null) return;
        _owner.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove));
        _owner.MouseLeave -= OnMouseLeave;
        _owner.IsVisibleChanged -= OnVisibilityChanged;
        _owner = null;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Reset();
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => MoveTo(e.GetPosition(_surface));
    private void OnMouseLeave(object sender, MouseEventArgs e) => Leave();

    internal void MoveTo(Point point)
    {
        if (!_surface.CanShowPointerHighlight) { Reset(); return; }
        var panel = _surface.PointerHighlightPanel.Bounds;
        if (!panel.Contains(point)) { Leave(); return; }
        _target = point;
        _inside = true;
        if (!_hasPosition) { _position = point; _hasPosition = true; }
        StartFrames();
    }

    internal void Leave()
    {
        _inside = false;
        if (_strength > 0) StartFrames();
        else Reset();
    }

    private void StartFrames()
    {
        _timer.Interval = FrameInterval;
        if (_timer.IsEnabled) return;
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Clamp(Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds, .001, .05);
        _lastTick = now;
        AdvanceFrame(seconds);
    }

    internal void AdvanceFrame(double seconds)
    {
        if (!_surface.CanShowPointerHighlight || IsFullscreen()) { Reset(); return; }
        var (bounds, radius, shape) = _surface.PointerHighlightPanel;
        if (bounds.Width <= 0 || bounds.Height <= 0) { Reset(); return; }
        var geometryChanged = _outline is null || bounds != _bounds || radius != _radius || !ReferenceEquals(shape, _suppliedShape);
        if (geometryChanged)
        {
            _bounds = bounds; _radius = radius; _suppliedShape = shape;
            _outline = shape ?? ((GlassWindow)Window.GetWindow(_surface)).CreateGlassPanelGeometry(bounds, radius);
        }
        if (_inside && !_outline!.FillContains(_target)) _inside = false;
        var previous = _position;
        var previousStrength = _strength;
        var blend = 1 - Math.Exp(-seconds / .025);
        _position += (_target - _position) * blend;
        if ((_target - _position).Length < .05) _position = _target;
        _strength = _inside ? Math.Min(1, _strength + seconds / .09) : Math.Max(0, _strength - seconds / .22);
        if (_strength == 0) { Reset(); return; }
        if (geometryChanged || previous != _position || previousStrength != _strength || !_hasDrawing)
        {
            var extent = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * 1.8, 120, 240);
            _body.Center = _body.GradientOrigin = _position;
            _rim.Center = _rim.GradientOrigin = _position;
            _body.RadiusX = _body.RadiusY = extent;
            _rim.RadiusX = _rim.RadiusY = extent;
            _body.Opacity = _rim.Opacity = _strength;
            using var dc = _visual.RenderOpen();
            dc.PushClip(_outline);
            dc.DrawRectangle(_body, null, bounds);
            dc.DrawGeometry(null, _rimPen, _outline);
            dc.Pop();
            _hasDrawing = true;
            DrawCount++;
        }
        // Stationary light is retained without redraw. Only a cheap, shared fullscreen
        // admission check remains until leave/hide/sleep clears and stops the timer.
        _timer.Interval = _inside && _strength == 1 && _position == _target ? RestInterval : FrameInterval;
    }

    internal void Reset()
    {
        _timer.Stop();
        _lastTick = 0; _inside = false; _hasPosition = false; _strength = 0;
        if (_hasDrawing) { using var dc = _visual.RenderOpen(); _hasDrawing = false; }
    }
}
