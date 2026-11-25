using System.Collections.ObjectModel;
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

/// <summary>A centered Glassquid Wi-Fi network picker with radio, scan, connection and disconnect controls.</summary>
public partial class WifiFlyoutWindow : GlassWindow, IDisposable
{
    private readonly SystemTrayBarWindow _dock;
    private readonly WifiQuickActionController _wifi = new();
    private readonly ObservableCollection<WifiNetworkEntry> _networks = [];
    private bool _glassRevealPending;
    private bool _isClosing;
    private bool _closeQueued;
    private bool _disposed;
    private bool _radioTogglePending;
    private bool _networkOperationPending;
    private bool _passwordPromptOpen;
    private WifiNetworkInfo? _connectedNetwork;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal WifiFlyoutWindow(SystemTrayBarWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;
        InitializeComponent();
        AvailableNetworksList.ItemsSource = _networks;
        _wifi.StateChanged += OnWifiStateChanged;
        ApplyWifiState(WifiRadioState.Searching);
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

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(34, appearance.DockCornerRadius);
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
        _wifi.StateChanged -= OnWifiStateChanged;
        _wifi.Dispose();
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
        await RefreshNetworksAsync(scan: false);
        _ = ScanNetworksAfterShowAsync();
    }

    private async Task ScanNetworksAfterShowAsync()
    {
        await Task.Delay(250);
        if (!_disposed && _wifi.State == WifiRadioState.On)
            await RefreshNetworksAsync(scan: true);
    }

    private async void OnWifiPowerClick(object sender, RoutedEventArgs e)
    {
        if (_radioTogglePending || _disposed) return;
        _radioTogglePending = true;
        WifiPowerButton.IsEnabled = false;
        ApplyWifiState(WifiRadioState.Searching);
        var changed = await _wifi.ToggleAsync();
        _radioTogglePending = false;
        if (_disposed) return;
        WifiPowerButton.IsEnabled = true;
        if (!changed) SetOperationStatus("Windows không cho phép đổi trạng thái Wi-Fi. Hãy kiểm tra chế độ máy bay hoặc công tắc phần cứng.");
        await RefreshNetworksAsync(scan: changed);
    }

    private async void OnRefreshNetworksClick(object sender, RoutedEventArgs e) => await RefreshNetworksAsync(scan: true);

    private async Task RefreshNetworksAsync(bool scan)
    {
        if (_disposed || _networkOperationPending) return;
        RefreshText.Text = scan ? "Đang quét…" : "Đang tải…";
        NetworksEmptyText.Text = "Đang tìm mạng Wi-Fi…";
        NetworksEmptyText.Visibility = Visibility.Visible;
        try
        {
            await _wifi.RefreshAsync();
            if (_wifi.State != WifiRadioState.On)
            {
                _networks.Clear();
                _connectedNetwork = null;
                UpdateConnectedNetwork();
                NetworksEmptyText.Text = _wifi.State == WifiRadioState.Unavailable
                    ? "Không tìm thấy bộ điều hợp Wi-Fi trên thiết bị này."
                    : "Wi-Fi đang tắt.";
                return;
            }

            var result = await Task.Run(() => scan ? NativeWifi.ScanAndReadNetworks() : NativeWifi.ReadNetworks());
            if (_disposed) return;
            _connectedNetwork = result.FirstOrDefault(network => network.IsConnected);
            _networks.Clear();
            foreach (var network in result.Where(network => !network.IsConnected))
                _networks.Add(new WifiNetworkEntry(network));
            UpdateConnectedNetwork();
            WifiStatusText.Text = _connectedNetwork is { } connected
                ? $"Đang bật · kết nối {connected.Name}"
                : "Đang bật · chưa kết nối";
            NetworksEmptyText.Text = result.Count == 0 ? "Không tìm thấy mạng Wi-Fi gần đây." : "Không còn mạng Wi-Fi nào khác trong vùng phủ sóng.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Could not enumerate networks: {ex}");
            NetworksEmptyText.Text = $"Không thể đọc danh sách Wi-Fi. {ex.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                RefreshText.Text = "Quét lại";
                NetworksEmptyText.Visibility = _networks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_networkOperationPending || _disposed) return;
        _networkOperationPending = true;
        DisconnectButton.IsEnabled = false;
        SetOperationStatus("Đang ngắt kết nối…");
        try
        {
            var requested = await Task.Run(NativeWifi.Disconnect);
            if (!requested) SetOperationStatus("Windows chưa nhận lệnh ngắt kết nối Wi-Fi.");
            else if (await Task.Run(() => NativeWifi.WaitForConnection(string.Empty, shouldConnect: false, TimeSpan.FromSeconds(5))))
                SetOperationStatus("Đã ngắt kết nối Wi-Fi.");
            else SetOperationStatus("Lệnh ngắt đã gửi nhưng kết nối vẫn đang được cập nhật.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Disconnect failed: {ex}");
            SetOperationStatus("Không thể ngắt kết nối Wi-Fi.");
        }
        finally
        {
            _networkOperationPending = false;
            if (!_disposed)
            {
                DisconnectButton.IsEnabled = true;
                await RefreshNetworksAsync(scan: false);
            }
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WifiNetworkEntry entry } button || _networkOperationPending || _disposed) return;
        string? password = null;
        if (NativeWifi.RequiresPassword(entry.Network) && !entry.Network.HasProfile())
        {
            var prompt = new WifiPasswordDialog(entry.Network.Name) { Owner = this };
            _passwordPromptOpen = true;
            try
            {
                if (prompt.ShowDialog() != true) return;
                password = prompt.Password;
            }
            finally
            {
                _passwordPromptOpen = false;
            }
        }

        _networkOperationPending = true;
        button.IsEnabled = false;
        SetOperationStatus($"Đang kết nối với {entry.Network.Name}…");
        try
        {
            var attempt = await Task.Run(() =>
            {
                var started = NativeWifi.Connect(entry.Network, password, out var message);
                return (started, message);
            });
            SetOperationStatus(attempt.message);
            if (attempt.started)
            {
                var connected = await Task.Run(() => NativeWifi.WaitForConnection(entry.Network.Name, shouldConnect: true, TimeSpan.FromSeconds(18)));
                SetOperationStatus(connected ? $"Đã kết nối với {entry.Network.Name}." : "Yêu cầu đã gửi. Windows vẫn đang kết nối với mạng.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Connection failed: {ex}");
            SetOperationStatus($"Không thể kết nối với {entry.Network.Name}. Hãy kiểm tra mật khẩu hoặc thử lại.");
        }
        finally
        {
            _networkOperationPending = false;
            if (!_disposed)
            {
                button.IsEnabled = true;
                await RefreshNetworksAsync(scan: false);
            }
        }
    }

    private void UpdateConnectedNetwork()
    {
        if (_connectedNetwork is not { } network)
        {
            ConnectedNetworkCard.Visibility = Visibility.Collapsed;
            return;
        }

        ConnectedNetworkNameText.Text = network.Name;
        ConnectedNetworkDetailsText.Text = network.IsSecure
            ? $"Đã kết nối, bảo mật · tín hiệu {network.SignalQuality}%"
            : $"Đã kết nối · tín hiệu {network.SignalQuality}%";
        ConnectedNetworkCard.Visibility = Visibility.Visible;
    }

    private void OnWifiStateChanged(WifiRadioState state)
    {
        if (Dispatcher.CheckAccess()) ApplyWifiState(state);
        else _ = Dispatcher.BeginInvoke(new Action(() => ApplyWifiState(state)));
    }

    private void ApplyWifiState(WifiRadioState state)
    {
        if (_disposed) return;
        var isOn = state == WifiRadioState.On;
        WifiToggleTrack.Background = isOn ? new SolidColorBrush(Color.FromArgb(255, 81, 193, 255)) : new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));
        WifiToggleKnob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        WifiStatusText.Text = state switch
        {
            WifiRadioState.On => _connectedNetwork is { } network ? $"Đang bật · kết nối {network.Name}" : "Đang bật · chưa kết nối",
            WifiRadioState.Off => "Wi-Fi đang tắt",
            WifiRadioState.Unavailable => "Wi-Fi không khả dụng",
            _ => "Đang kiểm tra Wi-Fi…",
        };
        WifiPowerButton.IsEnabled = state != WifiRadioState.Unavailable && !_radioTogglePending;
        if (!isOn && !_networkOperationPending)
        {
            ConnectedNetworkCard.Visibility = Visibility.Collapsed;
            _networks.Clear();
        }
    }

    private void SetOperationStatus(string text)
    {
        OperationStatusText.Text = text;
        OperationStatusText.Visibility = Visibility.Visible;
    }

    private void OnMoreSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true });
            _isClosing = true;
            Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Could not open Windows Wi-Fi settings: {ex.Message}");
            SetOperationStatus("Không mở được Cài đặt Wi-Fi.");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _isClosing = true;
        Close();
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosing || _closeQueued || _passwordPromptOpen) return;
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

    private sealed class WifiNetworkEntry
    {
        public WifiNetworkInfo Network { get; }
        public string Name => Network.Name;
        public uint SignalQuality => Network.SignalQuality;
        public string SecurityLabel => Network.IsSecure ? "Có bảo mật" : "Mở";

        public WifiNetworkEntry(WifiNetworkInfo network) => Network = network;
    }
}
