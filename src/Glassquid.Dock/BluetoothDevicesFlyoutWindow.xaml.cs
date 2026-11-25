using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Linq;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Glassquid.Dock;

/// <summary>A Glassquid glass panel for Bluetooth quick controls and device pairing.</summary>
public partial class BluetoothDevicesFlyoutWindow : GlassWindow, IDisposable
{
    private static readonly Lazy<Geometry> BluetoothGeometry = new(() => LoadBluetoothGeometry("ic_fluent_bluetooth_24_regular"));
    private static readonly Lazy<Geometry> BluetoothConnectedGeometry = new(() => LoadBluetoothGeometry("ic_fluent_bluetooth_connected_24_regular"));
    private static readonly Lazy<Geometry> BluetoothDisabledGeometry = new(() => LoadBluetoothGeometry("ic_fluent_bluetooth_disabled_24_regular"));
    private static readonly Lazy<Geometry> BluetoothSearchingGeometry = new(() => LoadBluetoothGeometry("ic_fluent_bluetooth_searching_24_regular"));

    private readonly SystemTrayBarWindow _dock;
    private readonly BluetoothQuickActionController _bluetooth = new();
    private readonly ObservableCollection<BluetoothDeviceEntry> _pairedDevices = [];
    private readonly ObservableCollection<BluetoothDeviceEntry> _newDevices = [];
    private readonly Dictionary<string, BluetoothDeviceEntry> _newDevicesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _glassRevealPending;
    private bool _isClosing;
    private bool _closeQueued;
    private bool _disposed;
    private bool _powerChangePending;
    private DeviceWatcher? _classicWatcher;
    private DeviceWatcher? _leWatcher;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal BluetoothDevicesFlyoutWindow(SystemTrayBarWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;
        InitializeComponent();

        PairedDevicesList.ItemsSource = _pairedDevices;
        NewDevicesList.ItemsSource = _newDevices;
        HeaderBluetoothIcon.Data = BluetoothGeometry.Value;
        _bluetooth.StateChanged += OnBluetoothStateChanged;
        ApplyBluetoothState(BluetoothQuickActionState.Searching);

        Loaded += OnPanelLoaded;
        PanelCornerRadius = new CornerRadius(34);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndCenter();
        GlassSurface.PrepareRevealForWindow(this);
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
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupSlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(34, appearance.DockCornerRadius);
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
        Loaded -= OnPanelLoaded;
        _bluetooth.StateChanged -= OnBluetoothStateChanged;
        _bluetooth.Dispose();
        StopWatcher(ref _classicWatcher);
        StopWatcher(ref _leWatcher);
        GC.SuppressFinalize(this);
    }

    private void MeasureContentAndCenter()
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
        await _bluetooth.RefreshAsync();
        await RefreshPairedDevicesAsync();
        if (_bluetooth.State is BluetoothQuickActionState.On or BluetoothQuickActionState.Connected)
            StartScanning();
    }

    private async void OnBluetoothPowerClick(object sender, RoutedEventArgs e)
    {
        if (_powerChangePending) return;
        _powerChangePending = true;
        BluetoothPowerButton.IsEnabled = false;
        BluetoothStatusText.Text = "Đang đổi trạng thái Bluetooth…";
        var changed = await _bluetooth.ToggleAsync();
        _powerChangePending = false;
        if (_disposed) return;
        BluetoothPowerButton.IsEnabled = true;
        if (!changed)
            SetOperationStatus("Windows không cho phép đổi trạng thái Bluetooth trên thiết bị này.");
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
        HeaderBluetoothIcon.Data = state switch
        {
            BluetoothQuickActionState.Off or BluetoothQuickActionState.Unavailable => BluetoothDisabledGeometry.Value,
            BluetoothQuickActionState.Connected => BluetoothConnectedGeometry.Value,
            BluetoothQuickActionState.Searching => BluetoothSearchingGeometry.Value,
            _ => BluetoothGeometry.Value,
        };

        var enabled = state is BluetoothQuickActionState.On or BluetoothQuickActionState.Connected;
        BluetoothToggleTrack.Background = enabled
            ? new SolidColorBrush(Color.FromRgb(57, 191, 244))
            : new SolidColorBrush(Color.FromArgb(74, 255, 255, 255));
        BluetoothToggleKnob.HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        BluetoothStatusText.Text = state switch
        {
            BluetoothQuickActionState.Unavailable => "Không tìm thấy bộ thu Bluetooth.",
            BluetoothQuickActionState.Off => "Bluetooth đang tắt. Bật Bluetooth để tìm thiết bị.",
            BluetoothQuickActionState.Connected => "Bluetooth đang bật · đã kết nối thiết bị.",
            BluetoothQuickActionState.Searching => "Đang kiểm tra Bluetooth…",
            _ => "Bluetooth đang bật · đang tìm thiết bị ở gần.",
        };
        BluetoothPowerButton.IsEnabled = state != BluetoothQuickActionState.Unavailable && !_powerChangePending;
        AutomationProperties.SetName(BluetoothPowerButton, enabled ? "Tắt Bluetooth" : "Bật Bluetooth");

        if (enabled)
        {
            StartScanning();
        }
        else if (state is BluetoothQuickActionState.Off or BluetoothQuickActionState.Unavailable)
        {
            StopWatcher(ref _classicWatcher);
            StopWatcher(ref _leWatcher);
            _newDevices.Clear();
            _newDevicesById.Clear();
            NewDevicesEmptyText.Text = state == BluetoothQuickActionState.Unavailable
                ? "Thiết bị này không có bộ thu Bluetooth khả dụng."
                : "Bật Bluetooth và chế độ ghép đôi trên thiết bị để bắt đầu quét.";
            NewDevicesEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshPairedDevicesAsync()
    {
        var connectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devicesById = new Dictionary<string, DeviceInformation>(StringComparer.OrdinalIgnoreCase);
        var selectors = new[]
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        };

        foreach (var selector in selectors)
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(selector);
                foreach (var device in devices)
                {
                    if (device.Pairing.IsPaired && !string.IsNullOrWhiteSpace(device.Id))
                        devicesById.TryAdd(device.Id, device);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Bluetooth] Could not list paired devices: {ex.Message}");
            }
        }

        foreach (var selector in new[]
        {
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        })
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(selector);
                foreach (var device in devices)
                {
                    if (string.IsNullOrWhiteSpace(device.Id)) continue;

                    connectedIds.Add(device.Id);
                    // A device can be reported as connected even when the paired-state
                    // selector does not return it (for example, some Bluetooth LE devices).
                    // Keep the paired-state result when available because it usually has
                    // the richer friendly name, and use this live result as a fallback.
                    devicesById.TryAdd(device.Id, device);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Bluetooth] Could not read connected devices: {ex.Message}");
            }
        }

        if (_disposed) return;
        _pairedDevices.Clear();
        foreach (var device in devicesById.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var connected = connectedIds.Contains(device.Id);
            _pairedDevices.Add(new BluetoothDeviceEntry(
                device,
                string.IsNullOrWhiteSpace(device.Name) ? "Thiết bị Bluetooth" : device.Name,
                connected ? "Đã kết nối" : "Đã ghép đôi · chưa kết nối",
                connected ? BluetoothConnectedGeometry.Value : BluetoothGeometry.Value));
        }
        PairedEmptyText.Visibility = _pairedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartScanning()
    {
        if (_disposed || _bluetooth.State is not (BluetoothQuickActionState.On or BluetoothQuickActionState.Connected)) return;
        if (_classicWatcher is not null || _leWatcher is not null) return;

        _newDevices.Clear();
        _newDevicesById.Clear();
        NewDevicesEmptyText.Text = "Đang quét. Hãy bật chế độ ghép đôi trên thiết bị mới.";
        NewDevicesEmptyText.Visibility = Visibility.Visible;
        _classicWatcher = CreateAndStartWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(false));
        _leWatcher = CreateAndStartWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false));
        if (_classicWatcher is null && _leWatcher is null)
            NewDevicesEmptyText.Text = "Windows không cấp quyền quét thiết bị Bluetooth cho Glassquid. Mở Cài đặt Bluetooth để tiếp tục.";
    }

    private DeviceWatcher? CreateAndStartWatcher(string selector)
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += OnDeviceAdded;
            watcher.Updated += OnDeviceUpdated;
            watcher.Removed += OnDeviceRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            watcher.Start();
            return watcher;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not start device scan: {ex.Message}");
            return null;
        }
    }

    private void StopWatcher(ref DeviceWatcher? watcher)
    {
        if (watcher is null) return;
        watcher.Added -= OnDeviceAdded;
        watcher.Updated -= OnDeviceUpdated;
        watcher.Removed -= OnDeviceRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not stop device scan: {ex.Message}");
        }
        watcher = null;
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Id)) return;
        _ = Dispatcher.BeginInvoke(new Action(() => AddNewDevice(info)));
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        _ = RefreshDeviceInfoAsync(update.Id);
    }

    private async Task RefreshDeviceInfoAsync(string id)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id);
            if (info is not null) _ = Dispatcher.BeginInvoke(new Action(() => AddNewDevice(info)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not refresh discovered device: {ex.Message}");
        }
    }

    private void AddNewDevice(DeviceInformation info)
    {
        if (_disposed || info.Pairing.IsPaired || string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Id)) return;
        if (_newDevicesById.ContainsKey(info.Id)) return;

        var item = new BluetoothDeviceEntry(info, info.Name, "Thiết bị mới ở gần", BluetoothGeometry.Value);
        _newDevicesById.Add(info.Id, item);
        _newDevices.Add(item);
        NewDevicesEmptyText.Visibility = Visibility.Collapsed;
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_newDevicesById.Remove(update.Id, out var item)) return;
            _newDevices.Remove(item);
            NewDevicesEmptyText.Visibility = _newDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }));
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_newDevices.Count == 0 && _bluetooth.State is BluetoothQuickActionState.On or BluetoothQuickActionState.Connected)
                NewDevicesEmptyText.Text = "Chưa thấy thiết bị mới. Hãy bật chế độ ghép đôi trên thiết bị cần thêm.";
        }));
    }

    private async void OnPairDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BluetoothDeviceEntry entry }) return;
        SetOperationStatus($"Đang ghép đôi với {entry.Name}…");
        if (sender is Button pairButton) pairButton.IsEnabled = false;
        try
        {
            var result = await entry.Device.Pairing.PairAsync();
            if (entry.Device.Pairing.IsPaired)
            {
                SetOperationStatus($"Đã ghép đôi với {entry.Name}.");
                await RefreshPairedDevicesAsync();
                _newDevicesById.Remove(entry.Device.Id);
                _newDevices.Remove(entry);
                NewDevicesEmptyText.Visibility = _newDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                SetOperationStatus($"Chưa ghép đôi được với {entry.Name}: {result.Status}.");
                if (sender is Button button) button.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Pairing failed: {ex}");
            SetOperationStatus($"Không thể ghép đôi với {entry.Name}. Hãy thử trong Cài đặt Bluetooth.");
            if (sender is Button button) button.IsEnabled = true;
        }
    }

    private void OnManageDeviceClick(object sender, RoutedEventArgs e) => OpenWindowsBluetoothSettings();

    private async void OnRemoveDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BluetoothDeviceEntry entry } button) return;
        button.IsEnabled = false;
        SetOperationStatus($"Đang gỡ {entry.Name}…");
        try
        {
            var result = await entry.Device.Pairing.UnpairAsync();
            SetOperationStatus(result.Status.ToString() == "Unpaired"
                ? $"Đã gỡ {entry.Name}."
                : $"Không thể gỡ {entry.Name}: {result.Status}.");
            await RefreshPairedDevicesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Unpairing failed: {ex.Message}");
            SetOperationStatus($"Không thể gỡ {entry.Name}. Hãy thử trong Cài đặt Bluetooth.");
        }
        finally
        {
            if (!_disposed) button.IsEnabled = true;
        }
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        StopWatcher(ref _classicWatcher);
        StopWatcher(ref _leWatcher);
        StartScanning();
    }

    private void OnMoreSettingsClick(object sender, RoutedEventArgs e) => OpenWindowsBluetoothSettings();

    private void OpenWindowsBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            _isClosing = true;
            Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not open Windows Bluetooth settings: {ex.Message}");
            SetOperationStatus("Không mở được Cài đặt Bluetooth của Windows.");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        Close();
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
        _isClosing = true;
        Close();
    }

    private void SetOperationStatus(string text)
    {
        OperationStatusText.Text = text;
        OperationStatusText.Visibility = Visibility.Visible;
    }

    private static Geometry LoadBluetoothGeometry(string resourceName)
    {
        using var stream = typeof(BluetoothDevicesFlyoutWindow).Assembly.GetManifestResourceStream(
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

    private sealed record BluetoothDeviceEntry(DeviceInformation Device, string Name, string Status, Geometry IconGeometry);
}
