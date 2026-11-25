using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Xml.Linq;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>A Glassquid-owned quick settings panel opened from the network/audio/battery group.</summary>
public partial class QuickSettingsFlyoutWindow : GlassWindow, IDisposable
{
    private readonly SystemTrayBarWindow _dock;
    private readonly BluetoothQuickActionController _bluetooth = new();
    private readonly AirplaneModeQuickActionController _airplaneMode = new();
    private readonly NightLightQuickActionController _nightLight = new();
    private readonly NightLightStrengthController _nightLightStrength = new();
    private readonly MediaTransportController _media = new();
    private bool _isClosing;
    private bool _closeQueued;
    private bool _glassRevealPending;
    private bool _disposed;
    private bool _isEditingControls;
    private bool _isShowingNightLightStrength;
    private bool _energySaverTogglePending;
    private bool _nightLightTogglePending;
    private readonly DispatcherTimer _brightnessDebounceTimer = new(DispatcherPriority.Background)
    {
        // Coalesce rapid pointer moves so the monitor only receives the latest value.
        Interval = TimeSpan.FromMilliseconds(200),
    };
    private AudioEndpointVolumeController? _audioVolume;
    private bool _loadingBrightness;
    private int _brightnessRequestVersion;
    private int _pendingBrightness;
    private Task _brightnessWriteTask = Task.CompletedTask;
    private bool _loadingNightLightStrength;
    private int _nightLightStrengthRequestVersion;
    private int _pendingNightLightStrength;
    private Task _nightLightStrengthWriteTask = Task.CompletedTask;
    private bool _bluetoothTogglePending;
    private bool _airplaneModeTogglePending;
    private bool _wifiConnected;
    private bool _bluetoothLongPressTriggered;
    private bool _nightLightLongPressTriggered;
    private bool _lastWifiConnected;
    private string _lastWifiStatus = "Chưa kết nối";
    private bool _lastIsEthernet;
    private BluetoothQuickActionState _lastBluetoothState = BluetoothQuickActionState.Searching;
    private AirplaneModeQuickActionState _lastAirplaneState = AirplaneModeQuickActionState.Searching;
    private bool? _lastNightLightState;
    private bool? _lastEnergySaverState;
    private BluetoothDevicesFlyoutWindow? _bluetoothDevicesFlyoutWindow;
    private WifiFlyoutWindow? _wifiFlyoutWindow;
    private List<ControlTileLayout> _tileLayout;
    private readonly Dictionary<string, FrameworkElement> _editTileElements = new();
    private Geometry? _energySaverGeometry;
    private ControlTileLayout? _resizingTile;
    private readonly Border[,] _placeholderCells = new Border[8, 4];
    private readonly DispatcherTimer _bluetoothLongPressTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };
    private readonly DispatcherTimer _nightLightRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };
    private readonly DispatcherTimer _energySaverRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(1000),
    };
    private readonly DispatcherTimer _nightLightStrengthDebounceTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };
    private readonly DispatcherTimer _nightLightLongPressTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal QuickSettingsFlyoutWindow(
        SystemTrayBarWindow dock,
        string networkStatus,
        string volumeStatus,
        string batteryStatus)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;

        InitializeComponent();
        InitializePlaceholderGrid();
        _energySaverGeometry = LoadEnergySaverGeometry();
        EnergySaverIcon.Data = _energySaverGeometry;
        _tileLayout = ControlTileLayout.LoadLayout() ?? ControlTileLayout.CreateDefaultLayout();
        EnsureSlidersInLayout(_tileLayout);
        SanitizeLayout();
        ApplyLayoutToNormalPanel();
        _bluetooth.StateChanged += OnBluetoothStateChanged;
        _airplaneMode.StateChanged += OnAirplaneModeStateChanged;
        _nightLight.StateChanged += OnNightLightStateChanged;
        _nightLightStrength.StrengthChanged += OnNightLightStrengthChanged;
        _media.MediaChanged += OnMediaTrackChanged;
        _ = _media.InitializeAsync();
        _bluetoothLongPressTimer.Tick += OnBluetoothLongPressTimerTick;
        _nightLightLongPressTimer.Tick += OnNightLightLongPressTimerTick;
        _nightLightRefreshTimer.Tick += OnNightLightRefreshTimerTick;
        _energySaverRefreshTimer.Tick += OnEnergySaverRefreshTimerTick;
        _nightLightStrengthDebounceTimer.Tick += OnNightLightStrengthDebounceTimerTick;
        ApplyEnergySaverState(null);
        ApplyBluetoothState(BluetoothQuickActionState.Searching);
        ApplyAirplaneModeState(AirplaneModeQuickActionState.Searching);
        ApplyNightLightState(null);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        var isEthernetInitial = networkStatus.StartsWith("Mạng dây", StringComparison.CurrentCultureIgnoreCase)
                                || networkStatus.StartsWith("Ethernet", StringComparison.CurrentCultureIgnoreCase);
        _wifiConnected = isEthernetInitial || networkStatus.StartsWith("Wi-Fi đang kết nối", StringComparison.CurrentCultureIgnoreCase);
        ApplyWifiTileState(_wifiConnected, _wifiConnected ? (isEthernetInitial ? "Đã kết nối" : "Đang lấy tên mạng…") : CleanStatus(networkStatus, "Wi-Fi"), isEthernet: isEthernetInitial);
        _ = RefreshWifiTileStateAsync();
        VolumeStatusText.Text = CleanStatus(volumeStatus, "Âm lượng");
        BatteryQuickButton.ToolTip = CleanStatus(batteryStatus, "Pin");
        AutomationProperties.SetName(BatteryQuickButton, CleanStatus(batteryStatus, "Pin"));

        try
        {
            _audioVolume = new AudioEndpointVolumeController();
            VolumeSlider.Value = _audioVolume.GetMasterVolumePercent();
            VolumeStatusText.Text = $"Âm lượng {VolumeSlider.Value:0}%";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            VolumeSlider.IsEnabled = false;
            VolumeSlider.ToolTip = "Không tìm thấy thiết bị âm thanh đầu ra.";
            VolumeStatusText.Text = "Âm thanh không khả dụng";
        }

        VolumeSlider.ValueChanged += OnVolumeSliderValueChanged;
        BrightnessSlider.ValueChanged += OnBrightnessSliderValueChanged;
        BrightnessSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnBrightnessDragCompleted));
        NightLightStrengthSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnNightLightStrengthDragCompleted));
        _brightnessDebounceTimer.Tick += OnBrightnessDebounceTick;
        Loaded += OnPanelLoaded;
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
    }

    private async Task RefreshWifiTileStateAsync()
    {
        try
        {
            var (isConnected, status, isEthernet) = await Task.Run(() =>
            {
                // 1. Check Ethernet connection
                Windows.Networking.Connectivity.ConnectionProfile? profile = null;
                try
                {
                    profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                }
                catch
                {
                    // Fallback gracefully if WinRT API is unavailable
                }

                var activeAdapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                var activeEthernet = activeAdapters.FirstOrDefault(a => a.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet);

                var ethernetDetected = (profile is { IsWlanConnectionProfile: false } && (profile.NetworkAdapter?.IanaInterfaceType == 6 || activeEthernet is not null))
                                       || (profile is null && activeEthernet is not null && NetworkInterface.GetIsNetworkAvailable());

                if (ethernetDetected)
                {
                    var hasInternet = profile is not null &&
                                      profile.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
                    return (true, hasInternet ? "Đã kết nối" : "Không có Internet", true);
                }

                // 2. Check Wi-Fi connection
                var connectedWifi = NativeWifi.ReadNetworks().FirstOrDefault(network => network.IsConnected);
                if (connectedWifi is not null && !string.IsNullOrWhiteSpace(connectedWifi.Name))
                {
                    return (true, connectedWifi.Name.Trim(), false);
                }

                if (profile is { IsWlanConnectionProfile: true } && !string.IsNullOrWhiteSpace(profile.ProfileName))
                {
                    return (true, profile.ProfileName.Trim(), false);
                }

                return (false, "Chưa kết nối", false);
            });

            if (_disposed || _isClosing) return;

            ApplyWifiTileState(isConnected, status, isEthernet);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Wi-Fi] Could not refresh network state: {ex.Message}");
            if (!_disposed && !_isClosing && _wifiConnected)
                ApplyWifiTileState(true, "Đã kết nối", _lastIsEthernet);
        }
    }

    private void ApplyWifiTileState(bool isConnected, string status, bool isEthernet = false)
    {
        _wifiConnected = isConnected;
        _lastWifiConnected = isConnected;
        _lastWifiStatus = status;
        _lastIsEthernet = isEthernet;

        var tile = _tileLayout?.FirstOrDefault(t => t.Key == "wifi");
        bool isCircle = tile != null && tile.ColumnSpan == 1 && tile.RowSpan == 1;

        Brush background;
        if (isConnected)
        {
            background = Brushes.White;
        }
        else
        {
            background = isCircle
                ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49));
        }

        Brush iconBubble;
        if (isCircle)
        {
            iconBubble = Brushes.Transparent;
        }
        else
        {
            iconBubble = isConnected
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xFB))
                : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        }

        var iconForeground = isConnected
            ? new SolidColorBrush(Color.FromRgb(24, 104, 220))
            : Brushes.White;

        var titleForeground = isConnected
            ? new SolidColorBrush(Color.FromRgb(32, 33, 36))
            : (ForegroundBrush ?? Brushes.White);

        var detailForeground = isConnected
            ? new SolidColorBrush(Color.FromRgb(113, 117, 122))
            : (SecondaryForegroundBrush ?? Brushes.White);

        WifiQuickButton.Background = background;
        WifiIconBubble.Background = iconBubble;
        WifiIcon.Foreground = iconForeground;
        WifiIcon.Text = isEthernet ? "\uE839" : "\uE701";
        WifiNameText.Text = isEthernet ? "Ethernet" : "Wi-Fi";
        WifiNameText.Foreground = titleForeground;
        NetworkStatusText.Text = status;
        NetworkStatusText.Foreground = detailForeground;

        var tooltip = isEthernet
            ? (isConnected ? $"Ethernet · {status}" : "Ethernet chưa kết nối")
            : (isConnected ? $"Wi-Fi · {status}" : "Wi-Fi chưa kết nối");
        WifiQuickButton.ToolTip = tooltip;
        AutomationProperties.SetName(WifiQuickButton, tooltip);

        // Sync edit mode Wi-Fi tile if it exists (dynamically generated)
        if (_editTileElements.TryGetValue("wifi", out var editWifiElement) && editWifiElement is Button editWifiBtn)
        {
            editWifiBtn.Background = background;
            editWifiBtn.ToolTip = WifiQuickButton.ToolTip;
        }
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndPosition();
        GlassSurface.PrepareRevealForWindow(this);
        InstallMouseHook();
        _ = _media.RefreshMediaAsync();
        _ = RefreshWifiTileStateAsync();
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

        var anchor = _dock.GetQuickSettingsAnchorPoint();
        Left = Math.Clamp(anchor.X - Width / 2, workArea.Left + 4,
            Math.Max(workArea.Left + 4, workArea.Right - Width - 4));
        Top = _dock.IsTopPlacement
            ? Math.Clamp(anchor.Y + 14, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - Height - 4))
            : Math.Clamp(anchor.Y - Height - 14, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - Height - 4));
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

    protected override void OnClosed(EventArgs e)
    {
        UninstallMouseHook();
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isClosing = true;
        UninstallMouseHook();
        _brightnessDebounceTimer.Stop();
        _brightnessDebounceTimer.Tick -= OnBrightnessDebounceTick;
        _bluetoothLongPressTimer.Stop();
        _bluetoothLongPressTimer.Tick -= OnBluetoothLongPressTimerTick;
        _nightLightLongPressTimer.Stop();
        _nightLightLongPressTimer.Tick -= OnNightLightLongPressTimerTick;
        _bluetooth.StateChanged -= OnBluetoothStateChanged;
        _airplaneMode.StateChanged -= OnAirplaneModeStateChanged;
        _nightLight.StateChanged -= OnNightLightStateChanged;
        _nightLightStrength.StrengthChanged -= OnNightLightStrengthChanged;
        _nightLightRefreshTimer.Stop();
        _nightLightRefreshTimer.Tick -= OnNightLightRefreshTimerTick;
        _energySaverRefreshTimer.Stop();
        _energySaverRefreshTimer.Tick -= OnEnergySaverRefreshTimerTick;
        _nightLightStrengthDebounceTimer.Stop();
        _nightLightStrengthDebounceTimer.Tick -= OnNightLightStrengthDebounceTimerTick;
        _bluetooth.Dispose();
        _airplaneMode.Dispose();
        _nightLight.Dispose();
        _media.MediaChanged -= OnMediaTrackChanged;
        _media.Dispose();
        VolumeSlider.ValueChanged -= OnVolumeSliderValueChanged;
        BrightnessSlider.ValueChanged -= OnBrightnessSliderValueChanged;
        BrightnessSlider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnBrightnessDragCompleted));
        NightLightStrengthSlider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnNightLightStrengthDragCompleted));
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _audioVolume?.Dispose();
        _audioVolume = null;
        GC.SuppressFinalize(this);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed || _isClosing) return;
        Dispatcher.InvokeAsync(() => _ = RefreshWifiTileStateAsync());
    }

    private async void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _ = _bluetooth.RefreshAsync();
        _ = _airplaneMode.RefreshAsync();
        _ = _media.RefreshMediaAsync();
        _ = RefreshWifiTileStateAsync();
        RefreshEnergySaverState();
        _energySaverRefreshTimer.Start();
        RefreshNightLightState();
        _nightLightRefreshTimer.Start();
        _ = _nightLight.RefreshAsync();
        _ = RefreshNightLightStrengthAsync();
        var brightness = await Task.Run(DisplayBrightnessController.TryGetCurrentPercent);
        if (!IsVisible) return;

        _loadingBrightness = true;
        if (brightness is { } value)
        {
            BrightnessSlider.Value = value;
            BrightnessSlider.IsEnabled = true;
            BrightnessStatusText.Text = $"Độ sáng {value}%";
        }
        else
        {
            BrightnessSlider.IsEnabled = false;
            BrightnessSlider.ToolTip = "Màn hình này không cung cấp điều khiển độ sáng cho Windows.";
            BrightnessStatusText.Text = "Độ sáng không khả dụng";
        }
        _loadingBrightness = false;
    }

    private async void OnBluetoothButtonClick(object sender, RoutedEventArgs e)
    {
        if (_bluetoothLongPressTriggered)
        {
            _bluetoothLongPressTriggered = false;
            return;
        }
        if (_bluetoothTogglePending) return;
        _bluetoothTogglePending = true;
        BluetoothButton.IsEnabled = false;
        ApplyBluetoothState(BluetoothQuickActionState.Searching);
        await _bluetooth.ToggleAsync();
        _bluetoothTogglePending = false;
        if (!_disposed) BluetoothButton.IsEnabled = true;
    }

    private void OnDisplayProjectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || !DisplayProjectionButton.IsEnabled) return;
        try
        {
            var flyout = new DisplayProjectionFlyoutWindow(_dock);
            flyout.PrepareForShow();
            flyout.Show();
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Display projection] Could not open panel: {ex.Message}");
            DisplayProjectionButton.ToolTip = "Không mở được bảng chiếu màn hình.";
        }
    }

    private void OnOpenSoundOutputClick(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            var flyout = new SoundOutputFlyoutWindow(_dock);
            flyout.PrepareForShow();
            flyout.Show();
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Sound Output] Could not open panel: {ex.Message}");
            OpenSoundOutputButton.ToolTip = "Không mở được bảng cài đặt âm thanh.";
        }
    }

    private async void OnAirplaneButtonClick(object sender, RoutedEventArgs e)
    {
        if (_airplaneModeTogglePending || _disposed) return;
        _airplaneModeTogglePending = true;
        AirplaneButton.IsEnabled = false;
        ApplyAirplaneModeState(AirplaneModeQuickActionState.Searching);
        var changed = await _airplaneMode.ToggleAsync();
        _airplaneModeTogglePending = false;
        if (_disposed) return;
        AirplaneButton.IsEnabled = true;
        if (!changed)
            AirplaneButton.ToolTip = "Windows không cho phép thay đổi trạng thái radio. Bấm lại để thử.";
    }

    private void RefreshNightLightState() => _ = RefreshNightLightStateAsync();

    private async Task RefreshNightLightStateAsync()
    {
        if (_disposed || _nightLightTogglePending) return;
        var state = await Task.Run(NightLight.IsEnabled);
        if (!_disposed) ApplyNightLightState(state);
    }

    private async void OnNightLightButtonClick(object sender, RoutedEventArgs e)
    {
        if (_nightLightLongPressTriggered)
        {
            _nightLightLongPressTriggered = false;
            return;
        }
        if (_disposed || _nightLightTogglePending) return;
        _nightLightTogglePending = true;
        NightLightButton.IsEnabled = false;

        try
        {
            var requestedState = await Task.Run(NightLight.Toggle);
            if (_disposed) return;
            if (requestedState is null)
            {
                NightLightButton.ToolTip = "Không thể thay đổi trạng thái Night light của Windows.";
                return;
            }

            ApplyNightLightState(requestedState);
            await Task.Delay(150);
            var actualState = await Task.Run(NightLight.IsEnabled);
            if (!_disposed) ApplyNightLightState(actualState ?? requestedState);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Night light] Could not toggle Windows state: {ex.Message}");
            if (!_disposed) NightLightButton.ToolTip = "Không thể thay đổi trạng thái Night light của Windows.";
        }
        finally
        {
            _nightLightTogglePending = false;
            if (!_disposed)
            {
                NightLightButton.IsEnabled = true;
                RefreshNightLightState();
            }
        }
    }

    private async void OnNightLightRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshNightLightStateAsync();
        await RefreshNightLightStrengthAsync();
    }

    private async Task RefreshNightLightStrengthAsync()
    {
        await _nightLightStrength.RefreshAsync();
        if (_disposed) return;
        if (_nightLightStrength.Strength < 0 && !string.IsNullOrWhiteSpace(_nightLightStrength.Error))
        {
            NightLightStrengthSlider.IsEnabled = false;
            NightLightStrengthValueText.Text = "Không khả dụng";
            NightLightStrengthStatusText.Text = _nightLightStrength.Error;
            NightLightStrengthSlider.ToolTip = _nightLightStrength.Error;
            return;
        }
        NightLightStrengthStatusText.Text = "Đang đọc trực tiếp cường độ Night light của Windows.";
    }

    private void OnNightLightButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _nightLightLongPressTriggered = false;
        _nightLightLongPressTimer.Stop();
        _nightLightLongPressTimer.Start();
    }

    private void OnNightLightButtonPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _nightLightLongPressTimer.Stop();
        if (_nightLightLongPressTriggered) e.Handled = true;
    }

    private void OnNightLightLongPressTimerTick(object? sender, EventArgs e)
    {
        _nightLightLongPressTimer.Stop();
        if (_disposed || Mouse.LeftButton != MouseButtonState.Pressed || !NightLightButton.IsMouseOver) return;
        _nightLightLongPressTriggered = true;
        OpenNightLightStrengthPanel();
    }

    private void OpenNightLightStrengthPanel()
    {
        _isShowingNightLightStrength = true;
        _isEditingControls = false;
        NormalControlsPanel.Visibility = Visibility.Collapsed;
        EditControlsGrid.Visibility = Visibility.Collapsed;
        NightLightStrengthPanel.Visibility = Visibility.Visible;
        PanelTitleText.Text = "Night light";
        EditControlsButtonText.Text = "Quay lại";
        EditControlsButtonText.FontWeight = FontWeights.Medium;
        EditControlsButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
        EditControlsButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
        EditControlsButtonText.Foreground = ForegroundBrush ?? Brushes.White;
        AutomationProperties.SetName(EditControlsButton, "Quay lại trung tâm điều khiển");
        MeasureContentAndPosition();
        _ = RefreshNightLightStrengthAsync();
    }

    private void OnPanelHeaderActionClick(object sender, RoutedEventArgs e)
    {
        if (_isShowingNightLightStrength)
        {
            _isShowingNightLightStrength = false;
            _isEditingControls = false;
            NightLightStrengthPanel.Visibility = Visibility.Collapsed;
            NormalControlsPanel.Visibility = Visibility.Visible;
            EditControlsGrid.Visibility = Visibility.Collapsed;
            EditHiddenControlsPanel.Visibility = Visibility.Collapsed;
            PanelTitleText.Text = "Trung tâm điều khiển";
            EditControlsButtonText.Text = "Chỉnh sửa giao diện";
            EditControlsButtonText.FontWeight = FontWeights.Medium;
            EditControlsButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
            EditControlsButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
            EditControlsButtonText.Foreground = ForegroundBrush ?? Brushes.White;
            AutomationProperties.SetName(EditControlsButton, EditControlsButtonText.Text);
            MeasureContentAndPosition();
            return;
        }
        OnEditControlsClick(sender, e);
    }

    private void OnBluetoothButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _bluetoothLongPressTriggered = false;
        _bluetoothLongPressTimer.Stop();
        _bluetoothLongPressTimer.Start();
    }

    private void OnBluetoothButtonPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _bluetoothLongPressTimer.Stop();
        if (_bluetoothLongPressTriggered) e.Handled = true;
    }

    private void OnBluetoothLongPressTimerTick(object? sender, EventArgs e)
    {
        _bluetoothLongPressTimer.Stop();
        if (_disposed || Mouse.LeftButton != MouseButtonState.Pressed || !BluetoothButton.IsMouseOver) return;
        _bluetoothLongPressTriggered = true;
        OpenBluetoothDevicesFlyout();
    }

    private void OpenBluetoothDevicesFlyout()
    {
        if (_bluetoothDevicesFlyoutWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var flyout = new BluetoothDevicesFlyoutWindow(_dock);
        _bluetoothDevicesFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_bluetoothDevicesFlyoutWindow, flyout))
                _bluetoothDevicesFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Bluetooth] Could not open devices panel: {ex}");
            _bluetoothDevicesFlyoutWindow = null;
        }
    }

    private void OnWifiTileClick(object sender, RoutedEventArgs e) => OpenWifiFlyout();

    private void OnEditControlsClick(object sender, RoutedEventArgs e)
    {
        _isEditingControls = !_isEditingControls;
        if (_isEditingControls)
        {
            BuildEditTiles();
        }
        else
        {
            // Exiting edit mode — apply layout changes to the normal panel and save
            ApplyLayoutToNormalPanel();
            ControlTileLayout.SaveLayout(_tileLayout);
            ClearEditTiles();
            _ = RefreshWifiTileStateAsync();
        }

        NormalControlsPanel.Visibility = _isEditingControls ? Visibility.Collapsed : Visibility.Visible;
        EditControlsGrid.Visibility = _isEditingControls ? Visibility.Visible : Visibility.Collapsed;
        if (!_isEditingControls)
        {
            EditHiddenControlsPanel.Visibility = Visibility.Collapsed;
            HiddenControlsWrapPanel.Children.Clear();
        }
        if (_isEditingControls)
        {
            EditControlsButtonText.Text = "Xong";
            EditControlsButtonText.FontWeight = FontWeights.SemiBold;
            EditControlsButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
            EditControlsButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
            EditControlsButtonText.Foreground = Brushes.White;
        }
        else
        {
            EditControlsButtonText.Text = "Chỉnh sửa giao diện";
            EditControlsButtonText.FontWeight = FontWeights.Medium;
            EditControlsButtonBubble.Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
            EditControlsButtonBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
            EditControlsButtonText.Foreground = ForegroundBrush ?? Brushes.White;
        }
        AutomationProperties.SetName(EditControlsButton, EditControlsButtonText.Text);
        MeasureContentAndPosition();
    }

    // ────────────────────────────────────────────────
    //  Layout synchronization — edit tiles ↔ normal panel
    // ────────────────────────────────────────────────

    /// <summary>
    /// Maps tile keys to their metadata: icon, default title, default status/tooltip, is SVG geometry.
    /// </summary>
    private static readonly Dictionary<string, (string Icon, string Title, string Tooltip, bool IsSvg)> _tileMetadata = new()
    {
        ["wifi"]        = ("\uE701", "Wi-Fi", "Wi-Fi", false),
        ["media"]       = ("\uE768", "Đang phát", "Không có nội dung", false),
        ["bluetooth"]   = ("\uE702", "Bluetooth", "Đang tắt", true),
        ["airplane"]    = ("\uE709", "Chế độ máy bay", "Đang tắt", true),
        ["projection"]  = ("\uE7F4", "Chiếu màn hình", "Windows + P", false),
        ["nightlight"]  = ("\uE708", "Night light", "Đang tắt", false),
        ["energysaver"] = ("\uE945", "Tiết kiệm pin", "Đang tắt", true),
        ["battery"]     = ("\uE8A7", "Pin", "Trạng thái pin", false),
        ["calculator"]  = ("\uE8EF", "Máy tính", "Mở ứng dụng", false),
        ["clock"]       = ("\uE916", "Đồng hồ", "Hẹn giờ & Báo thức", false),
        ["camera"]      = ("\uE722", "Máy ảnh", "Chụp ảnh & Video", false),
    };

    // ────────────────────────────────────────────────
    //  ControlTile slider helpers & visual states
    // ────────────────────────────────────────────────

    private static bool IsSlider(ControlTileLayout tile) =>
        tile.Key == "brightness" || tile.Key == "volume";

    private bool IsRowOccupiedBySlider(int row, ControlTileLayout? excludeSlider = null) =>
        _tileLayout.Any(t => IsSlider(t) && !ReferenceEquals(t, excludeSlider) && t.Row == row);

    private static void EnsureSlidersInLayout(List<ControlTileLayout> layout)
    {
        var brightness = layout.FirstOrDefault(t => t.Key == "brightness");
        if (brightness == null)
        {
            layout.Add(new ControlTileLayout { Key = "brightness", Row = 3, Column = 0, ColumnSpan = 4, RowSpan = 1 });
        }
        else
        {
            brightness.Column = 0;
            brightness.ColumnSpan = 4;
            brightness.RowSpan = 1;
        }

        var volume = layout.FirstOrDefault(t => t.Key == "volume");
        if (volume == null)
        {
            layout.Add(new ControlTileLayout { Key = "volume", Row = 4, Column = 0, ColumnSpan = 4, RowSpan = 1 });
        }
        else
        {
            volume.Column = 0;
            volume.ColumnSpan = 4;
            volume.RowSpan = 1;
        }
    }

    private static readonly Brush _sliderHoverBorder = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 0.0),
            new GradientStop(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF), 0.4),
            new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.6),
            new GradientStop(Color.FromArgb(0x75, 0xFF, 0xFF, 0xFF), 1.0)
        },
        new Point(0, 0), new Point(0, 1));

    private static readonly Brush _sliderNormalBorder = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
            new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.4),
            new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.6),
            new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0)
        },
        new Point(0, 0), new Point(0, 1));
    private static readonly SolidColorBrush _sliderHoverBackground = new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _sliderNormalBackground = new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private void OnSliderPanelMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = _sliderHoverBorder;
            border.Background = _sliderHoverBackground;
        }
    }

    private void OnSliderPanelMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = _sliderNormalBorder;
            border.Background = _sliderNormalBackground;
        }
    }

    // ────────────────────────────────────────────────
    //  Edit mode — Thumb-based drag-to-resize & reorder
    // ────────────────────────────────────────────────

    private static ControlTemplate CreateResizeThumbTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ThumbBorder";
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4D, 0x56, 0x60)));
        borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x8B, 0x98, 0xA5)));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));

        var dotFactory = new FrameworkElementFactory(typeof(Border));
        dotFactory.SetValue(Border.WidthProperty, 6.0);
        dotFactory.SetValue(Border.HeightProperty, 6.0);
        dotFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        dotFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));
        dotFactory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dotFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(dotFactory);

        template.VisualTree = borderFactory;

        var mouseOverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x65, 0x72, 0x7F)), "ThumbBorder"));
        mouseOverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xB8, 0xCC, 0xDF)), "ThumbBorder"));
        template.Triggers.Add(mouseOverTrigger);

        var draggingTrigger = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
        draggingTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x75, 0x85, 0x95)), "ThumbBorder"));
        draggingTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), "ThumbBorder"));
        template.Triggers.Add(draggingTrigger);

        return template;
    }

    private static readonly ControlTemplate _resizeThumbTemplate = CreateResizeThumbTemplate();

    private Thumb CreateResizeThumb(ControlTileLayout tile)
    {
        var thumb = new Thumb
        {
            Width = 26, Height = 26,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeWE,
            Tag = tile,
            Template = _resizeThumbTemplate,
            ToolTip = "Kéo để đổi kích thước (1 ô tròn, 2 grid, 4 grid)",
        };
        thumb.DragStarted += OnResizeThumbDragStarted;
        thumb.DragDelta += OnResizeThumbDragDelta;
        thumb.DragCompleted += OnResizeThumbDragCompleted;
        return thumb;
    }

    private Point _resizeStartMousePos;
    private int _dragStartColSpan;
    private int _dragStartRowSpan;
    private int _dragStartCol;
    private int _dragStartRow;

    private void OnResizeThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not ControlTileLayout tile) return;
        _resizingTile = tile;
        _resizeStartMousePos = Mouse.GetPosition(EditControlsGrid);
        _dragStartColSpan = tile.ColumnSpan;
        _dragStartRowSpan = tile.RowSpan;
        _dragStartCol = tile.Column;
        _dragStartRow = tile.Row;
    }

    private void OnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_resizingTile == null) return;
        var currentPos = Mouse.GetPosition(EditControlsGrid);
        double deltaX = currentPos.X - _resizeStartMousePos.X;
        double deltaY = currentPos.Y - _resizeStartMousePos.Y;

        int targetColSpan = _dragStartColSpan;
        int targetRowSpan = _dragStartRowSpan;

        if (_dragStartColSpan == 1 && _dragStartRowSpan == 1) // Started at 1x1 circle
        {
            // Dragged down-right or diagonally down -> 2x2 square (4-grid)
            if (deltaY >= 40 && deltaX >= 25)
            {
                targetColSpan = 2;
                targetRowSpan = 2;
            }
            // Dragged right -> 2x1 capsule (2-grid)
            else if (deltaX >= 25)
            {
                targetColSpan = 2;
                targetRowSpan = 1;
            }
            else
            {
                targetColSpan = 1;
                targetRowSpan = 1;
            }
        }
        else if (_dragStartColSpan == 2 && _dragStartRowSpan == 1) // Started at 2x1 capsule
        {
            // Dragged down -> 2x2 square (4-grid)
            if (deltaY >= 40)
            {
                targetColSpan = 2;
                targetRowSpan = 2;
            }
            // Dragged left -> 1x1 circle (1-grid)
            else if (deltaX <= -25)
            {
                targetColSpan = 1;
                targetRowSpan = 1;
            }
            else
            {
                targetColSpan = 2;
                targetRowSpan = 1;
            }
        }
        else if (_dragStartColSpan >= 2 && _dragStartRowSpan >= 2) // Started at 2x2 square
        {
            // Dragged up and left significantly -> 1x1 circle
            if (deltaY <= -50 && deltaX <= -30)
            {
                targetColSpan = 1;
                targetRowSpan = 1;
            }
            // Dragged up -> 2x1 capsule
            else if (deltaY <= -35)
            {
                targetColSpan = 2;
                targetRowSpan = 1;
            }
            // Dragged left -> 1x1 circle
            else if (deltaX <= -50)
            {
                targetColSpan = 1;
                targetRowSpan = 1;
            }
            else
            {
                targetColSpan = 2;
                targetRowSpan = 2;
            }
        }

        if (targetColSpan != _resizingTile.ColumnSpan || targetRowSpan != _resizingTile.RowSpan)
        {
            int targetCol = _dragStartCol;
            int targetRow = _dragStartRow;

            if (targetColSpan == 2 && targetCol > 2)
            {
                targetCol = 2;
            }

            if (targetRowSpan == 2)
            {
                if (targetRow > 6) targetRow = 6;

                // If expanding down hits a slider, try shifting up by 1 row
                if (IsRowOccupiedBySlider(targetRow + 1))
                {
                    if (targetRow > 0 && !IsRowOccupiedBySlider(targetRow - 1))
                    {
                        targetRow--;
                    }
                }

                // If it still hits a slider, do not allow expanding to 2 rows
                if (IsRowOccupiedBySlider(targetRow) || IsRowOccupiedBySlider(targetRow + 1))
                {
                    targetRowSpan = 1;
                    targetRow = _dragStartRow;
                }
            }

            if (TryResizeTile(_resizingTile, targetCol, targetRow, targetColSpan, targetRowSpan))
            {
                UpdateEditTilesVisual();
            }
        }
    }

    private void OnResizeThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resizingTile != null)
        {
            SanitizeLayout();
            ControlTileLayout.SaveLayout(_tileLayout);
            _resizingTile = null;
            UpdateEditTilesVisual();
        }
    }

    /// <summary>Populates EditControlsGrid with interactive tiles generated from _tileLayout.</summary>
    private void BuildEditTiles()
    {
        SanitizeLayout();
        ClearEditTiles();

        if (!EditControlsGrid.Children.Contains(EditPlaceholdersGrid))
        {
            EditControlsGrid.Children.Insert(0, EditPlaceholdersGrid);
        }
        Panel.SetZIndex(EditPlaceholdersGrid, 0);

        if (_placeholderCells[0, 0] == null || EditPlaceholdersGrid.Children.Count == 0)
        {
            InitializePlaceholderGrid();
        }

        var panelIconFont = (FontFamily)FindResource("PanelIconFont");

        foreach (var tile in _tileLayout)
        {
            if (!_tileMetadata.TryGetValue(tile.Key, out var meta)) continue;

            var wrapper = new Grid
            {
                Margin = new Thickness(2),
                Tag = tile,
                ToolTip = meta.Title + " · " + meta.Tooltip,
            };

            if (tile.Key == "media")
            {
                BuildEditMediaTileContent(wrapper, tile, panelIconFont);
            }
            else
            {
                BuildEditStandardTileContent(wrapper, tile, panelIconFont, meta);
            }

            var minusBadge = CreateMinusBadge(tile);
            var resizeThumb = CreateResizeThumb(tile);

            wrapper.Children.Add(minusBadge);
            wrapper.Children.Add(resizeThumb);

            wrapper.MouseLeftButtonDown += OnTileBodyMouseDown;
            wrapper.MouseMove += OnTileBodyMouseMove;
            wrapper.MouseLeftButtonUp += OnTileBodyMouseUp;

            Grid.SetRow(wrapper, tile.Row);
            Grid.SetColumn(wrapper, tile.Column);
            Grid.SetColumnSpan(wrapper, tile.ColumnSpan);
            Grid.SetRowSpan(wrapper, tile.RowSpan);

            Panel.SetZIndex(wrapper, 10);
            EditControlsGrid.Children.Add(wrapper);
            _editTileElements[tile.Key] = wrapper;
        }

        // Configure EditBrightnessPanel and EditVolumePanel (draggable row placement only, non-resizable)
        var brightnessTile = _tileLayout.FirstOrDefault(t => t.Key == "brightness");
        if (brightnessTile != null)
        {
            brightnessTile.Column = 0;
            brightnessTile.ColumnSpan = 4;
            brightnessTile.RowSpan = 1;
            EditBrightnessPanel.Tag = brightnessTile;
            EditBrightnessPanel.Cursor = Cursors.SizeAll;
            EditBrightnessPanel.ToolTip = "Kéo để đổi vị trí thanh độ sáng";
            EditBrightnessPanel.MouseLeftButtonDown -= OnTileBodyMouseDown;
            EditBrightnessPanel.MouseLeftButtonDown += OnTileBodyMouseDown;
            EditBrightnessPanel.MouseMove -= OnTileBodyMouseMove;
            EditBrightnessPanel.MouseMove += OnTileBodyMouseMove;
            EditBrightnessPanel.MouseLeftButtonUp -= OnTileBodyMouseUp;
            EditBrightnessPanel.MouseLeftButtonUp += OnTileBodyMouseUp;
            EditBrightnessPanel.MouseEnter -= OnSliderPanelMouseEnter;
            EditBrightnessPanel.MouseEnter += OnSliderPanelMouseEnter;
            EditBrightnessPanel.MouseLeave -= OnSliderPanelMouseLeave;
            EditBrightnessPanel.MouseLeave += OnSliderPanelMouseLeave;
            Grid.SetRow(EditBrightnessPanel, brightnessTile.Row);
            Grid.SetColumn(EditBrightnessPanel, 0);
            Grid.SetColumnSpan(EditBrightnessPanel, 4);
            Grid.SetRowSpan(EditBrightnessPanel, 1);
            Panel.SetZIndex(EditBrightnessPanel, 10);
            _editTileElements["brightness"] = EditBrightnessPanel;
        }

        var volumeTile = _tileLayout.FirstOrDefault(t => t.Key == "volume");
        if (volumeTile != null)
        {
            volumeTile.Column = 0;
            volumeTile.ColumnSpan = 4;
            volumeTile.RowSpan = 1;
            EditVolumePanel.Tag = volumeTile;
            EditVolumePanel.Cursor = Cursors.SizeAll;
            EditVolumePanel.ToolTip = "Kéo để đổi vị trí thanh âm lượng";
            EditVolumePanel.MouseLeftButtonDown -= OnTileBodyMouseDown;
            EditVolumePanel.MouseLeftButtonDown += OnTileBodyMouseDown;
            EditVolumePanel.MouseMove -= OnTileBodyMouseMove;
            EditVolumePanel.MouseMove += OnTileBodyMouseMove;
            EditVolumePanel.MouseLeftButtonUp -= OnTileBodyMouseUp;
            EditVolumePanel.MouseLeftButtonUp += OnTileBodyMouseUp;
            EditVolumePanel.MouseEnter -= OnSliderPanelMouseEnter;
            EditVolumePanel.MouseEnter += OnSliderPanelMouseEnter;
            EditVolumePanel.MouseLeave -= OnSliderPanelMouseLeave;
            EditVolumePanel.MouseLeave += OnSliderPanelMouseLeave;
            Grid.SetRow(EditVolumePanel, volumeTile.Row);
            Grid.SetColumn(EditVolumePanel, 0);
            Grid.SetColumnSpan(EditVolumePanel, 4);
            Grid.SetRowSpan(EditVolumePanel, 1);
            Panel.SetZIndex(EditVolumePanel, 10);
            _editTileElements["volume"] = EditVolumePanel;
        }

        UpdateEditTilesVisual();
        BuildHiddenControlsTray();
    }

    private Grid CreateDockIconBubble(string key, FontFamily panelIconFont, (string Icon, string Title, string Tooltip, bool IsSvg) meta, double size, double iconSize = 22)
    {
        var container = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Outer ring: dock background with specular top & bottom rim light
        container.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)),
            BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.45),
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0)
                },
                new Point(0, 0), new Point(0, 1)),
            BorderThickness = new Thickness(size >= 60 ? 1.5 : 1),
            CornerRadius = new CornerRadius(999),
        });

        // Inner ring: dock inner reflection
        container.Children.Add(new Border
        {
            Margin = new Thickness(size >= 60 ? 6 : 5),
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(size >= 60 ? 3 : 2.5),
            CornerRadius = new CornerRadius(999),
        });

        if (meta.IsSvg)
        {
            Geometry? geom = null;
            if (key == "bluetooth") geom = BluetoothIcon.Data;
            else if (key == "airplane") geom = AirplaneIcon.Data;
            else if (key == "energysaver") geom = _energySaverGeometry;

            if (geom != null)
            {
                container.Children.Add(new System.Windows.Shapes.Path
                {
                    Width = iconSize,
                    Height = iconSize,
                    Stretch = Stretch.Uniform,
                    Fill = Brushes.White,
                    Data = geom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }
        else
        {
            var iconText = meta.Icon;
            if (key == "wifi" && _lastIsEthernet)
            {
                iconText = "\uE839";
            }

            container.Children.Add(new TextBlock
            {
                Text = iconText,
                FontFamily = panelIconFont,
                FontSize = iconSize,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return container;
    }

    private void BuildEditStandardTileContent(Grid wrapper, ControlTileLayout tile, FontFamily panelIconFont, (string Icon, string Title, string Tooltip, bool IsSvg) meta)
    {
        // 1. Circular View (1-grid)
        var circularView = new Grid
        {
            Width = 66, Height = 66,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "CircularView",
        };
        circularView.Children.Add(CreateDockIconBubble(tile.Key, panelIconFont, meta, size: 66, iconSize: 22));
        wrapper.Children.Add(circularView);

        // 2. Wide View (2-grid)
        var wideView = new Grid
        {
            Height = 66,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "WideView",
        };
        wideView.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)),
            BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.4),
                    new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.6),
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0)
                },
                new Point(0, 0), new Point(0, 1)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(22),
        });

        var contentGrid = new Grid { Margin = new Thickness(9, 0, 12, 0) };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBubble = CreateDockIconBubble(tile.Key, panelIconFont, meta, size: 54, iconSize: 22);
        iconBubble.Margin = new Thickness(0, 0, 10, 0);
        contentGrid.Children.Add(iconBubble);

        var titleText = meta.Title;
        var subtitleText = meta.Tooltip;
        if (tile.Key == "wifi")
        {
            titleText = WifiNameText.Text;
            subtitleText = NetworkStatusText.Text;
        }

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(textStack, 1);
        textStack.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = ForegroundBrush ?? Brushes.White,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitleText,
            FontSize = 11, Opacity = 0.82,
            Foreground = SecondaryForegroundBrush ?? Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        contentGrid.Children.Add(textStack);
        wideView.Children.Add(contentGrid);
        wrapper.Children.Add(wideView);

        // 3. Square View (4-grid 2x2 square)
        var squareView = new Grid
        {
            Height = 136,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = "SquareView",
        };
        squareView.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)),
            BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.3),
                    new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.7),
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0)
                },
                new Point(0, 0), new Point(0, 1)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(26),
        });

        var squareContentGrid = new Grid { Margin = new Thickness(14) };
        var squareIconBubble = CreateDockIconBubble(tile.Key, panelIconFont, meta, size: 54, iconSize: 22);
        squareIconBubble.HorizontalAlignment = HorizontalAlignment.Left;
        squareIconBubble.VerticalAlignment = VerticalAlignment.Top;
        squareContentGrid.Children.Add(squareIconBubble);

        var squareTextStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        squareTextStack.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = ForegroundBrush ?? Brushes.White,
        });
        squareTextStack.Children.Add(new TextBlock
        {
            Text = subtitleText,
            FontSize = 12, Opacity = 0.82,
            Foreground = SecondaryForegroundBrush ?? Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        squareContentGrid.Children.Add(squareTextStack);
        squareView.Children.Add(squareContentGrid);
        wrapper.Children.Add(squareView);
    }

    private void BuildEditMediaTileContent(Grid wrapper, ControlTileLayout tile, FontFamily panelIconFont)
    {
        var wideView = new Grid { Tag = "WideView" };
        wideView.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)),
            BorderBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.3),
                    new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.7),
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1.0)
                },
                new Point(0, 0), new Point(0, 1)),
            BorderThickness = new Thickness(1.5),
        });

        var iconBubble = new Border
        {
            Width = 52, Height = 52,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0x25, 0x34, 0x3D, 0x46)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(13),
        };
        if (_media.CurrentTrack.Thumbnail != null)
        {
            iconBubble.Background = new ImageBrush(_media.CurrentTrack.Thumbnail) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            iconBubble.Child = new TextBlock
            {
                Text = "\uE768",
                FontFamily = panelIconFont, FontSize = 23,
                Foreground = ForegroundBrush ?? Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        wideView.Children.Add(iconBubble);

        var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 13) };
        statusStack.Children.Add(new TextBlock
        {
            Text = _media.CurrentTrack.HasMedia ? _media.CurrentTrack.Title : "Không phát",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = ForegroundBrush ?? Brushes.White, Margin = new Thickness(0, 0, 0, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        statusStack.Children.Add(new TextBlock
        {
            Text = _media.CurrentTrack.HasMedia ? _media.CurrentTrack.Artist : "Không có nội dung",
            FontSize = 10, Opacity = 0.75,
            Foreground = SecondaryForegroundBrush ?? Brushes.White, Margin = new Thickness(0, 0, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var controlsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        controlsPanel.Children.Add(new TextBlock { Text = "\uE892", FontFamily = panelIconFont, FontSize = 17, Foreground = SecondaryForegroundBrush ?? Brushes.White, Margin = new Thickness(0, 0, 17, 0) });
        controlsPanel.Children.Add(new TextBlock { Text = _media.CurrentTrack.IsPlaying ? "\uE769" : "\uE768", FontFamily = panelIconFont, FontSize = 19, Foreground = ForegroundBrush ?? Brushes.White, Margin = new Thickness(0, 0, 17, 0) });
        controlsPanel.Children.Add(new TextBlock { Text = "\uE893", FontFamily = panelIconFont, FontSize = 17, Foreground = SecondaryForegroundBrush ?? Brushes.White });
        statusStack.Children.Add(controlsPanel);
        wideView.Children.Add(statusStack);

        wrapper.Children.Add(wideView);
    }

    private Border CreateMinusBadge(ControlTileLayout tile)
    {
        var minusBadge = new Border
        {
            Width = 24, Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromRgb(0x55, 0x5D, 0x64)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x92, 0x9B, 0xA3)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Cursor = Cursors.Hand,
            ToolTip = "Ẩn điều khiển này",
            Tag = "MinusBadge",
        };
        minusBadge.Child = new TextBlock
        {
            Text = "−", FontSize = 18, FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        };
        minusBadge.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            OnRemoveTileClick(tile);
        };
        return minusBadge;
    }

    private void OnRemoveTileClick(ControlTileLayout tile)
    {
        _tileLayout.Remove(tile);
        ControlTileLayout.SaveLayout(_tileLayout);
        BuildEditTiles();
        MeasureContentAndPosition();
    }

    private void BuildHiddenControlsTray()
    {
        HiddenControlsWrapPanel.Children.Clear();
        if (!_isEditingControls)
        {
            EditHiddenControlsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var defaults = ControlTileLayout.CreateDefaultLayout();
        var hiddenTiles = defaults
            .Where(d => _tileMetadata.ContainsKey(d.Key) && _tileLayout.All(t => t.Key != d.Key))
            .ToList();

        if (hiddenTiles.Count == 0)
        {
            EditHiddenControlsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var template in hiddenTiles)
        {
            var meta = _tileMetadata[template.Key];
            var pill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = $"+ {meta.Title}",
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = ForegroundBrush ?? Brushes.White,
                },
            };

            var btn = new Button
            {
                Style = (Style)FindResource("PlainIconButton"),
                Content = pill,
                ToolTip = $"Hiện lại {meta.Title}",
            };
            AutomationProperties.SetName(btn, $"Thêm lại {meta.Title}");
            btn.Click += (_, _) => OnRestoreHiddenTileClick(template);
            HiddenControlsWrapPanel.Children.Add(btn);
        }

        EditHiddenControlsPanel.Visibility = Visibility.Visible;
    }

    private void OnRestoreHiddenTileClick(ControlTileLayout template)
    {
        if (_tileLayout.Any(t => t.Key == template.Key)) return;

        var dummy = new ControlTileLayout();
        int colSpan = template.ColumnSpan;
        int rowSpan = template.RowSpan;
        var slot = FindFreeSlotInCandidate(_tileLayout, dummy, colSpan, rowSpan);
        if (!slot.HasValue && (colSpan != 1 || rowSpan != 1))
        {
            colSpan = 1;
            rowSpan = 1;
            slot = FindFreeSlotInCandidate(_tileLayout, dummy, 1, 1);
        }

        if (!slot.HasValue) return;

        _tileLayout.Add(new ControlTileLayout
        {
            Key = template.Key,
            Row = slot.Value.Row,
            Column = slot.Value.Col,
            ColumnSpan = colSpan,
            RowSpan = rowSpan,
        });
        ControlTileLayout.SaveLayout(_tileLayout);
        BuildEditTiles();
        MeasureContentAndPosition();
    }

    private void OnRestoreDefaultLayoutClick(object sender, RoutedEventArgs e)
    {
        _tileLayout = ControlTileLayout.CreateDefaultLayout();
        EnsureSlidersInLayout(_tileLayout);
        ControlTileLayout.SaveLayout(_tileLayout);
        BuildEditTiles();
        MeasureContentAndPosition();
    }

    private void UpdateEditTilesVisual()
    {
        // Update position of EditBrightnessPanel and EditVolumePanel
        var brightnessTile = _tileLayout.FirstOrDefault(t => t.Key == "brightness");
        if (brightnessTile != null && _editTileElements.TryGetValue("brightness", out var bElem))
        {
            Grid.SetRow(bElem, brightnessTile.Row);
            Grid.SetColumn(bElem, 0);
            Grid.SetColumnSpan(bElem, 4);
            Grid.SetRowSpan(bElem, 1);
        }

        var volumeTile = _tileLayout.FirstOrDefault(t => t.Key == "volume");
        if (volumeTile != null && _editTileElements.TryGetValue("volume", out var vElem))
        {
            Grid.SetRow(vElem, volumeTile.Row);
            Grid.SetColumn(vElem, 0);
            Grid.SetColumnSpan(vElem, 4);
            Grid.SetRowSpan(vElem, 1);
        }

        // Dynamically show empty placeholder slots, hide occupied slots
        UpdatePlaceholderVisibility();

        foreach (var tile in _tileLayout)
        {
            if (!_editTileElements.TryGetValue(tile.Key, out var elem) || elem is not Grid wrapper)
                continue;

            Grid.SetRow(wrapper, tile.Row);
            Grid.SetColumn(wrapper, tile.Column);
            Grid.SetColumnSpan(wrapper, tile.ColumnSpan);
            Grid.SetRowSpan(wrapper, tile.RowSpan);

            var circularView = wrapper.Children.OfType<Grid>().FirstOrDefault(g => (string?)g.Tag == "CircularView");
            var wideView = wrapper.Children.OfType<Grid>().FirstOrDefault(g => (string?)g.Tag == "WideView");
            var squareView = wrapper.Children.OfType<Grid>().FirstOrDefault(g => (string?)g.Tag == "SquareView");
            var minusBadge = wrapper.Children.OfType<Border>().FirstOrDefault(b => (string?)b.Tag == "MinusBadge");
            var resizeThumb = wrapper.Children.OfType<Thumb>().FirstOrDefault();

            if (tile.Key == "media")
            {
                wrapper.Width = double.NaN;
                wrapper.Height = double.NaN;
                wrapper.HorizontalAlignment = HorizontalAlignment.Stretch;
                wrapper.VerticalAlignment = VerticalAlignment.Stretch;
                if (wideView != null) wideView.Visibility = Visibility.Visible;
                if (minusBadge != null) minusBadge.Margin = new Thickness(0, 0, 0, 0);
                if (resizeThumb != null) resizeThumb.Margin = new Thickness(0, 0, 0, 0);
            }
            else if (tile.RowSpan >= 2 && tile.ColumnSpan >= 2) // 4-grid square
            {
                wrapper.Width = double.NaN;
                wrapper.Height = double.NaN;
                wrapper.HorizontalAlignment = HorizontalAlignment.Stretch;
                wrapper.VerticalAlignment = VerticalAlignment.Stretch;
                if (circularView != null) circularView.Visibility = Visibility.Collapsed;
                if (wideView != null) wideView.Visibility = Visibility.Collapsed;
                if (squareView != null) squareView.Visibility = Visibility.Visible;
                if (minusBadge != null) minusBadge.Margin = new Thickness(0, 0, 0, 0);
                if (resizeThumb != null) resizeThumb.Margin = new Thickness(0, 0, 0, 0);
            }
            else if (tile.ColumnSpan >= 2) // 2-grid capsule
            {
                wrapper.Width = double.NaN;
                wrapper.Height = double.NaN;
                wrapper.HorizontalAlignment = HorizontalAlignment.Stretch;
                wrapper.VerticalAlignment = VerticalAlignment.Stretch;
                if (circularView != null) circularView.Visibility = Visibility.Collapsed;
                if (wideView != null) wideView.Visibility = Visibility.Visible;
                if (squareView != null) squareView.Visibility = Visibility.Collapsed;
                if (minusBadge != null) minusBadge.Margin = new Thickness(0, 0, 0, 0);
                if (resizeThumb != null) resizeThumb.Margin = new Thickness(0, 0, 0, 0);
            }
            else // 1-grid circle
            {
                wrapper.Width = 66;
                wrapper.Height = 66;
                wrapper.HorizontalAlignment = HorizontalAlignment.Center;
                wrapper.VerticalAlignment = VerticalAlignment.Center;
                if (circularView != null) circularView.Visibility = Visibility.Visible;
                if (wideView != null) wideView.Visibility = Visibility.Collapsed;
                if (squareView != null) squareView.Visibility = Visibility.Collapsed;
                if (minusBadge != null) minusBadge.Margin = new Thickness(-2, -2, 0, 0);
                if (resizeThumb != null) resizeThumb.Margin = new Thickness(0, 0, -2, -2);
            }
        }
    }

    private void InitializePlaceholderGrid()
    {
        EditPlaceholdersGrid.Children.Clear();
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                var placeholder = new Border
                {
                    Width = 66,
                    Height = 66,
                    CornerRadius = new CornerRadius(33),
                    Background = new SolidColorBrush(Color.FromArgb(0x25, 0x3A, 0x41, 0x49)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "+",
                        FontSize = 18,
                        FontWeight = FontWeights.Light,
                        Foreground = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, -1, 0, 0)
                    }
                };
                Grid.SetRow(placeholder, r);
                Grid.SetColumn(placeholder, c);
                _placeholderCells[r, c] = placeholder;
                EditPlaceholdersGrid.Children.Add(placeholder);
            }
        }
    }

    private bool IsCellOccupied(int row, int col)
    {
        foreach (var tile in _tileLayout)
        {
            if (row >= tile.Row && row < tile.Row + tile.RowSpan &&
                col >= tile.Column && col < tile.Column + tile.ColumnSpan)
            {
                return true;
            }
        }
        return false;
    }

    private void UpdatePlaceholderVisibility()
    {
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                bool isOccupied = IsCellOccupied(r, c);
                if (_placeholderCells[r, c] != null)
                {
                    _placeholderCells[r, c].Visibility = isOccupied ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
    }

    // ────────────────────────────────────────────────
    //  Tile body drag-to-move
    // ────────────────────────────────────────────────

    private ControlTileLayout? _movingTile;
    private Point _moveDragStart;

    private void OnTileBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement elem || elem.Tag is not ControlTileLayout tile) return;
        _movingTile = tile;
        _moveDragStart = e.GetPosition(EditControlsGrid);
        elem.CaptureMouse();
        elem.Opacity = 0.75;
        e.Handled = true;
    }

    private void OnTileBodyMouseMove(object sender, MouseEventArgs e)
    {
        if (_movingTile == null || e.LeftButton != MouseButtonState.Pressed) return;
        var currentPos = e.GetPosition(EditControlsGrid);
        var deltaX = currentPos.X - _moveDragStart.X;
        var deltaY = currentPos.Y - _moveDragStart.Y;

        if (IsSlider(_movingTile))
        {
            if (Math.Abs(deltaY) > 20)
            {
                int targetRow = Math.Clamp((int)(currentPos.Y / 70.0), 0, 7);
                if (targetRow != _movingTile.Row)
                {
                    TryMoveSlider(_movingTile, targetRow);
                    _moveDragStart = currentPos;
                }
            }
        }
        else
        {
            if (Math.Abs(deltaX) > 20 || Math.Abs(deltaY) > 20)
            {
                int targetCol = Math.Clamp((int)(currentPos.X / 81.5), 0, 4 - _movingTile.ColumnSpan);
                int targetRow = Math.Clamp((int)(currentPos.Y / 70.0), 0, 8 - _movingTile.RowSpan);

                // Cannot place onto or across any row occupied by a slider
                bool hitsSlider = false;
                for (int span = 0; span < _movingTile.RowSpan; span++)
                {
                    if (IsRowOccupiedBySlider(targetRow + span))
                    {
                        hitsSlider = true;
                        break;
                    }
                }
                if (hitsSlider) return;

                if (targetRow != _movingTile.Row || targetCol != _movingTile.Column)
                {
                    if (TryMoveTile(_movingTile, targetRow, targetCol))
                    {
                        _moveDragStart = currentPos;
                        UpdateEditTilesVisual();
                    }
                }
            }
        }
        e.Handled = true;
    }

    private void OnTileBodyMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem)
        {
            elem.ReleaseMouseCapture();
            elem.Opacity = 1.0;
            if (elem is Border border)
            {
                border.BorderBrush = _sliderNormalBorder;
                border.Background = _sliderNormalBackground;
            }
        }

        if (_movingTile != null)
        {
            SanitizeLayout();
            ControlTileLayout.SaveLayout(_tileLayout);
            _movingTile = null;
            UpdateEditTilesVisual();
        }
        e.Handled = true;
    }

    private void ClearEditTiles()
    {
        EditBrightnessPanel.Opacity = 1.0;
        EditBrightnessPanel.BorderBrush = _sliderNormalBorder;
        EditBrightnessPanel.Background = _sliderNormalBackground;
        EditVolumePanel.Opacity = 1.0;
        EditVolumePanel.BorderBrush = _sliderNormalBorder;
        EditVolumePanel.Background = _sliderNormalBackground;

        var toRemove = new List<UIElement>();
        foreach (UIElement child in EditControlsGrid.Children)
        {
            if (ReferenceEquals(child, EditPlaceholdersGrid) ||
                child is UniformGrid ||
                (child is Border b && (b.Name == "EditBrightnessPanel" || b.Name == "EditVolumePanel")))
                continue;
            toRemove.Add(child);
        }
        foreach (var element in toRemove)
            EditControlsGrid.Children.Remove(element);
        _editTileElements.Clear();
    }

    // ────────────────────────────────────────────────
    //  Synchronize normal panel from _tileLayout
    // ────────────────────────────────────────────────

    private void UpdateNormalButtonAppearance(Button button, Border iconBubble, FrameworkElement textPanel, int colSpan, int rowSpan)
    {
        if (colSpan == 1 && rowSpan == 1) // 1-grid circle: icon in exact center
        {
            button.Width = 66;
            button.Height = 66;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(2);
            button.Padding = new Thickness(0);
            button.Style = (Style)FindResource("CircularQuickTileButton");
            iconBubble.Width = double.NaN;
            iconBubble.Height = double.NaN;
            iconBubble.Background = Brushes.Transparent;
            iconBubble.Margin = new Thickness(0);
            Grid.SetColumn(iconBubble, 0);
            Grid.SetColumnSpan(iconBubble, 2);
            iconBubble.HorizontalAlignment = HorizontalAlignment.Center;
            iconBubble.VerticalAlignment = VerticalAlignment.Center;
            textPanel.Visibility = Visibility.Collapsed;
        }
        else if (rowSpan == 1) // 2-grid capsule
        {
            button.Width = double.NaN;
            button.Height = 66;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(2);
            button.Padding = new Thickness(10, 8, 12, 8);
            button.Style = (Style)FindResource("QuickTileButton");
            button.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49));
            iconBubble.Width = 52;
            iconBubble.Height = 52;
            iconBubble.CornerRadius = new CornerRadius(26);
            iconBubble.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            iconBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            iconBubble.BorderThickness = new Thickness(2);
            iconBubble.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(iconBubble, 0);
            Grid.SetColumnSpan(iconBubble, 1);
            iconBubble.HorizontalAlignment = HorizontalAlignment.Left;
            iconBubble.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(textPanel, 1);
            Grid.SetColumnSpan(textPanel, 1);
            textPanel.Visibility = Visibility.Visible;
            textPanel.VerticalAlignment = VerticalAlignment.Center;
            textPanel.HorizontalAlignment = HorizontalAlignment.Left;
            textPanel.Margin = new Thickness(0);
        }
        else // 4-grid square! (2x2)
        {
            button.Width = double.NaN;
            button.Height = 136;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Margin = new Thickness(2);
            button.Padding = new Thickness(14);
            button.Style = (Style)FindResource("SquareQuickTileButton");
            button.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49));
            iconBubble.Width = 52;
            iconBubble.Height = 52;
            iconBubble.CornerRadius = new CornerRadius(26);
            iconBubble.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            iconBubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            iconBubble.BorderThickness = new Thickness(2);
            iconBubble.Margin = new Thickness(0);
            Grid.SetColumn(iconBubble, 0);
            Grid.SetColumnSpan(iconBubble, 2);
            iconBubble.HorizontalAlignment = HorizontalAlignment.Left;
            iconBubble.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(textPanel, 0);
            Grid.SetColumnSpan(textPanel, 2);
            textPanel.Visibility = Visibility.Visible;
            textPanel.VerticalAlignment = VerticalAlignment.Bottom;
            textPanel.HorizontalAlignment = HorizontalAlignment.Left;
            textPanel.Margin = new Thickness(0);
        }
    }

    /// <summary>
    /// Synchronizes the NormalControlsPanel controls with the saved layout.
    /// </summary>
    private void ApplyLayoutToNormalPanel()
    {
        WifiQuickButton.Visibility = Visibility.Collapsed;
        BluetoothButton.Visibility = Visibility.Collapsed;
        AirplaneButton.Visibility = Visibility.Collapsed;
        DisplayProjectionButton.Visibility = Visibility.Collapsed;
        NightLightButton.Visibility = Visibility.Collapsed;
        EnergySaverButton.Visibility = Visibility.Collapsed;
        BatteryQuickButton.Visibility = Visibility.Collapsed;
        CalculatorQuickButton.Visibility = Visibility.Collapsed;
        ClockQuickButton.Visibility = Visibility.Collapsed;
        CameraQuickButton.Visibility = Visibility.Collapsed;
        MediaPlayerBorder.Visibility = Visibility.Collapsed;

        int maxRow = _tileLayout.Count > 0 ? _tileLayout.Max(t => t.Row + t.RowSpan - 1) : 5;
        maxRow = Math.Max(maxRow, 4);
        for (int r = 0; r < 8; r++)
        {
            NormalControlsPanel.RowDefinitions[r].Height = (r <= maxRow) ? new GridLength(70) : new GridLength(0);
        }

        foreach (var tile in _tileLayout)
        {
            switch (tile.Key)
            {
                case "wifi":
                    WifiQuickButton.Visibility = Visibility.Visible;
                    Grid.SetRow(WifiQuickButton, tile.Row);
                    Grid.SetColumn(WifiQuickButton, tile.Column);
                    Grid.SetColumnSpan(WifiQuickButton, tile.ColumnSpan);
                    Grid.SetRowSpan(WifiQuickButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(WifiQuickButton, WifiIconBubble, WifiTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "bluetooth":
                    BluetoothButton.Visibility = Visibility.Visible;
                    Grid.SetRow(BluetoothButton, tile.Row);
                    Grid.SetColumn(BluetoothButton, tile.Column);
                    Grid.SetColumnSpan(BluetoothButton, tile.ColumnSpan);
                    Grid.SetRowSpan(BluetoothButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(BluetoothButton, BluetoothIconBubble, BluetoothTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "airplane":
                    AirplaneButton.Visibility = Visibility.Visible;
                    Grid.SetRow(AirplaneButton, tile.Row);
                    Grid.SetColumn(AirplaneButton, tile.Column);
                    Grid.SetColumnSpan(AirplaneButton, tile.ColumnSpan);
                    Grid.SetRowSpan(AirplaneButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(AirplaneButton, AirplaneIconBubble, AirplaneTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "projection":
                    DisplayProjectionButton.Visibility = Visibility.Visible;
                    Grid.SetRow(DisplayProjectionButton, tile.Row);
                    Grid.SetColumn(DisplayProjectionButton, tile.Column);
                    Grid.SetColumnSpan(DisplayProjectionButton, tile.ColumnSpan);
                    Grid.SetRowSpan(DisplayProjectionButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(DisplayProjectionButton, DisplayProjectionIconBubble, DisplayProjectionTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "nightlight":
                    NightLightButton.Visibility = Visibility.Visible;
                    Grid.SetRow(NightLightButton, tile.Row);
                    Grid.SetColumn(NightLightButton, tile.Column);
                    Grid.SetColumnSpan(NightLightButton, tile.ColumnSpan);
                    Grid.SetRowSpan(NightLightButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(NightLightButton, NightLightIconBubble, NightLightTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "energysaver":
                    EnergySaverButton.Visibility = Visibility.Visible;
                    Grid.SetRow(EnergySaverButton, tile.Row);
                    Grid.SetColumn(EnergySaverButton, tile.Column);
                    Grid.SetColumnSpan(EnergySaverButton, tile.ColumnSpan);
                    Grid.SetRowSpan(EnergySaverButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(EnergySaverButton, EnergySaverIconBubble, EnergySaverTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "battery":
                    BatteryQuickButton.Visibility = Visibility.Visible;
                    Grid.SetRow(BatteryQuickButton, tile.Row);
                    Grid.SetColumn(BatteryQuickButton, tile.Column);
                    Grid.SetColumnSpan(BatteryQuickButton, tile.ColumnSpan);
                    Grid.SetRowSpan(BatteryQuickButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(BatteryQuickButton, BatteryIconBubble, BatteryTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "calculator":
                    CalculatorQuickButton.Visibility = Visibility.Visible;
                    Grid.SetRow(CalculatorQuickButton, tile.Row);
                    Grid.SetColumn(CalculatorQuickButton, tile.Column);
                    Grid.SetColumnSpan(CalculatorQuickButton, tile.ColumnSpan);
                    Grid.SetRowSpan(CalculatorQuickButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(CalculatorQuickButton, CalculatorIconBubble, CalculatorTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "clock":
                    ClockQuickButton.Visibility = Visibility.Visible;
                    Grid.SetRow(ClockQuickButton, tile.Row);
                    Grid.SetColumn(ClockQuickButton, tile.Column);
                    Grid.SetColumnSpan(ClockQuickButton, tile.ColumnSpan);
                    Grid.SetRowSpan(ClockQuickButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(ClockQuickButton, ClockIconBubble, ClockTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "camera":
                    CameraQuickButton.Visibility = Visibility.Visible;
                    Grid.SetRow(CameraQuickButton, tile.Row);
                    Grid.SetColumn(CameraQuickButton, tile.Column);
                    Grid.SetColumnSpan(CameraQuickButton, tile.ColumnSpan);
                    Grid.SetRowSpan(CameraQuickButton, tile.RowSpan);
                    UpdateNormalButtonAppearance(CameraQuickButton, CameraIconBubble, CameraTextPanel, tile.ColumnSpan, tile.RowSpan);
                    break;
                case "media":
                    MediaPlayerBorder.Visibility = Visibility.Visible;
                    Grid.SetRow(MediaPlayerBorder, tile.Row);
                    Grid.SetColumn(MediaPlayerBorder, tile.Column);
                    Grid.SetColumnSpan(MediaPlayerBorder, tile.ColumnSpan);
                    Grid.SetRowSpan(MediaPlayerBorder, tile.RowSpan);
                    break;
                case "brightness":
                    Grid.SetRow(NormalBrightnessPanel, tile.Row);
                    Grid.SetColumn(NormalBrightnessPanel, 0);
                    Grid.SetColumnSpan(NormalBrightnessPanel, 4);
                    Grid.SetRowSpan(NormalBrightnessPanel, 1);
                    break;
                case "volume":
                    Grid.SetRow(NormalVolumePanel, tile.Row);
                    Grid.SetColumn(NormalVolumePanel, 0);
                    Grid.SetColumnSpan(NormalVolumePanel, 4);
                    Grid.SetRowSpan(NormalVolumePanel, 1);
                    break;
            }
        }

        ApplyWifiTileState(_lastWifiConnected, _lastWifiStatus, _lastIsEthernet);
        ApplyBluetoothState(_lastBluetoothState);
        ApplyAirplaneModeState(_lastAirplaneState);
        ApplyNightLightState(_lastNightLightState);
        ApplyEnergySaverState(_lastEnergySaverState);
    }

    private static bool HasAnyOverlap(List<ControlTileLayout> layout)
    {
        for (int i = 0; i < layout.Count; i++)
        {
            for (int j = i + 1; j < layout.Count; j++)
            {
                if (TilesOverlap(layout[i], layout[j]))
                    return true;
            }
        }
        return false;
    }

    private static bool TilesOverlap(ControlTileLayout a, ControlTileLayout b)
    {
        return a.Row < b.Row + b.RowSpan && a.Row + a.RowSpan > b.Row &&
               a.Column < b.Column + b.ColumnSpan && a.Column + a.ColumnSpan > b.Column;
    }

    private (int Row, int Col)? FindFreeSlot(int colSpan, int rowSpan, ControlTileLayout excludeTile) =>
        FindFreeSlotInCandidate(_tileLayout, excludeTile, colSpan, rowSpan);

    private static (int Row, int Col)? FindFreeSlotInCandidate(List<ControlTileLayout> layout, ControlTileLayout excludeTile, int colSpan, int rowSpan)
    {
        for (int r = 0; r <= 8 - rowSpan; r++)
        {
            bool hitsSlider = false;
            for (int span = 0; span < rowSpan; span++)
            {
                int checkRow = r + span;
                if (layout.Any(t => IsSlider(t) && !ReferenceEquals(t, excludeTile) && t.Row == checkRow))
                {
                    hitsSlider = true;
                    break;
                }
            }
            if (hitsSlider) continue;

            for (int c = 0; c <= 4 - colSpan; c++)
            {
                var testTile = new ControlTileLayout { Row = r, Column = c, ColumnSpan = colSpan, RowSpan = rowSpan };
                bool overlaps = layout.Any(other =>
                    !ReferenceEquals(other, excludeTile) &&
                    TilesOverlap(other, testTile));

                if (!overlaps)
                    return (r, c);
            }
        }
        return null;
    }

    private static bool ReflowCandidateTiles(List<ControlTileLayout> layout, ControlTileLayout anchorTile, int preferredVacatedRow, int preferredVacatedCol)
    {
        var queue = new Queue<ControlTileLayout>();
        var overlapping = layout.Where(t => !ReferenceEquals(t, anchorTile) && !IsSlider(t) && TilesOverlap(t, anchorTile)).ToList();
        foreach (var t in overlapping) queue.Enqueue(t);

        int maxIterations = 20;
        while (queue.Count > 0 && maxIterations-- > 0)
        {
            var tileToMove = queue.Dequeue();

            // First: test if tileToMove fits cleanly in the preferred vacated slot
            bool placedInVacated = false;
            if (preferredVacatedRow >= 0 && preferredVacatedRow <= 8 - tileToMove.RowSpan &&
                preferredVacatedCol >= 0 && preferredVacatedCol <= 4 - tileToMove.ColumnSpan)
            {
                var testVacated = new ControlTileLayout
                {
                    Row = preferredVacatedRow,
                    Column = preferredVacatedCol,
                    ColumnSpan = tileToMove.ColumnSpan,
                    RowSpan = tileToMove.RowSpan
                };

                bool sliderConflict = false;
                for (int s = 0; s < tileToMove.RowSpan; s++)
                {
                    if (layout.Any(l => IsSlider(l) && l.Row == preferredVacatedRow + s))
                    {
                        sliderConflict = true;
                        break;
                    }
                }

                if (!sliderConflict && !layout.Any(other => !ReferenceEquals(other, tileToMove) && TilesOverlap(other, testVacated)))
                {
                    tileToMove.Row = preferredVacatedRow;
                    tileToMove.Column = preferredVacatedCol;
                    placedInVacated = true;
                }
            }

            if (!placedInVacated)
            {
                var freeSlot = FindFreeSlotInCandidate(layout, tileToMove, tileToMove.ColumnSpan, tileToMove.RowSpan);
                if (freeSlot.HasValue)
                {
                    tileToMove.Row = freeSlot.Value.Row;
                    tileToMove.Column = freeSlot.Value.Col;

                    var newCollisions = layout.Where(t =>
                        !ReferenceEquals(t, tileToMove) &&
                        !ReferenceEquals(t, anchorTile) &&
                        !IsSlider(t) &&
                        TilesOverlap(t, tileToMove)).ToList();

                    foreach (var c in newCollisions)
                    {
                        if (!queue.Contains(c)) queue.Enqueue(c);
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        return queue.Count == 0 && !HasAnyOverlap(layout);
    }

    private void ApplyCandidateLayout(List<ControlTileLayout> candidate)
    {
        foreach (var cand in candidate)
        {
            var real = _tileLayout.FirstOrDefault(t => t.Key == cand.Key);
            if (real != null)
            {
                real.Row = cand.Row;
                real.Column = cand.Column;
                real.ColumnSpan = cand.ColumnSpan;
                real.RowSpan = cand.RowSpan;
            }
        }
    }

    private bool TryMoveTile(ControlTileLayout movingTile, int targetRow, int targetCol)
    {
        int oldRow = movingTile.Row;
        int oldCol = movingTile.Column;

        if (oldRow == targetRow && oldCol == targetCol) return false;

        var candidateLayout = _tileLayout.Select(t => t.Clone()).ToList();
        var candMoving = candidateLayout.First(t => t.Key == movingTile.Key);

        candMoving.Row = targetRow;
        candMoving.Column = targetCol;

        var collisions = candidateLayout.Where(t =>
            !ReferenceEquals(t, candMoving) &&
            !IsSlider(t) &&
            TilesOverlap(t, candMoving)).ToList();

        if (collisions.Count == 0)
        {
            ApplyCandidateLayout(candidateLayout);
            return true;
        }

        // Direct 1-to-1 swap if sizes are identical
        if (collisions.Count == 1)
        {
            var other = collisions[0];
            if (other.ColumnSpan == candMoving.ColumnSpan && other.RowSpan == candMoving.RowSpan)
            {
                other.Row = oldRow;
                other.Column = oldCol;

                if (!HasAnyOverlap(candidateLayout))
                {
                    ApplyCandidateLayout(candidateLayout);
                    return true;
                }
            }
        }

        // General reflow
        bool reflowSuccess = ReflowCandidateTiles(candidateLayout, candMoving, oldRow, oldCol);
        if (reflowSuccess && !HasAnyOverlap(candidateLayout))
        {
            ApplyCandidateLayout(candidateLayout);
            return true;
        }

        return false;
    }

    private bool TryResizeTile(ControlTileLayout resizingTile, int targetCol, int targetRow, int targetColSpan, int targetRowSpan)
    {
        if (resizingTile.Column == targetCol &&
            resizingTile.Row == targetRow &&
            resizingTile.ColumnSpan == targetColSpan &&
            resizingTile.RowSpan == targetRowSpan)
        {
            return false;
        }

        for (int span = 0; span < targetRowSpan; span++)
        {
            if (IsRowOccupiedBySlider(targetRow + span))
                return false;
        }

        var candidateLayout = _tileLayout.Select(t => t.Clone()).ToList();
        var candResizing = candidateLayout.First(t => t.Key == resizingTile.Key);

        int oldRow = candResizing.Row;
        int oldCol = candResizing.Column;

        candResizing.Column = targetCol;
        candResizing.Row = targetRow;
        candResizing.ColumnSpan = targetColSpan;
        candResizing.RowSpan = targetRowSpan;

        var collisions = candidateLayout.Where(t =>
            !ReferenceEquals(t, candResizing) &&
            !IsSlider(t) &&
            TilesOverlap(t, candResizing)).ToList();

        if (collisions.Count == 0)
        {
            ApplyCandidateLayout(candidateLayout);
            return true;
        }

        bool reflowSuccess = ReflowCandidateTiles(candidateLayout, candResizing, oldRow, oldCol);
        if (reflowSuccess && !HasAnyOverlap(candidateLayout))
        {
            ApplyCandidateLayout(candidateLayout);
            return true;
        }

        return false;
    }

    private void TryMoveSlider(ControlTileLayout movingSlider, int targetRow)
    {
        var otherSliderKey = movingSlider.Key == "brightness" ? "volume" : "brightness";
        var otherSlider = _tileLayout.FirstOrDefault(t => t.Key == otherSliderKey);

        var candidateLayout = _tileLayout.Select(t => t.Clone()).ToList();
        var candMoving = candidateLayout.First(t => t.Key == movingSlider.Key);
        var candOther = candidateLayout.FirstOrDefault(t => t.Key == otherSliderKey);

        int oldRow = candMoving.Row;

        if (candOther != null && candOther.Row == targetRow)
        {
            candOther.Row = oldRow;
            candMoving.Row = targetRow;
            ApplyCandidateLayout(candidateLayout);
            UpdateEditTilesVisual();
            return;
        }

        candMoving.Row = targetRow;

        var displaced = candidateLayout.Where(t => !IsSlider(t) && t.Row == targetRow).ToList();
        foreach (var d in displaced)
        {
            d.Row = oldRow;
        }

        if (!HasAnyOverlap(candidateLayout))
        {
            ApplyCandidateLayout(candidateLayout);
            UpdateEditTilesVisual();
        }
        else if (ReflowCandidateTiles(candidateLayout, candMoving, oldRow, 0) && !HasAnyOverlap(candidateLayout))
        {
            ApplyCandidateLayout(candidateLayout);
            UpdateEditTilesVisual();
        }
    }

    private void SanitizeLayout()
    {
        EnsureSlidersInLayout(_tileLayout);

        foreach (var tile in _tileLayout)
        {
            if (IsSlider(tile))
            {
                tile.Column = 0;
                tile.ColumnSpan = 4;
                tile.RowSpan = 1;
                tile.Row = Math.Clamp(tile.Row, 0, 7);
                continue;
            }

            tile.ColumnSpan = Math.Clamp(tile.ColumnSpan, 1, 2);
            tile.RowSpan = Math.Clamp(tile.RowSpan, 1, 2);
            tile.Column = Math.Clamp(tile.Column, 0, 4 - tile.ColumnSpan);
            tile.Row = Math.Clamp(tile.Row, 0, 8 - tile.RowSpan);
        }

        bool resolvedAny;
        int pass = 0;
        do
        {
            resolvedAny = false;
            pass++;
            for (int i = 0; i < _tileLayout.Count; i++)
            {
                var a = _tileLayout[i];
                for (int j = i + 1; j < _tileLayout.Count; j++)
                {
                    var b = _tileLayout[j];
                    if (TilesOverlap(a, b))
                    {
                        resolvedAny = true;
                        var tileToMove = IsSlider(a) ? b : (IsSlider(b) ? a : b);
                        var other = ReferenceEquals(tileToMove, a) ? b : a;

                        var slot = FindFreeSlotInCandidate(_tileLayout, tileToMove, tileToMove.ColumnSpan, tileToMove.RowSpan);
                        if (slot.HasValue)
                        {
                            tileToMove.Row = slot.Value.Row;
                            tileToMove.Column = slot.Value.Col;
                        }
                        else
                        {
                            tileToMove.ColumnSpan = 1;
                            tileToMove.RowSpan = 1;
                            var slot1x1 = FindFreeSlotInCandidate(_tileLayout, tileToMove, 1, 1);
                            if (slot1x1.HasValue)
                            {
                                tileToMove.Row = slot1x1.Value.Row;
                                tileToMove.Column = slot1x1.Value.Col;
                            }
                            else if (!IsSlider(other))
                            {
                                other.ColumnSpan = 1;
                                other.RowSpan = 1;
                                var otherSlot = FindFreeSlotInCandidate(_tileLayout, other, 1, 1);
                                if (otherSlot.HasValue)
                                {
                                    other.Row = otherSlot.Value.Row;
                                    other.Column = otherSlot.Value.Col;
                                }
                            }
                        }
                    }
                }
            }
        } while (resolvedAny && pass < 20);
    }

    // ────────────────────────────────────────────────
    //  Media Player Control & State
    // ────────────────────────────────────────────────

    private void OnMediaTrackChanged(MediaTrackInfo track)
    {
        if (_disposed || _isClosing) return;
        Dispatcher.InvokeAsync(() => ApplyMediaState(track));
    }

    private void ApplyMediaState(MediaTrackInfo track)
    {
        if (_disposed || _isClosing) return;

        MediaTitleText.Text = track.Title;
        MediaArtistText.Text = track.Artist;

        if (track.Thumbnail != null)
        {
            MediaThumbnailBorder.Background = new ImageBrush(track.Thumbnail)
            {
                Stretch = Stretch.UniformToFill
            };
            MediaFallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaThumbnailBorder.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x8D, 0xB5, 0xFF));
            MediaFallbackIcon.Visibility = Visibility.Visible;
        }

        MediaPlayPauseIcon.Text = track.IsPlaying ? "\uE769" : "\uE768";
        MediaPlayPauseButton.ToolTip = track.IsPlaying ? "Tạm dừng" : "Phát";

        MediaPrevButton.IsEnabled = track.CanSkipPrevious;
        MediaPrevButton.Opacity = track.CanSkipPrevious ? 1.0 : 0.45;

        MediaNextButton.IsEnabled = track.CanSkipNext;
        MediaNextButton.Opacity = track.CanSkipNext ? 1.0 : 0.45;

        MediaPlayPauseButton.IsEnabled = track.CanPlayPause;
        MediaPlayPauseButton.Opacity = track.CanPlayPause ? 1.0 : 0.45;

        MediaPlayerBorder.ToolTip = track.HasMedia ? $"{track.Title} · {track.Artist}" : "Không có phương tiện đang phát";
    }

    private async void OnMediaPlayPauseButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _media.TogglePlayPauseAsync();
    }

    private async void OnMediaPrevButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _media.SkipPreviousAsync();
    }

    private async void OnMediaNextButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _media.SkipNextAsync();
    }

    // ────────────────────────────────────────────────
    //  Bottom Shortcut Click Handlers
    // ────────────────────────────────────────────────

    private void OnBatteryQuickButtonClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true }); } catch { }
    }

    private void OnCalculatorQuickButtonClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("calc.exe") { UseShellExecute = true }); } catch { }
    }

    private void OnClockQuickButtonClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-clock:") { UseShellExecute = true }); }
        catch { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("timedate.cpl") { UseShellExecute = true }); } catch { } }
    }

    private void OnCameraQuickButtonClick(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true }); } catch { }
    }

    private void OnPowerButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
        var powerDialog = new PowerDialogWindow();
        powerDialog.PrepareForShow();
        powerDialog.Show();
        powerDialog.Activate();
    }

    private void OnWindowsSettingsButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnNightLightStrengthSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingNightLightStrength || !NightLightStrengthSlider.IsEnabled) return;
        _pendingNightLightStrength = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        UpdateNightLightStrengthValueText(_pendingNightLightStrength);
        if (_lastNightLightState == true || NightLightDisplayEffect.IsActive)
        {
            NightLightDisplayEffect.Apply(_pendingNightLightStrength);
        }
        Interlocked.Increment(ref _nightLightStrengthRequestVersion);
        _nightLightStrengthDebounceTimer.Stop();
        _nightLightStrengthDebounceTimer.Start();
    }

    private async void OnNightLightStrengthDebounceTimerTick(object? sender, EventArgs e)
    {
        _nightLightStrengthDebounceTimer.Stop();
        if (_isDisposedOrClosing() || (Mouse.LeftButton == MouseButtonState.Pressed && NightLightStrengthSlider.IsMouseCaptureWithin)) return;
        var value = _pendingNightLightStrength;
        var requestVersion = Volatile.Read(ref _nightLightStrengthRequestVersion);
        await _nightLightStrengthWriteTask;
        if (_isDisposedOrClosing() || requestVersion != Volatile.Read(ref _nightLightStrengthRequestVersion)) return;
        _nightLightStrengthWriteTask = ApplyNightLightStrengthAsync(value, requestVersion);
        await _nightLightStrengthWriteTask;
    }

    private void OnNightLightStrengthDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_disposed || _isClosing || _loadingNightLightStrength || !NightLightStrengthSlider.IsEnabled) return;
        _nightLightStrengthDebounceTimer.Stop();
        _nightLightStrengthDebounceTimer.Start();
    }

    private async Task ApplyNightLightStrengthAsync(int strength, int requestVersion)
    {
        var applied = await _nightLightStrength.SetStrengthAsync(strength);
        if (_isDisposedOrClosing() || requestVersion != Volatile.Read(ref _nightLightStrengthRequestVersion)) return;
        if (!applied)
        {
            NightLightStrengthStatusText.Text = _nightLightStrength.Error ?? "Không thể đổi cường độ Night light.";
            NightLightStrengthSlider.ToolTip = NightLightStrengthStatusText.Text;
            return;
        }
        NightLightStrengthStatusText.Text = "Đã đồng bộ với Night light của Windows";
    }

    private void OnNightLightStrengthChanged(int strength)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyNightLightStrength(strength);
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() => ApplyNightLightStrength(strength)));
    }

    private void ApplyNightLightStrength(int strength)
    {
        if (NightLightStrengthSlider.IsMouseCaptureWithin && Mouse.LeftButton == MouseButtonState.Pressed) return;
        _loadingNightLightStrength = true;
        NightLightStrengthSlider.Value = strength;
        NightLightStrengthSlider.IsEnabled = true;
        NightLightStrengthSlider.ToolTip = "Cường độ Night light của Windows";
        UpdateNightLightStrengthValueText(strength);
        NightLightStrengthStatusText.Text = "Đã đồng bộ với Night light của Windows";
        _loadingNightLightStrength = false;
    }

    private void UpdateNightLightStrengthValueText(int strength)
    {
        var kelvin = 6500 - (int)Math.Round(Math.Clamp(strength, 0, 100) * 53.0);
        NightLightStrengthValueText.Text = $"{strength}% · {kelvin} K";
    }

    private bool _isDisposedOrClosing() => _disposed || _isClosing;

    private void OpenWifiFlyout()
    {
        if (_wifiFlyoutWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var flyout = new WifiFlyoutWindow(_dock);
        _wifiFlyoutWindow = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_wifiFlyoutWindow, flyout))
                _wifiFlyoutWindow = null;
        };

        try
        {
            flyout.PrepareForShow();
            flyout.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Wi-Fi] Could not open networks panel: {ex}");
            _wifiFlyoutWindow = null;
        }
    }

    private void OnBluetoothStateChanged(BluetoothQuickActionState state)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyBluetoothState(state);
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() => ApplyBluetoothState(state)));
    }

    private void ApplyBluetoothState(BluetoothQuickActionState state)
    {
        _lastBluetoothState = state;
        var resourceName = state switch
        {
            BluetoothQuickActionState.Off or BluetoothQuickActionState.Unavailable => "ic_fluent_bluetooth_disabled_24_regular",
            BluetoothQuickActionState.Connected => "ic_fluent_bluetooth_connected_24_regular",
            BluetoothQuickActionState.Searching => "ic_fluent_bluetooth_searching_24_regular",
            _ => "ic_fluent_bluetooth_24_regular",
        };
        BluetoothIcon.Data = LoadBluetoothGeometry(resourceName);
        var isOn = state is BluetoothQuickActionState.On or BluetoothQuickActionState.Connected;

        var tile = _tileLayout?.FirstOrDefault(t => t.Key == "bluetooth");
        bool isCircle = tile == null || (tile.ColumnSpan == 1 && tile.RowSpan == 1);

        BluetoothButton.Background = isOn
            ? Brushes.White
            : (isCircle ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)));
        BluetoothIcon.Fill = isOn
            ? new SolidColorBrush(Color.FromRgb(24, 104, 220))
            : Brushes.White;
        BluetoothTitleText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(32, 33, 36))
            : (ForegroundBrush ?? Brushes.White);
        BluetoothStatusText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(113, 117, 122))
            : (SecondaryForegroundBrush ?? Brushes.White);
        BluetoothIconBubble.Background = isCircle
            ? Brushes.Transparent
            : (isOn ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xFB)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));

        var description = state switch
        {
            BluetoothQuickActionState.Unavailable => "Bluetooth không khả dụng",
            BluetoothQuickActionState.Off => "Bluetooth đang tắt · bấm để bật",
            BluetoothQuickActionState.Connected => "Bluetooth đang kết nối thiết bị · bấm để tắt",
            BluetoothQuickActionState.Searching => "Đang cập nhật trạng thái Bluetooth…",
            _ => "Bluetooth đang bật · bấm để tắt",
        };
        BluetoothButton.ToolTip = description;
        BluetoothStatusText.Text = state switch
        {
            BluetoothQuickActionState.Connected => "Đã kết nối",
            BluetoothQuickActionState.On => "Đang bật",
            BluetoothQuickActionState.Searching => "Đang tìm…",
            _ => "Đang tắt",
        };
        if (_editTileElements.TryGetValue("bluetooth", out var editBtElement))
            editBtElement.ToolTip = description;
        AutomationProperties.SetName(BluetoothButton, description);
    }

    private void OnAirplaneModeStateChanged(AirplaneModeQuickActionState state)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyAirplaneModeState(state);
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() => ApplyAirplaneModeState(state)));
    }

    private void ApplyAirplaneModeState(AirplaneModeQuickActionState state)
    {
        _lastAirplaneState = state;
        AirplaneIcon.Data = LoadAirplaneGeometry();
        var isOn = state == AirplaneModeQuickActionState.On;
        var tile = _tileLayout?.FirstOrDefault(t => t.Key == "airplane");
        bool isCircle = tile == null || (tile.ColumnSpan == 1 && tile.RowSpan == 1);

        AirplaneButton.Background = isOn
            ? Brushes.White
            : (isCircle ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)));
        AirplaneIcon.Fill = isOn
            ? new SolidColorBrush(Color.FromRgb(24, 104, 220))
            : Brushes.White;
        AirplaneTitleText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(32, 33, 36))
            : (ForegroundBrush ?? Brushes.White);
        AirplaneStatusText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(113, 117, 122))
            : (SecondaryForegroundBrush ?? Brushes.White);
        AirplaneIconBubble.Background = isCircle
            ? Brushes.Transparent
            : (isOn ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xFB)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
        AirplaneButton.Opacity = state == AirplaneModeQuickActionState.Unavailable ? 0.55 : 1;
        AirplaneStatusText.Text = isOn ? "Đang bật" : "Đang tắt";
        var description = state switch
        {
            AirplaneModeQuickActionState.Unavailable => "Không tìm thấy radio Wi-Fi, Bluetooth hoặc di động",
            AirplaneModeQuickActionState.On => "Chế độ máy bay đang bật · bấm để bật lại radio",
            AirplaneModeQuickActionState.Searching => "Đang cập nhật chế độ máy bay…",
            _ => "Chế độ máy bay đang tắt · bấm để tắt Wi-Fi và Bluetooth",
        };
        AirplaneButton.ToolTip = description;
        AutomationProperties.SetName(AirplaneButton, description);
    }

    private void OnNightLightStateChanged(NightLightQuickActionState state)
    {
        bool? s = state switch
        {
            NightLightQuickActionState.On => true,
            NightLightQuickActionState.Off => false,
            _ => null
        };
        if (Dispatcher.CheckAccess())
        {
            ApplyNightLightState(s);
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() => ApplyNightLightState(s)));
    }

    private void ApplyNightLightState(bool? state)
    {
        _lastNightLightState = state;
        var isOn = state == true;
        var isKnown = state.HasValue;

        if (isOn)
        {
            int strength = 50;
            try
            {
                strength = _nightLightStrength.Strength >= 0
                    ? _nightLightStrength.Strength
                    : NightLightCloudStore.ReadStrength();
            }
            catch { }
            NightLightDisplayEffect.Apply(strength);
        }
        else if (isKnown)
        {
            NightLightDisplayEffect.Restore();
        }
        var tile = _tileLayout?.FirstOrDefault(t => t.Key == "nightlight");
        bool isCircle = tile == null || (tile.ColumnSpan == 1 && tile.RowSpan == 1);

        NightLightButton.Background = isOn
            ? Brushes.White
            : (isCircle ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)));
        NightLightIcon.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(24, 104, 220))
            : (isKnown ? Brushes.White : (ForegroundBrush ?? Brushes.White));
        NightLightTitleText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(32, 33, 36))
            : (ForegroundBrush ?? Brushes.White);
        NightLightStatusText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(113, 117, 122))
            : (SecondaryForegroundBrush ?? Brushes.White);
        NightLightIconBubble.Background = isCircle
            ? Brushes.Transparent
            : (isOn ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xFB)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
        NightLightButton.Opacity = isKnown ? 1 : 0.55;
        NightLightStatusText.Text = isOn ? "Đang bật" : "Đang tắt";

        var description = state switch
        {
            true => "Night light của Windows đang bật · bấm để tắt",
            false => "Night light của Windows đang tắt · bấm để bật",
            _ => "Không thể đọc trạng thái Night light của Windows",
        };
        const string strengthHint = " · nhấn giữ để chỉnh cường độ";
        NightLightButton.ToolTip = description + strengthHint;
        if (_editTileElements.TryGetValue("nightlight", out var editNlElement))
            editNlElement.ToolTip = description;
        AutomationProperties.SetName(NightLightButton, description);
    }

    private async void OnEnergySaverRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshEnergySaverStateAsync();
    }

    private void RefreshEnergySaverState() => _ = RefreshEnergySaverStateAsync();

    private async Task RefreshEnergySaverStateAsync()
    {
        if (_disposed || _energySaverTogglePending) return;
        var state = await Task.Run(EnergySaver.IsEnabled);
        if (!_disposed) ApplyEnergySaverState(state);
    }

    private async void OnEnergySaverButtonClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _energySaverTogglePending) return;
        _energySaverTogglePending = true;
        EnergySaverButton.IsEnabled = false;

        try
        {
            var requestedState = await Task.Run(EnergySaver.Toggle);
            if (_disposed) return;
            if (requestedState is null)
            {
                EnergySaverButton.ToolTip = "Không thể thay đổi trạng thái Energy Saver của Windows.";
                return;
            }

            ApplyEnergySaverState(requestedState);
            await Task.Delay(150);
            var actualState = await Task.Run(EnergySaver.IsEnabled);
            if (!_disposed) ApplyEnergySaverState(actualState ?? requestedState);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Energy Saver] Could not toggle Windows state: {ex.Message}");
            if (!_disposed) EnergySaverButton.ToolTip = "Không thể thay đổi trạng thái Energy Saver của Windows.";
        }
        finally
        {
            _energySaverTogglePending = false;
            if (!_disposed)
            {
                EnergySaverButton.IsEnabled = true;
                RefreshEnergySaverState();
            }
        }
    }

    private void ApplyEnergySaverState(bool? state)
    {
        _lastEnergySaverState = state;
        var isOn = state == true;
        var isKnown = state.HasValue;
        var tile = _tileLayout?.FirstOrDefault(t => t.Key == "energysaver");
        bool isCircle = tile == null || (tile.ColumnSpan == 1 && tile.RowSpan == 1);

        EnergySaverButton.Background = isOn
            ? Brushes.White
            : (isCircle ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x41, 0x49)));
        EnergySaverIcon.Fill = isOn
            ? new SolidColorBrush(Color.FromRgb(24, 104, 220))
            : (isKnown ? Brushes.White : (ForegroundBrush ?? Brushes.White));
        EnergySaverTitleText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(32, 33, 36))
            : (ForegroundBrush ?? Brushes.White);
        EnergySaverStatusText.Foreground = isOn
            ? new SolidColorBrush(Color.FromRgb(113, 117, 122))
            : (SecondaryForegroundBrush ?? Brushes.White);
        EnergySaverIconBubble.Background = isCircle
            ? Brushes.Transparent
            : (isOn ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xFB)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
        EnergySaverButton.Opacity = isKnown ? 1 : 0.55;
        EnergySaverStatusText.Text = isOn ? "Đang bật" : "Đang tắt";

        var description = state switch
        {
            true => "Energy Saver đang bật · bấm để tắt",
            false => "Energy Saver đang tắt · bấm để bật",
            _ => "Không thể đọc trạng thái Energy Saver của Windows",
        };
        EnergySaverButton.ToolTip = description;
        AutomationProperties.SetName(EnergySaverButton, description);
    }

    private static Geometry LoadAirplaneGeometry()
    {
        const string resourceName = "ic_fluent_airplane_24_regular.svg";
        using var stream = typeof(QuickSettingsFlyoutWindow).Assembly.GetManifestResourceStream(
            $"Glassquid.Dock.Assets.Airplane.{resourceName}")
            ?? throw new InvalidOperationException($"The airplane icon resource '{resourceName}' was not embedded.");
        var document = XDocument.Load(stream);
        var pathData = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "path")?
            .Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
            throw new InvalidDataException($"The airplane icon resource '{resourceName}' does not contain an SVG path.");

        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private static Geometry LoadEnergySaverGeometry()
    {
        const string resourceName = "ic_fluent_battery_saver_24_regular.svg";
        using var stream = typeof(QuickSettingsFlyoutWindow).Assembly.GetManifestResourceStream(
            $"Glassquid.Dock.Assets.EnergySaver.{resourceName}")
            ?? throw new InvalidOperationException($"The Energy Saver icon resource '{resourceName}' was not embedded.");
        var document = XDocument.Load(stream);
        var pathData = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "path")?
            .Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
            throw new InvalidDataException($"The Energy Saver icon resource '{resourceName}' does not contain an SVG path.");

        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private static Geometry LoadBluetoothGeometry(string resourceName)
    {
        using var stream = typeof(QuickSettingsFlyoutWindow).Assembly.GetManifestResourceStream(
            $"Glassquid.Dock.Assets.Bluetooth.{resourceName}.svg")
            ?? throw new InvalidOperationException($"The Bluetooth icon resource '{resourceName}' was not embedded.");
        var document = XDocument.Load(stream);
        var pathData = document.Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "path")?
            .Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
            throw new InvalidDataException($"The Bluetooth icon resource '{resourceName}' does not contain an SVG path.");

        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioVolume is null) return;
        var percent = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        try
        {
            _audioVolume.SetMasterVolumePercent(percent);
            VolumeStatusText.Text = $"Âm lượng {percent}%";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            VolumeStatusText.Text = "Không thể đổi âm lượng";
        }
    }

    private void OnBrightnessSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingBrightness || !BrightnessSlider.IsEnabled) return;
        _pendingBrightness = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        BrightnessStatusText.Text = $"Độ sáng {_pendingBrightness}%";
        Interlocked.Increment(ref _brightnessRequestVersion);
        _brightnessDebounceTimer.Stop();
        _brightnessDebounceTimer.Start();
    }

    private async void OnBrightnessDebounceTick(object? sender, EventArgs e)
    {
        _brightnessDebounceTimer.Stop();
        // Don't send intermediate values while the thumb is still being dragged.
        // The drag-completed event schedules the final value once the user releases it.
        if (Mouse.LeftButton == MouseButtonState.Pressed && BrightnessSlider.IsMouseCaptureWithin) return;
        var value = _pendingBrightness;
        var requestVersion = Volatile.Read(ref _brightnessRequestVersion);
        await _brightnessWriteTask;
        if (_isClosing || requestVersion != Volatile.Read(ref _brightnessRequestVersion)) return;
        _brightnessWriteTask = ApplyBrightnessAsync(value, requestVersion);
        await _brightnessWriteTask;
    }

    private void OnBrightnessDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_disposed || _loadingBrightness || !BrightnessSlider.IsEnabled) return;
        _brightnessDebounceTimer.Stop();
        _brightnessDebounceTimer.Start();
    }

    private async Task ApplyBrightnessAsync(int value, int requestVersion)
    {
        var applied = await Task.Run(() => DisplayBrightnessController.TrySetPercent(value));
        if (_isClosing || requestVersion != Volatile.Read(ref _brightnessRequestVersion)) return;
        if (!applied)
        {
            BrightnessStatusText.Text = "Không thể đổi độ sáng màn hình";
            BrightnessSlider.ToolTip = "Màn hình từ chối thay đổi độ sáng.";
        }
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosing || _closeQueued) return;
        if (_wifiFlyoutWindow is { IsVisible: true } || _bluetoothDevicesFlyoutWindow is { IsVisible: true }) return;
        _closeQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _closeQueued = false;
            if (_wifiFlyoutWindow is { IsVisible: true } || _bluetoothDevicesFlyoutWindow is { IsVisible: true }) return;
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
        _isClosing = true;
        Close();
    }

    private static string CleanStatus(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var separator = value.IndexOf(" · ", StringComparison.Ordinal);
        return (separator >= 0 ? value[..separator] : value).Trim();
    }

    #region Outside Click Low-Level Mouse Hook

    private nint _mouseHookHandle;
    private HookProc? _mouseHookDelegate;

    private readonly List<Rect> _cachedDockButtonRects = new();

    private void CacheDockButtonRects()
    {
        _cachedDockButtonRects.Clear();
        try
        {
            foreach (var btn in new[] { _dock.NetworkButton, _dock.VolumeButton, _dock.BatteryButton })
            {
                if (btn != null && btn.IsVisible)
                {
                    var btnPoint = btn.PointToScreen(new Point(0, 0));
                    _cachedDockButtonRects.Add(new Rect(btnPoint.X, btnPoint.Y, btn.ActualWidth, btn.ActualHeight));
                }
            }
        }
        catch { /* Ignore */ }
    }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != 0) return;
        CacheDockButtonRects();
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
                            // If child flyouts are open, check if cursor is inside child flyout
                            bool isInsideChildFlyout = false;
                            if (_wifiFlyoutWindow is { IsVisible: true } wifi)
                            {
                                var hwndWifi = new WindowInteropHelper(wifi).Handle;
                                if (hwndWifi != 0 && GetWindowRect(hwndWifi, out var wifiRect))
                                {
                                    if (mouseInfo.pt.x >= wifiRect.Left && mouseInfo.pt.x <= wifiRect.Right &&
                                        mouseInfo.pt.y >= wifiRect.Top && mouseInfo.pt.y <= wifiRect.Bottom)
                                    {
                                        isInsideChildFlyout = true;
                                    }
                                }
                            }
                            if (_bluetoothDevicesFlyoutWindow is { IsVisible: true } bt)
                            {
                                var hwndBt = new WindowInteropHelper(bt).Handle;
                                if (hwndBt != 0 && GetWindowRect(hwndBt, out var btRect))
                                {
                                    if (mouseInfo.pt.x >= btRect.Left && mouseInfo.pt.x <= btRect.Right &&
                                        mouseInfo.pt.y >= btRect.Top && mouseInfo.pt.y <= btRect.Bottom)
                                    {
                                        isInsideChildFlyout = true;
                                    }
                                }
                            }

                            // If user clicked the Dock's Network/Volume/Battery buttons, check pre-cached rects
                            bool isDockButton = false;
                            for (int i = 0; i < _cachedDockButtonRects.Count; i++)
                            {
                                if (_cachedDockButtonRects[i].Contains(mouseInfo.pt.x, mouseInfo.pt.y))
                                {
                                    isDockButton = true;
                                    break;
                                }
                            }

                            if (!isDockButton && !isInsideChildFlyout)
                            {
                                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                                {
                                    if (!_isClosing && !_disposed)
                                    {
                                        _isClosing = true;
                                        Close();
                                    }
                                }));
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
