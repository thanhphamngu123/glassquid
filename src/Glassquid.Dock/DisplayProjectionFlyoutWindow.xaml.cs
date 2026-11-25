using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>A centered Glassquid panel for the Windows display projection modes.</summary>
public partial class DisplayProjectionFlyoutWindow : GlassWindow
{
    private readonly SystemTrayBarWindow _dock;
    private bool _glassRevealPending;
    private bool _operationPending;
    private bool _isClosing;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal DisplayProjectionFlyoutWindow(SystemTrayBarWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;
        InitializeComponent();
        PanelCornerRadius = new CornerRadius(32);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
        // Populate the four rows before PrepareForShow measures the window. Otherwise the
        // manual SizeToContent measurement happens against an empty ItemsControl and clips
        // the lower projection modes after they are loaded asynchronously.
        ShowOptions(null);
        Loaded += OnPanelLoaded;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        CenterInWorkArea();
        GlassSurface.PrepareRevealForWindow(this);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(32, appearance.DockCornerRadius);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupSlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnPanelLoaded;
        base.OnClosed(e);
    }

    private void CenterInWorkArea()
    {
        if (Content is not UIElement content) return;
        var workArea = WorkAreaDip;
        content.Measure(new Size(workArea.Width, workArea.Height));
        var desired = content.DesiredSize;
        Width = Math.Ceiling(desired.Width);
        Height = Math.Ceiling(desired.Height);
        SizeToContent = SizeToContent.Manual;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private async void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPanelLoaded;
        try
        {
            var current = await Task.Run(DisplayProjectionController.GetCurrentMode);
            if (!_isClosing) ShowOptions(current);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Display projection] Could not read current display mode: {ex}");
            if (!_isClosing)
            {
                ShowOptions(null);
                ShowStatus("Couldn't read the current Windows display mode.");
            }
        }
    }

    private void ShowOptions(DisplayProjectionMode? currentMode)
    {
        ProjectionChoicesList.ItemsSource = new[]
        {
            new ProjectionChoice(DisplayProjectionMode.PcScreenOnly, "PC screen only", CreateIcon(ProjectionIcon.Pc), currentMode),
            new ProjectionChoice(DisplayProjectionMode.Duplicate, "Duplicate", CreateIcon(ProjectionIcon.Duplicate), currentMode),
            new ProjectionChoice(DisplayProjectionMode.Extend, "Extend", CreateIcon(ProjectionIcon.Extend), currentMode),
            new ProjectionChoice(DisplayProjectionMode.SecondScreenOnly, "Second screen only", CreateIcon(ProjectionIcon.Second), currentMode),
        };
    }

    private static FrameworkElement CreateIcon(ProjectionIcon kind)
    {
        return kind switch
        {
            ProjectionIcon.Pc => CreatePcScreenIcon(),
            ProjectionIcon.Duplicate => CreateDuplicateIcon(),
            ProjectionIcon.Extend => CreateExtendIcon(),
            _ => CreateSecondScreenIcon(),
        };
    }

    private static Grid CreatePcScreenIcon()
    {
        var grid = new Grid { Width = 38, Height = 26, SnapsToDevicePixels = true };

        var screen = new Border
        {
            Width = 26,
            Height = 17,
            CornerRadius = new CornerRadius(2.5),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };

        var innerGlow = new Border
        {
            Height = 2.5,
            Background = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 2, 2),
        };
        screen.Child = innerGlow;

        var stem = new System.Windows.Shapes.Rectangle
        {
            Width = 2.5,
            Height = 4,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 17, 0, 0),
        };

        var basePlate = new Border
        {
            Width = 11,
            Height = 1.8,
            CornerRadius = new CornerRadius(1),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 21, 0, 0),
        };

        grid.Children.Add(screen);
        grid.Children.Add(stem);
        grid.Children.Add(basePlate);
        return grid;
    }

    private static Canvas CreateDuplicateIcon()
    {
        var canvas = new Canvas { Width = 38, Height = 26, SnapsToDevicePixels = true };

        var rearScreen = new Border
        {
            Width = 20,
            Height = 13.5,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
        };
        rearScreen.Child = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1.5, 0, 1.5, 1.5),
        };
        Canvas.SetLeft(rearScreen, 15);
        Canvas.SetTop(rearScreen, 1);

        var rearBase = new Border
        {
            Width = 8,
            Height = 1.5,
            CornerRadius = new CornerRadius(0.8),
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(rearBase, 21);
        Canvas.SetTop(rearBase, 17);

        var frontScreen = new Border
        {
            Width = 20,
            Height = 13.5,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.6),
            BorderBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        };
        frontScreen.Child = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1.5, 0, 1.5, 1.5),
        };
        Canvas.SetLeft(frontScreen, 2);
        Canvas.SetTop(frontScreen, 7);

        var frontStem = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(frontStem, 11);
        Canvas.SetTop(frontStem, 20.5);

        var frontBase = new Border
        {
            Width = 9,
            Height = 1.6,
            CornerRadius = new CornerRadius(0.8),
            Background = Brushes.White,
        };
        Canvas.SetLeft(frontBase, 7.5);
        Canvas.SetTop(frontBase, 23.5);

        canvas.Children.Add(rearScreen);
        canvas.Children.Add(rearBase);
        canvas.Children.Add(frontScreen);
        canvas.Children.Add(frontStem);
        canvas.Children.Add(frontBase);
        return canvas;
    }

    private static Canvas CreateExtendIcon()
    {
        var canvas = new Canvas { Width = 38, Height = 26, SnapsToDevicePixels = true };

        var connectLine = new Border
        {
            Width = 34,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x39, 0xC5, 0xF6)),
        };
        Canvas.SetLeft(connectLine, 2);
        Canvas.SetTop(connectLine, 10);

        var screen1 = new Border
        {
            Width = 16,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(screen1, 2);
        Canvas.SetTop(screen1, 4);

        var stem1 = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(stem1, 9);
        Canvas.SetTop(stem1, 17);

        var base1 = new Border
        {
            Width = 7,
            Height = 1.5,
            CornerRadius = new CornerRadius(0.8),
            Background = Brushes.White,
        };
        Canvas.SetLeft(base1, 6.5);
        Canvas.SetTop(base1, 20);

        var screen2 = new Border
        {
            Width = 16,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(screen2, 20);
        Canvas.SetTop(screen2, 4);

        var stem2 = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(stem2, 27);
        Canvas.SetTop(stem2, 17);

        var base2 = new Border
        {
            Width = 7,
            Height = 1.5,
            CornerRadius = new CornerRadius(0.8),
            Background = Brushes.White,
        };
        Canvas.SetLeft(base2, 24.5);
        Canvas.SetTop(base2, 20);

        canvas.Children.Add(connectLine);
        canvas.Children.Add(screen1);
        canvas.Children.Add(stem1);
        canvas.Children.Add(base1);
        canvas.Children.Add(screen2);
        canvas.Children.Add(stem2);
        canvas.Children.Add(base2);
        return canvas;
    }

    private static Canvas CreateSecondScreenIcon()
    {
        var canvas = new Canvas { Width = 38, Height = 26, SnapsToDevicePixels = true };

        var screen1 = new Border
        {
            Width = 16,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(screen1, 2);
        Canvas.SetTop(screen1, 4);

        var stem1 = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(stem1, 9);
        Canvas.SetTop(stem1, 17);

        var base1 = new Border
        {
            Width = 7,
            Height = 1.4,
            CornerRadius = new CornerRadius(0.7),
            Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
        };
        Canvas.SetLeft(base1, 6.5);
        Canvas.SetTop(base1, 20);

        var cyan = Color.FromRgb(0x39, 0xC5, 0xF6);
        var screen2 = new Border
        {
            Width = 16,
            Height = 13,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.6),
            BorderBrush = new SolidColorBrush(cyan),
            Background = new SolidColorBrush(Color.FromArgb(0x45, 0x39, 0xC5, 0xF6)),
        };
        screen2.Child = new Border
        {
            Height = 2.5,
            Background = new SolidColorBrush(cyan),
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(1.5, 0, 1.5, 1.5),
        };
        Canvas.SetLeft(screen2, 20);
        Canvas.SetTop(screen2, 4);

        var stem2 = new System.Windows.Shapes.Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = new SolidColorBrush(cyan),
        };
        Canvas.SetLeft(stem2, 27);
        Canvas.SetTop(stem2, 17);

        var base2 = new Border
        {
            Width = 8,
            Height = 1.6,
            CornerRadius = new CornerRadius(0.8),
            Background = new SolidColorBrush(cyan),
        };
        Canvas.SetLeft(base2, 24);
        Canvas.SetTop(base2, 20);

        canvas.Children.Add(screen1);
        canvas.Children.Add(stem1);
        canvas.Children.Add(base1);
        canvas.Children.Add(screen2);
        canvas.Children.Add(stem2);
        canvas.Children.Add(base2);
        return canvas;
    }

    private async void OnProjectionChoiceClick(object sender, RoutedEventArgs e)
    {
        if (_operationPending || sender is not Button { Tag: ProjectionChoice choice }) return;
        if (choice.IsCurrent)
        {
            ClosePanel();
            return;
        }

        _operationPending = true;
        ProjectionChoicesList.IsEnabled = false;
        ShowStatus($"Switching to {choice.DisplayName}…");
        try
        {
            await Task.Run(() => DisplayProjectionController.Apply(choice.Mode));
            ClosePanel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Display projection] Could not apply {choice.Mode}: {ex}");
            ShowStatus($"Windows couldn't apply this display mode: {ex.Message}");
            ProjectionChoicesList.IsEnabled = true;
            _operationPending = false;
        }
    }

    private void OnMoreDisplaySettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
            ClosePanel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Display projection] Could not open Display settings: {ex}");
            ShowStatus("Couldn't open Display settings.");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ClosePanel();
        _dock.OpenQuickSettingsFlyout();
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClosePanel();
        e.Handled = true;
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_operationPending || _isClosing) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_operationPending && !_isClosing && !IsActive) ClosePanel();
        }));
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClosePanel()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private enum ProjectionIcon
    {
        Pc,
        Duplicate,
        Extend,
        Second,
    }

    private sealed record ProjectionChoice(
        DisplayProjectionMode Mode,
        string DisplayName,
        FrameworkElement IconElement,
        DisplayProjectionMode? CurrentMode)
    {
        public bool IsCurrent => CurrentMode == Mode;
    }
}
