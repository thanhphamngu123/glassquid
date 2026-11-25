using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.Core.Contracts;
using Glassquid.Platform;
using Glassquid.Platform.Interop;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock;

/// <summary>
/// Alt+Tab window switcher styled with Glassquid's liquid glass dock theme.
/// Displays live DWM preview thumbnails of running windows, active window highlight,
/// application icons, and individual close buttons.
/// </summary>
public sealed class AltTabWindow : Window
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);
    private delegate bool EnumWindowsCallback(nint window, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativePreviewRect lpRect);

    private static string GetWindowTitle(nint hwnd)
    {
        var buffer = new char[256];
        var len = GetWindowText(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len).Trim() : string.Empty;
    }

    private static string GetWindowClass(nint hwnd)
    {
        var buffer = new char[128];
        var len = GetClassName(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    private sealed class ThumbnailEntry
    {
        public required nint Handle { get; init; }
        public required string Title { get; init; }
        public required Border Card { get; init; }
        public required Border Host { get; init; }
        public nint ThumbId { get; set; }
    }

    private readonly List<ThumbnailEntry> _entries = new();
    private readonly Func<nint, BitmapSource?> _iconResolver;
    private readonly Action? _onDismissed;
    private readonly WrapPanel _cardsWrapPanel;
    private readonly int _columns;
    private nint _hwnd;
    private int _selectedIndex;
    private bool _isDisposed;
    private bool _isCommitting;

    // Card styling brushes
    private static readonly SolidColorBrush NormalCardBg = new(Color.FromArgb(28, 255, 255, 255));
    private static readonly SolidColorBrush NormalCardBorder = new(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush HoverCardBg = new(Color.FromArgb(48, 255, 255, 255));
    private static readonly SolidColorBrush HoverCardBorder = new(Color.FromArgb(90, 255, 255, 255));
    private static readonly SolidColorBrush SelectedCardBg = new(Color.FromArgb(42, 10, 132, 255));
    private static readonly SolidColorBrush SelectedCardBorder = new(Color.FromRgb(10, 132, 255)); // Dock accent blue
    private static readonly DropShadowEffect SelectedCardShadow = new()
    {
        Color = Color.FromRgb(10, 132, 255),
        BlurRadius = 16,
        ShadowDepth = 0,
        Opacity = 0.65,
    };

    public int SelectedIndex => _selectedIndex;
    public int WindowCount => _entries.Count;

    public AltTabWindow(
        IReadOnlyList<WindowCandidate> candidates,
        Func<nint, BitmapSource?> iconResolver,
        int initialIndex = 0,
        Action? onDismissed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _iconResolver = iconResolver ?? (_ => null);
        _onDismissed = onDismissed;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var count = candidates.Count;
        if (count <= 4) _columns = Math.Max(1, count);
        else if (count <= 8) _columns = 4;
        else if (count <= 12) _columns = 4;
        else _columns = 5;

        const double cardWidth = 236;
        const double cardMargin = 6;
        var totalCardWidth = cardWidth + (cardMargin * 2);

        // Root container with dock liquid glass styling
        var rootBorder = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(205, 18, 18, 22)),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(16),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(25, 255, 255, 255), 1.0),
                }
            },
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 40,
                ShadowDepth = 10,
                Opacity = 0.55,
            }
        };

        _cardsWrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Width = _columns * totalCardWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var workArea = SystemParameters.WorkArea;
        var maxViewerHeight = Math.Max(300, workArea.Height * 0.85);

        var scrollViewer = new ScrollViewer
        {
            Content = _cardsWrapPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = maxViewerHeight,
            Focusable = false,
        };

        rootBorder.Child = scrollViewer;
        Content = rootBorder;

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();

        // Win32 ToolWindow style (excludes from Alt+Tab itself)
        var exStyle = GetWindowLong(_hwnd, GwlExStyle);
        _ = SetWindowLong(_hwnd, GwlExStyle, exStyle | WsExToolWindow);

        // Windows 11 Rounded Corners (2 = DWMWCP_ROUND)
        const int DwmwaWindowCornerPreference = 33;
        var round = 2;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        // Windows 11 Acrylic Backdrop
        const int DwmwaSystemBackdropType = 38;
        var backdrop = 4; // Acrylic
        _ = DwmSetWindowAttribute(_hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // Dark Mode
        const int DwmwaUseImmersiveDarkMode = 20;
        var dark = 1;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // Build cards
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var entry = CreateCard(candidate, i);
            _entries.Add(entry);
            _cardsWrapPanel.Children.Add(entry.Card);
        }

        _selectedIndex = Math.Clamp(initialIndex, 0, Math.Max(0, _entries.Count - 1));
        ApplySelectionStyles();

        Loaded += OnLoaded;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            if (!_isCommitting && !_isDisposed)
            {
                Close();
            }
        };
    }

    private ThumbnailEntry CreateCard(WindowCandidate candidate, int index)
    {
        var targetHandle = candidate.Handle;
        var title = string.IsNullOrWhiteSpace(candidate.Title)
            ? (WindowControl.GetTitle(targetHandle) is { Length: > 0 } t ? t : "Cửa sổ")
            : candidate.Title;

        var card = new Border
        {
            Width = 236,
            Height = 168,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(14),
            Background = NormalCardBg,
            BorderBrush = NormalCardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Cursor = Cursors.Hand,
            Tag = index,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header (Icon, Title, Close Button)
        var header = new Grid { Margin = new Thickness(4, 2, 4, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconImage = new Image
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        var icon = _iconResolver(targetHandle);
        if (icon != null) iconImage.Source = icon;
        Grid.SetColumn(iconImage, 0);
        header.Children.Add(iconImage);

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
        header.Children.Add(titleBlock);

        // Circular Close Button (pill styled like Win11 / Dock)
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

        closeBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseAndRemoveWindow(targetHandle, card);
        };

        Grid.SetColumn(closeBtn, 2);
        header.Children.Add(closeBtn);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Thumbnail host
        var thumbHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        Grid.SetRow(thumbHost, 1);
        grid.Children.Add(thumbHost);

        card.Child = grid;

        // Card mouse interactions
        card.MouseEnter += (_, _) =>
        {
            var idx = _entries.FindIndex(x => x.Card == card);
            if (idx >= 0 && idx != _selectedIndex)
            {
                Select(idx);
            }
        };

        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                var idx = _entries.FindIndex(x => x.Card == card);
                if (idx >= 0)
                {
                    _selectedIndex = idx;
                    CommitAndClose();
                }
            }
        };

        return new ThumbnailEntry
        {
            Handle = targetHandle,
            Title = title,
            Card = card,
            Host = thumbHost,
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterOnPrimaryScreen();
        RegisterAllThumbnails();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateAllThumbnailPositions);
    }

    private void CenterOnPrimaryScreen()
    {
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = DesiredSize.Width > 0 ? DesiredSize.Width : 800.0;
        var h = DesiredSize.Height > 0 ? DesiredSize.Height : 500.0;

        var workArea = SystemParameters.WorkArea;
        Left = Math.Round(workArea.Left + (workArea.Width - w) / 2.0);
        Top = Math.Round(workArea.Top + (workArea.Height - h) / 2.0);
    }

    private void RegisterAllThumbnails()
    {
        if (_isDisposed || _hwnd == 0) return;

        foreach (var entry in _entries)
        {
            if (entry.ThumbId != 0) continue;

            if (DwmRegisterThumbnail(_hwnd, entry.Handle, out var thumb) == 0 && thumb != 0)
            {
                entry.ThumbId = thumb;
            }
        }
    }

    public void UpdateAllThumbnailPositions()
    {
        if (_isDisposed || !IsLoaded || _hwnd == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        foreach (var entry in _entries)
        {
            if (entry.ThumbId == 0 || !entry.Host.IsVisible) continue;

            try
            {
                var pt = entry.Host.TranslatePoint(new Point(0, 0), this);
                var boxX = (int)Math.Round(pt.X * dpiX);
                var boxY = (int)Math.Round(pt.Y * dpiY);
                var boxW = (int)Math.Round(entry.Host.ActualWidth * dpiX);
                var boxH = (int)Math.Round(entry.Host.ActualHeight * dpiY);

                if (boxW <= 0 || boxH <= 0) continue;

                var fitX = boxX;
                var fitY = boxY;
                var fitW = boxW;
                var fitH = boxH;

                if (DwmQueryThumbnailSourceSize(entry.ThumbId, out var srcSize) == 0 && srcSize.cx > 0 && srcSize.cy > 0)
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

                _ = DwmUpdateThumbnailProperties(entry.ThumbId, ref props);
            }
            catch
            {
                // Ignore transient layout calculation exceptions
            }
        }
    }

    public void Select(int index)
    {
        if (_entries.Count == 0) return;
        _selectedIndex = (index % _entries.Count + _entries.Count) % _entries.Count;
        ApplySelectionStyles();
        _entries[_selectedIndex].Card.BringIntoView();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateAllThumbnailPositions);
    }

    public void SelectNext() => Select(_selectedIndex + 1);

    public void SelectPrevious() => Select(_selectedIndex - 1);

    public void NavigateDirection(uint vkCode)
    {
        switch (vkCode)
        {
            case 0x25: // Left
                SelectPrevious();
                break;
            case 0x27: // Right
                SelectNext();
                break;
            case 0x26: // Up
                Select(_selectedIndex - _columns);
                break;
            case 0x28: // Down
                Select(_selectedIndex + _columns);
                break;
        }
    }

    private void ApplySelectionStyles()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var card = _entries[i].Card;
            if (i == _selectedIndex)
            {
                card.Background = SelectedCardBg;
                card.BorderBrush = SelectedCardBorder;
                card.BorderThickness = new Thickness(2);
                card.Effect = SelectedCardShadow;
            }
            else
            {
                card.Background = NormalCardBg;
                card.BorderBrush = NormalCardBorder;
                card.BorderThickness = new Thickness(1);
                card.Effect = null;
            }
        }
    }

    public void CommitAndClose()
    {
        if (_isCommitting || _isDisposed) return;
        _isCommitting = true;

        nint target = 0;
        if (_selectedIndex >= 0 && _selectedIndex < _entries.Count)
        {
            target = _entries[_selectedIndex].Handle;
        }

        Close();

        if (target != 0)
        {
            _ = WindowLocator.TryBringToForeground(target);
        }
    }

    private void CloseAndRemoveWindow(nint handle, Border card)
    {
        _ = WindowControl.Close(handle);

        var entry = _entries.Find(x => x.Handle == handle);
        if (entry != null)
        {
            if (entry.ThumbId != 0)
            {
                _ = DwmUnregisterThumbnail(entry.ThumbId);
            }
            _entries.Remove(entry);
        }

        _cardsWrapPanel.Children.Remove(card);

        if (_entries.Count == 0)
        {
            Close();
            return;
        }

        if (_selectedIndex >= _entries.Count)
        {
            _selectedIndex = _entries.Count - 1;
        }

        ApplySelectionStyles();
        CenterOnPrimaryScreen();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateAllThumbnailPositions);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var entry in _entries)
        {
            if (entry.ThumbId != 0)
            {
                _ = DwmUnregisterThumbnail(entry.ThumbId);
                entry.ThumbId = 0;
            }
        }

        _entries.Clear();
        _onDismissed?.Invoke();
    }

    /// <summary>
    /// Gathers all candidate switchable top-level windows in Z-order.
    /// Excludes tool windows, cloaked windows, shell windows, and Glassquid's own windows.
    /// </summary>
    public static List<WindowCandidate> GetAltTabWindows(uint currentProcessId)
    {
        var list = new List<WindowCandidate>();

        _ = EnumWindows((hwnd, lParam) =>
        {
            if (hwnd == 0 || !IsWindow(hwnd)) return true;
            if (!IsWindowVisible(hwnd)) return true;

            if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0 || pid == currentProcessId)
                return true;

            const int GwlExStyle = -20;
            const int WsExToolWindow = 0x00000080;
            const int WsExAppWindow = 0x00040000;
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            var isAppWindow = (exStyle & WsExAppWindow) != 0;
            var isToolWindow = (exStyle & WsExToolWindow) != 0;

            var owner = GetWindow(hwnd, 4); // GW_OWNER
            if (owner != 0 && !isAppWindow) return true;
            if (isToolWindow && !isAppWindow) return true;

            // Exclude cloaked windows (virtual desktops, suspended UWP apps, etc.)
            const int DwmwaCloaked = 14;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrEmpty(title)) return true;

            // Exclude shell windows
            var className = GetWindowClass(hwnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow")
            {
                return true;
            }

            // Exclude empty bounds for non-minimized windows
            if (!IsIconic(hwnd))
            {
                if (GetWindowRect(hwnd, out var rc))
                {
                    if (rc.Right - rc.Left <= 0 || rc.Bottom - rc.Top <= 0)
                        return true;
                }
            }

            list.Add(new WindowCandidate
            {
                Handle = hwnd,
                Title = title,
            });

            return true;
        }, 0);

        return list;
    }
}
