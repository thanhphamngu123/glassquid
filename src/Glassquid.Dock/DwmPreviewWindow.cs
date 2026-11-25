using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock;

public sealed class DwmPreviewWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePreviewRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePreviewSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint dwFlags;
        public NativePreviewRect rcDestination;
        public NativePreviewRect rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    private const uint DwmTnpRectDestination = 0x00000001;
    private const uint DwmTnpOpacity = 0x00000004;
    private const uint DwmTnpVisible = 0x00000008;

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DwmThumbnailProperties props);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(nint thumb, out NativePreviewSize size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private sealed class ThumbnailItem
    {
        public required nint Handle { get; init; }
        public required nint ThumbId { get; set; }
        public required Border Host { get; init; }
        public required Border Card { get; init; }
    }

    private readonly List<ThumbnailItem> _items = [];
    private readonly StackPanel _cardsPanel;
    private readonly Action<nint> _onActivate;
    private readonly Action<nint> _onClose;
    private readonly nint _hwnd;
    private bool _isDisposed;
    private Rect _tileDipBounds;

    public event EventHandler? PreviewMouseEnter;
    public event EventHandler? PreviewMouseLeave;

    public DwmPreviewWindow(
        IReadOnlyList<WindowCandidate> candidates,
        Rect tileScreenBounds,
        Action<nint> onActivate,
        Action<nint> onClose,
        Func<nint, BitmapSource?> iconResolver)
    {
        _onActivate = onActivate;
        _onClose = onClose;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20));
        SizeToContent = SizeToContent.WidthAndHeight;

        // Liquid Glass border brush (specular highlight matching the dock)
        var glassBorderBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        glassBorderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(135, 255, 255, 255), 0.0));
        glassBorderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(35, 255, 255, 255), 0.35));
        glassBorderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), 1.0));
        glassBorderBrush.Freeze();

        // Dark frosted glass background (matching dock's dark liquid glass capsule)
        var glassBgBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        glassBgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 26, 26, 30), 0.0));
        glassBgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(250, 14, 14, 17), 1.0));
        glassBgBrush.Freeze();

        var rootBorder = new Border
        {
            Background = glassBgBrush,
            BorderBrush = glassBorderBrush,
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(22),
        };

        _cardsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };

        var scrollViewer = new ScrollViewer
        {
            Content = _cardsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        scrollViewer.PreviewMouseWheel += (_, e) =>
        {
            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

        rootBorder.Child = scrollViewer;
        Content = rootBorder;

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();

        // Configure Win32 styles
        var exStyle = GetWindowLong(_hwnd, GwlExStyle);
        _ = SetWindowLong(_hwnd, GwlExStyle, exStyle | WsExToolWindow | WsExNoActivate);

        // Windows 11 Rounded Corners (2 = DWMWCP_ROUND)
        const int DwmwaWindowCornerPreference = 33;
        var round = 2;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        // Windows 11 Acrylic Backdrop
        const int DwmwaSystemBackdropType = 38;
        var backdrop = 4;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // Dark mode
        const int DwmwaUseImmersiveDarkMode = 20;
        var dark = 1;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // Subtle glass border
        const int DwmwaBorderColor = 34;
        var border = 0x00454548;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref border, sizeof(int));

        // Build UI cards for all candidate windows
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var card = CreateCard(candidate, iconResolver);
            _cardsPanel.Children.Add(card);
        }

        _tileDipBounds = tileScreenBounds;

        // Measure and position above the hovered dock tile
        PositionAboveTile(_tileDipBounds);

        MouseEnter += (_, _) => PreviewMouseEnter?.Invoke(this, EventArgs.Empty);
        MouseLeave += (_, _) => PreviewMouseLeave?.Invoke(this, EventArgs.Empty);

        Loaded += OnLoaded;
    }

    private Border CreateCard(WindowCandidate candidate, Func<nint, BitmapSource?> iconResolver)
    {
        var targetHandle = candidate.Handle;
        var title = string.IsNullOrWhiteSpace(candidate.Title)
            ? (WindowControl.GetTitle(targetHandle) is { Length: > 0 } t ? t : "Cửa sổ")
            : candidate.Title;

        var cardBorderBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        cardBorderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(50, 255, 255, 255), 0.0));
        cardBorderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(20, 255, 255, 255), 1.0));
        cardBorderBrush.Freeze();

        var normalCardBg = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
        var hoverCardBg = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
        var hoverCardBorder = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

        var card = new Border
        {
            Width = 240,
            Height = 170,
            Margin = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(14),
            Background = normalCardBg,
            BorderBrush = cardBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7),
            Cursor = Cursors.Hand,
            Tag = targetHandle,
        };

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header
        var headerGrid = new Grid { Margin = new Thickness(4, 2, 4, 7) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconImage = new Image
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        var icon = iconResolver(targetHandle);
        if (icon != null)
        {
            iconImage.Source = icon;
        }
        Grid.SetColumn(iconImage, 0);
        headerGrid.Children.Add(iconImage);

        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI, system-ui"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = title,
        };
        Grid.SetColumn(titleBlock, 1);
        headerGrid.Children.Add(titleBlock);

        // Circular Close Button (styled like glass pill)
        var closeBtn = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Đóng cửa sổ",
            Cursor = Cursors.Hand,
        };
        var closeText = new TextBlock
        {
            Text = "✕",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.Child = closeText;

        closeBtn.MouseEnter += (_, _) =>
        {
            closeBtn.Background = new SolidColorBrush(Color.FromRgb(232, 17, 35));
            closeText.Foreground = Brushes.White;
        };
        closeBtn.MouseLeave += (_, _) =>
        {
            closeBtn.Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
            closeText.Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
        };

        var capturedCard = card;
        closeBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RemoveAndCloseWindow(targetHandle, capturedCard);
        };

        Grid.SetColumn(closeBtn, 2);
        headerGrid.Children.Add(closeBtn);

        Grid.SetRow(headerGrid, 0);
        cardGrid.Children.Add(headerGrid);

        // Thumbnail Host
        var thumbHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 12)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 0),
        };

        Grid.SetRow(thumbHost, 1);
        cardGrid.Children.Add(thumbHost);

        card.Child = cardGrid;

        card.MouseEnter += (_, _) =>
        {
            card.Background = hoverCardBg;
            card.BorderBrush = hoverCardBorder;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = normalCardBg;
            card.BorderBrush = cardBorderBrush;
        };

        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                _onActivate(targetHandle);
            }
        };

        _items.Add(new ThumbnailItem
        {
            Handle = targetHandle,
            ThumbId = 0,
            Host = thumbHost,
            Card = card,
        });

        return card;
    }

    public void PositionAboveTile(Rect tileDipBounds)
    {
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredW = DesiredSize.Width > 0 ? DesiredSize.Width : 250.0;
        var desiredH = DesiredSize.Height > 0 ? DesiredSize.Height : 180.0;

        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        Rect workArea = SystemParameters.WorkArea;

        double leftDip;
        double topDip;

        if (!tileDipBounds.IsEmpty && tileDipBounds.Width > 0)
        {
            var pScreen = new System.Drawing.Point((int)Math.Round(tileDipBounds.Left * dpiX), (int)Math.Round(tileDipBounds.Top * dpiY));
            var screen = System.Windows.Forms.Screen.FromPoint(pScreen);
            if (screen != null)
            {
                workArea = new Rect(screen.WorkingArea.X / dpiX, screen.WorkingArea.Y / dpiY, screen.WorkingArea.Width / dpiX, screen.WorkingArea.Height / dpiY);
            }

            leftDip = tileDipBounds.Left + (tileDipBounds.Width / 2.0) - (desiredW / 2.0);
            topDip = tileDipBounds.Top - desiredH - 12.0;

            if (topDip < workArea.Top + 8)
            {
                topDip = tileDipBounds.Bottom + 12.0;
            }
        }
        else
        {
            var p = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(p);
            if (screen != null)
            {
                workArea = new Rect(screen.WorkingArea.X / dpiX, screen.WorkingArea.Y / dpiY, screen.WorkingArea.Width / dpiX, screen.WorkingArea.Height / dpiY);
            }

            var mouseXDip = p.X / dpiX;
            var mouseYDip = p.Y / dpiY;
            leftDip = mouseXDip - (desiredW / 2.0);
            topDip = mouseYDip - desiredH - 20.0;
        }

        leftDip = Math.Clamp(leftDip, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - desiredW - 8));
        topDip = Math.Clamp(topDip, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - desiredH - 8));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = leftDip;
        Top = topDip;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAboveTile(_tileDipBounds);
        RegisterAllThumbnails();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateAllThumbnailPositions);
    }

    private void RegisterAllThumbnails()
    {
        if (_isDisposed || _hwnd == 0) return;

        foreach (var item in _items)
        {
            if (item.ThumbId != 0) continue;

            if (DwmRegisterThumbnail(_hwnd, item.Handle, out var thumb) == 0 && thumb != 0)
            {
                item.ThumbId = thumb;
            }
        }
    }

    public void UpdateAllThumbnailPositions()
    {
        if (_isDisposed || !IsLoaded || _hwnd == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        foreach (var item in _items)
        {
            if (item.ThumbId == 0 || !item.Host.IsVisible) continue;

            try
            {
                var pt = item.Host.TranslatePoint(new Point(0, 0), this);
                var boxX = (int)Math.Round(pt.X * dpiX);
                var boxY = (int)Math.Round(pt.Y * dpiY);
                var boxW = (int)Math.Round(item.Host.ActualWidth * dpiX);
                var boxH = (int)Math.Round(item.Host.ActualHeight * dpiY);

                if (boxW <= 0 || boxH <= 0) continue;

                var fitX = boxX;
                var fitY = boxY;
                var fitW = boxW;
                var fitH = boxH;

                if (DwmQueryThumbnailSourceSize(item.ThumbId, out var srcSize) == 0 && srcSize.cx > 0 && srcSize.cy > 0)
                {
                    var scale = Math.Min((double)boxW / srcSize.cx, (double)boxH / srcSize.cy);
                    fitW = Math.Max(1, (int)Math.Round(srcSize.cx * scale));
                    fitH = Math.Max(1, (int)Math.Round(srcSize.cy * scale));
                    fitX = boxX + (boxW - fitW) / 2;
                    fitY = boxY + (boxH - fitH) / 2;
                }

                var props = new DwmThumbnailProperties
                {
                    dwFlags = DwmTnpRectDestination | DwmTnpVisible | DwmTnpOpacity,
                    rcDestination = new NativePreviewRect
                    {
                        Left = fitX,
                        Top = fitY,
                        Right = fitX + fitW,
                        Bottom = fitY + fitH,
                    },
                    opacity = 255,
                    fVisible = true,
                };

                _ = DwmUpdateThumbnailProperties(item.ThumbId, ref props);
            }
            catch
            {
                // Ignore transient layout exceptions
            }
        }
    }

    private void RemoveAndCloseWindow(nint handle, Border card)
    {
        _onClose(handle);

        var item = _items.Find(x => x.Handle == handle);
        if (item != null)
        {
            if (item.ThumbId != 0)
            {
                _ = DwmUnregisterThumbnail(item.ThumbId);
            }
            _items.Remove(item);
        }

        _cardsPanel.Children.Remove(card);

        if (_items.Count == 0)
        {
            Close();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateAllThumbnailPositions);
    }

    public bool IsMouseOverPreview()
    {
        if (!IsLoaded || !IsVisible) return false;
        try
        {
            var mouse = Mouse.GetPosition(this);
            return mouse.X >= 0 && mouse.X <= ActualWidth && mouse.Y >= 0 && mouse.Y <= ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _isDisposed = true;

        foreach (var item in _items)
        {
            if (item.ThumbId != 0)
            {
                _ = DwmUnregisterThumbnail(item.ThumbId);
                item.ThumbId = 0;
            }
        }
        _items.Clear();
    }
}
