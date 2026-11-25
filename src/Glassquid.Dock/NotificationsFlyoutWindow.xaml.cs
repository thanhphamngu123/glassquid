using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>Windows 11-style Glass Notification and Calendar Center.</summary>
public partial class NotificationsFlyoutWindow : GlassWindow, IDisposable
{
    private readonly SystemTrayBarWindow _dock;
    private bool _isClosing;
    private bool _disposed;
    private bool _glassRevealPending;
    private bool _isDndEnabled;
    private bool _isCalendarExpanded = true;
    private DateTime _displayMonth = DateTime.Today;
    private int _focusMinutes = 30;
    private bool _isFocusRunning;
    private int _focusSecondsRemaining;
    private readonly DispatcherTimer _focusTimer;
    private readonly List<NotificationItem> _notifications = new();

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal NotificationsFlyoutWindow(SystemTrayBarWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;

        InitializeComponent();
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;

        _focusTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _focusTimer.Tick += OnFocusTimerTick;

        // Initialize Calendar & Date display
        UpdateCalendarHeaderDate();
        RenderCalendar(_displayMonth);

        // Synchronize Do Not Disturb with Windows 11
        _isDndEnabled = WindowsDoNotDisturbController.IsActive();
        ApplyDndVisualState(_isDndEnabled);
        WindowsDoNotDisturbController.StateChanged += OnDoNotDisturbStateChanged;

        WindowsNotificationManager.Instance.NotificationsChanged += OnNotificationsChanged;
    }

    private bool _forceRebuildOnShow;

    public void PrepareForShow()
    {
        _isDndEnabled = WindowsDoNotDisturbController.IsActive();
        ApplyDndVisualState(_isDndEnabled);
        _forceRebuildOnShow = true;
        LoadNotificationsFromCache();
        _forceRebuildOnShow = false;
        _ = WindowsNotificationManager.Instance.RefreshAsync();
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndPosition();
        GlassSurface.PrepareRevealForWindow(this);
        InstallMouseHook();
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

        var anchor = _dock.GetNotificationsAnchorPoint();
        Left = Math.Clamp(anchor.X - Width / 2, workArea.Left + 8,
            Math.Max(workArea.Left + 8, workArea.Right - Width - 10));
        Top = _dock.IsTopPlacement
            ? Math.Clamp(anchor.Y + 14, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - Height - 14))
            : Math.Clamp(anchor.Y - Height - 14, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - Height - 14));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fromY = _dock.IsTopPlacement ? -16 : 16;
        PopupSlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    #region Notifications Management

    private void LoadNotificationsFromCache()
    {
        var items = WindowsNotificationManager.Instance.GetCachedNotifications();
        _notifications.Clear();
        _notifications.AddRange(items);
        RebuildNotificationCards();
    }

    private void OnNotificationsChanged(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        Dispatcher.InvokeAsync(LoadNotificationsFromCache);
    }

    private void RebuildNotificationCards()
    {
        if (!IsVisible && !_forceRebuildOnShow) return;
        NotificationCardsContainer.Children.Clear();
        int count = _notifications.Count;

        NotificationCountText.Text = count.ToString(CultureInfo.InvariantCulture);
        NotificationCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearAllButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateBorder.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _notifications)
        {
            var card = CreateNotificationCard(item);
            NotificationCardsContainer.Children.Add(card);
        }
    }

    private Border CreateNotificationCard(NotificationItem item)
    {
        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(0, 3, 0, 3),
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            ToolTip = $"Mở {item.AppName}",
        };

        // Hover brightness feedback
        cardBorder.MouseEnter += (_, _) =>
        {
            cardBorder.Background = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255));
            cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
        };
        cardBorder.MouseLeave += (_, _) =>
        {
            cardBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
            cardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255));
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header Row: App icon, App name, Time ago, and Dismiss Button
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: Icon
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1: App Name
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2: Time Ago (right)
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3: Dismiss Button (far right)
        Grid.SetRow(headerGrid, 0);

        // App Icon Bubble
        var iconBubble = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = item.AppIcon != null
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(0x35, item.AppColor.R, item.AppColor.G, item.AppColor.B)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        if (item.AppIcon != null)
        {
            var img = new Image
            {
                Source = item.AppIcon,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconBubble.Child = img;
        }
        else
        {
            var iconText = new TextBlock
            {
                Text = item.AppGlyph,
                FontFamily = (FontFamily)FindResource("PanelIconFont"),
                FontSize = 13,
                Foreground = new SolidColorBrush(item.AppColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconBubble.Child = iconText;
        }
        Grid.SetColumn(iconBubble, 0);
        headerGrid.Children.Add(iconBubble);

        // App Name
        var appNameText = new TextBlock
        {
            Text = item.AppName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = ForegroundBrush ?? Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(appNameText, 1);
        headerGrid.Children.Add(appNameText);

        // Time text (aligned to the right)
        var timeText = new TextBlock
        {
            Text = item.TimeAgo,
            FontSize = 10,
            Opacity = 0.65,
            Foreground = SecondaryForegroundBrush ?? Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(timeText, 2);
        headerGrid.Children.Add(timeText);

        // Dismiss (X) Button (far right)
        var dismissButton = new Button
        {
            Style = (Style)FindResource("FlyoutButton"),
            Width = 20,
            Height = 20,
            ToolTip = "Xóa thông báo này",
            Tag = item.Id,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dismissIcon = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Opacity = 0.65,
            Foreground = SecondaryForegroundBrush ?? Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dismissButton.Content = dismissIcon;
        dismissButton.Click += (s, e) =>
        {
            e.Handled = true;
            if (s is Button btn && btn.Tag is uint id)
                DismissNotification(id);
        };
        Grid.SetColumn(dismissButton, 3);
        headerGrid.Children.Add(dismissButton);

        grid.Children.Add(headerGrid);

        // Title Row
        var titleText = new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ForegroundBrush ?? Brushes.White,
            Margin = new Thickness(0, 4, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(titleText, 1);
        grid.Children.Add(titleText);

        // Body Row
        var bodyText = new TextBlock
        {
            Text = item.Body,
            FontSize = 12,
            Opacity = 0.88,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryForegroundBrush ?? Brushes.LightGray,
            LineHeight = 16,
        };
        Grid.SetRow(bodyText, 2);
        grid.Children.Add(bodyText);

        cardBorder.Child = grid;

        // Clicking the notification card opens the corresponding application
        cardBorder.MouseLeftButtonUp += (s, e) =>
        {
            if (e.Handled) return;
            if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) == dismissButton)
            {
                return;
            }

            e.Handled = true;
            OnNotificationCardClicked(item);
        };

        return cardBorder;
    }

    private async void OnNotificationCardClicked(NotificationItem item)
    {
        var opened = false;
        var isScreenshot = WindowsNotificationManager.IsScreenshotNotification(item);
        try
        {
            opened = isScreenshot
                ? await Task.Run(() => WindowsNotificationManager.OpenNotificationApp(item))
                : WindowsNotificationManager.OpenNotificationApp(item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Notifications] OpenNotificationApp failed: {ex.Message}");
        }

        // Keep a screenshot toast in the flyout if its saved image was not found,
        // so the user can retry after Windows finishes writing the file.
        if (!opened && isScreenshot) return;

        DismissNotification(item.Id);
        CloseWithFade();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static void DismissNotification(uint id)
    {
        WindowsNotificationManager.Instance.DismissNotification(id);
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _notifications.Clear();
        WindowsNotificationManager.Instance.ClearAll();
        RebuildNotificationCards();
    }

    private void OnSendTestNotificationClick(object sender, RoutedEventArgs e)
    {
        WindowsNotificationManager.Instance.AddTestNotification();
        LoadNotificationsFromCache();
    }

    private void OnDoNotDisturbStateChanged(object? sender, bool active)
    {
        Dispatcher.InvokeAsync(() => ApplyDndVisualState(active));
    }

    private void OnDndToggleClick(object sender, RoutedEventArgs e)
    {
        _isDndEnabled = WindowsDoNotDisturbController.Toggle();
        ApplyDndVisualState(_isDndEnabled);
        _dock.RefreshNotificationsStatus();
    }

    private void ApplyDndVisualState(bool enabled)
    {
        _isDndEnabled = enabled;
        if (enabled)
        {
            DndButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x35, 255, 159, 10)); // Amber glow
            DndButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x75, 255, 159, 10));
            DndIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 64));
            DndIcon.Text = "\uED39"; // Moon / silent bell glyph
            DndText.Text = "Không làm phiền";
            DndText.FontWeight = FontWeights.SemiBold;
            DndText.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 64));
            DndToggleButton.ToolTip = "Không làm phiền: Đang bật (Nhấn để tắt)";
        }
        else
        {
            DndButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
            DndButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
            DndIcon.Foreground = Brushes.White;
            DndIcon.Text = "\uED39";
            DndText.Text = "Không làm phiền";
            DndText.FontWeight = FontWeights.Medium;
            DndText.Foreground = ForegroundBrush ?? Brushes.White;
            DndToggleButton.ToolTip = "Không làm phiền: Đang tắt (Nhấn để bật)";
        }
    }

    #endregion

    #region Calendar Management

    private void UpdateCalendarHeaderDate()
    {
        var now = DateTime.Now;
        var culture = new CultureInfo("vi-VN");
        CalendarFullDateText.Text = now.ToString("dddd, dd 'tháng' MM, yyyy", culture);
    }

    private void RenderCalendar(DateTime month)
    {
        var culture = new CultureInfo("vi-VN");
        CalendarMonthYearText.Text = month.ToString("'Tháng' MM, yyyy", culture);
        CalendarDaysGrid.Children.Clear();

        var firstDayOfMonth = new DateTime(month.Year, month.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);

        // Convert Monday=0 to Sunday=6 (Vietnamese week starting Monday)
        int dayOfWeekOffset = ((int)firstDayOfMonth.DayOfWeek + 6) % 7;

        var prevMonth = month.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);

        // 1. Trailing days from previous month
        for (int i = dayOfWeekOffset - 1; i >= 0; i--)
        {
            int day = daysInPrevMonth - i;
            var cell = CreateDayCell(day, isCurrentMonth: false, isToday: false);
            CalendarDaysGrid.Children.Add(cell);
        }

        // 2. Days of current month
        var today = DateTime.Today;
        for (int day = 1; day <= daysInMonth; day++)
        {
            bool isToday = (month.Year == today.Year && month.Month == today.Month && day == today.Day);
            var cell = CreateDayCell(day, isCurrentMonth: true, isToday: isToday);
            CalendarDaysGrid.Children.Add(cell);
        }

        // 3. Leading days of next month (fill up to 42 cells)
        int totalCells = dayOfWeekOffset + daysInMonth;
        int nextDays = 42 - totalCells;
        for (int day = 1; day <= nextDays; day++)
        {
            var cell = CreateDayCell(day, isCurrentMonth: false, isToday: false);
            CalendarDaysGrid.Children.Add(cell);
        }
    }

    private Button CreateDayCell(int day, bool isCurrentMonth, bool isToday)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("CalendarDayButtonStyle"),
        };

        var border = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isToday)
        {
            border.Background = new SolidColorBrush(Color.FromRgb(10, 132, 255)); // Windows 11 / iOS Accent
        }

        var text = new TextBlock
        {
            Text = day.ToString(CultureInfo.InvariantCulture),
            FontSize = 11,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isToday
                ? Brushes.White
                : (isCurrentMonth ? (ForegroundBrush ?? Brushes.White) : (SecondaryForegroundBrush ?? Brushes.Gray)),
            Opacity = isToday ? 1.0 : (isCurrentMonth ? 0.95 : 0.35),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        border.Child = text;
        btn.Content = border;
        return btn;
    }

    private void OnPrevMonthClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RenderCalendar(_displayMonth);
    }

    private void OnNextMonthClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        RenderCalendar(_displayMonth);
    }

    private void OnCalendarExpandCollapseClick(object sender, RoutedEventArgs e)
    {
        _isCalendarExpanded = !_isCalendarExpanded;
        CalendarContainer.Visibility = _isCalendarExpanded ? Visibility.Visible : Visibility.Collapsed;

        var animation = new DoubleAnimation
        {
            To = _isCalendarExpanded ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CalendarChevronRotation.BeginAnimation(RotateTransform.AngleProperty, animation);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            MeasureContentAndPosition();
            GlassSurface.RequestBackdropRefresh();
        });
    }

    #endregion

    #region Focus Sessions (Tính năng tập trung của Win11)

    private void OnFocusMinusClick(object sender, RoutedEventArgs e)
    {
        if (_isFocusRunning) return;
        if (_focusMinutes > 15)
        {
            _focusMinutes -= 5;
            FocusDurationText.Text = $"{_focusMinutes} phút";
        }
    }

    private void OnFocusPlusClick(object sender, RoutedEventArgs e)
    {
        if (_isFocusRunning) return;
        if (_focusMinutes < 120)
        {
            _focusMinutes += 5;
            FocusDurationText.Text = $"{_focusMinutes} phút";
        }
    }

    private void OnFocusActionClick(object sender, RoutedEventArgs e)
    {
        _isFocusRunning = !_isFocusRunning;
        if (_isFocusRunning)
        {
            _focusSecondsRemaining = _focusMinutes * 60;
            _focusTimer.Start();
            FocusActionBubble.Background = new SolidColorBrush(Color.FromArgb(0x40, 239, 68, 68)); // Red stop glow
            FocusActionBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 239, 68, 68));
            FocusActionText.Text = "Dừng";
            FocusActionText.Foreground = new SolidColorBrush(Color.FromRgb(254, 202, 202));
            UpdateFocusCountdownDisplay();
        }
        else
        {
            _focusTimer.Stop();
            FocusActionBubble.Background = new SolidColorBrush(Color.FromArgb(0x25, 139, 92, 246));
            FocusActionBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 139, 92, 246));
            FocusActionText.Text = "Bắt đầu";
            FocusActionText.Foreground = new SolidColorBrush(Color.FromRgb(221, 214, 254));
            FocusDurationText.Text = $"{_focusMinutes} phút";
        }
    }

    private void OnFocusTimerTick(object? sender, EventArgs e)
    {
        if (_focusSecondsRemaining > 0)
        {
            _focusSecondsRemaining--;
            UpdateFocusCountdownDisplay();
        }
        else
        {
            OnFocusActionClick(FocusActionButton, new RoutedEventArgs());
        }
    }

    private void UpdateFocusCountdownDisplay()
    {
        int minutes = _focusSecondsRemaining / 60;
        int seconds = _focusSecondsRemaining % 60;
        FocusDurationText.Text = $"{minutes:D2}:{seconds:D2} (Tập trung)";
    }

    #endregion

    private void OnOpenWindowsNotificationSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid] Could not open Windows settings: {ex.Message}");
        }
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_disposed || _isClosing) return;
        CloseWithFade();
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithFade();
        }
    }

    public void CloseWithFade()
    {
        if (_isClosing) return;
        _isClosing = true;
        UninstallMouseHook();

        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        animation.Completed += (_, _) =>
        {
            try { Close(); } catch { /* Ignore */ }
        };
        PopupVisualRoot.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isClosing = true;
        UninstallMouseHook();
        WindowsNotificationManager.Instance.NotificationsChanged -= OnNotificationsChanged;
        WindowsDoNotDisturbController.StateChanged -= OnDoNotDisturbStateChanged;
        _focusTimer.Stop();
        _focusTimer.Tick -= OnFocusTimerTick;
        GC.SuppressFinalize(this);
    }

    #region Outside Click Low-Level Mouse Hook

    private nint _mouseHookHandle;
    private HookProc? _mouseHookDelegate;
    private Rect? _cachedDockNotificationButtonScreenRect;

    private void CacheDockNotificationButtonRect()
    {
        _cachedDockNotificationButtonScreenRect = null;
        try
        {
            var btn = _dock.NotificationsButton;
            if (btn.IsVisible)
            {
                var btnPoint = btn.PointToScreen(new Point(0, 0));
                _cachedDockNotificationButtonScreenRect = new Rect(btnPoint.X, btnPoint.Y, btn.ActualWidth, btn.ActualHeight);
            }
        }
        catch { /* Ignore */ }
    }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != 0) return;
        CacheDockNotificationButtonRect();
        _mouseHookDelegate = MouseHookCallback;
        _mouseHookHandle = SetWindowsHookEx(14, _mouseHookDelegate, GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookHandle != 0)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = 0;
            _mouseHookDelegate = null;
        }
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && !_isClosing && !_disposed)
            {
                int msg = (int)wParam;
                // WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_NCLBUTTONDOWN = 0x00A1, WM_NCRBUTTONDOWN = 0x00A4
                if (msg is 0x0201 or 0x0204 or 0x00A1 or 0x00A4)
                {
                    var mouseInfo = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != 0 && GetWindowRect(hwnd, out var rect))
                    {
                        // Check if cursor position is outside this flyout's window rectangle
                        if (mouseInfo.pt.x < rect.Left || mouseInfo.pt.x > rect.Right ||
                            mouseInfo.pt.y < rect.Top || mouseInfo.pt.y > rect.Bottom)
                        {
                            // If user clicked the Dock's Notification button itself, check cached rect
                            bool isDockButton = _cachedDockNotificationButtonScreenRect.HasValue
                                && _cachedDockNotificationButtonScreenRect.Value.Contains(new Point(mouseInfo.pt.x, mouseInfo.pt.y));

                            if (!isDockButton)
                            {
                                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(CloseWithFade));
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Never let exceptions cross native hook callback
        }

        return CallNextHookEx(_mouseHookHandle, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    #endregion
}
