using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Glassquid.Dock;

internal enum BluetoothQuickActionState
{
    Unavailable,
    Off,
    On,
    Connected,
    Searching,
}

/// <summary>Reads and toggles the Bluetooth radio used by the Glassquid quick settings tile.</summary>
internal sealed class BluetoothQuickActionController : IDisposable
{
    private Radio? _radio;
    private int _refreshVersion;
    private bool _disposed;

    public event Action<BluetoothQuickActionState>? StateChanged;

    public BluetoothQuickActionState State { get; private set; } = BluetoothQuickActionState.Searching;

    public async Task RefreshAsync()
    {
        if (_disposed) return;

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(candidate => candidate.Kind == RadioKind.Bluetooth);
            if (!ReferenceEquals(_radio, radio))
            {
                if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
                _radio = radio;
                if (_radio is not null) _radio.StateChanged += OnRadioStateChanged;
            }

            if (_radio is null)
            {
                SetState(BluetoothQuickActionState.Unavailable);
                return;
            }

            await RefreshStateAsync(_radio);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not read Bluetooth state: {ex.Message}");
            SetState(BluetoothQuickActionState.Unavailable);
        }
    }

    public async Task<bool> ToggleAsync()
    {
        var radio = _radio;
        if (_disposed || radio is null) return false;

        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[Glassquid Bluetooth] Radio access was not allowed: {access}.");
                await RefreshStateAsync(radio);
                return false;
            }

            SetState(BluetoothQuickActionState.Searching);
            var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
            var result = await radio.SetStateAsync(target);
            if (result != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[Glassquid Bluetooth] Radio state change was not allowed: {result}.");
                await RefreshStateAsync(radio);
                return false;
            }

            // Windows applies radio state changes asynchronously; briefly allow StateChanged to arrive.
            await Task.Delay(250);
            await RefreshStateAsync(radio);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Bluetooth] Could not toggle Bluetooth: {ex.Message}");
            await RefreshStateAsync(radio);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
        _radio = null;
        StateChanged = null;
    }

    private async void OnRadioStateChanged(Radio sender, object args)
    {
        await RefreshStateAsync(sender);
    }

    private async Task RefreshStateAsync(Radio radio)
    {
        if (_disposed || !ReferenceEquals(_radio, radio)) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        if (radio.State != RadioState.On)
        {
            SetState(BluetoothQuickActionState.Off);
            return;
        }

        SetState(BluetoothQuickActionState.Searching);
        var connected = await HasConnectedBluetoothDeviceAsync();
        if (_disposed || version != Volatile.Read(ref _refreshVersion) || !ReferenceEquals(_radio, radio)) return;
        SetState(connected ? BluetoothQuickActionState.Connected : BluetoothQuickActionState.On);
    }

    private static async Task<bool> HasConnectedBluetoothDeviceAsync()
    {
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(selector);
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            // Some desktop installations do not grant the Bluetooth device-enumeration capability.
            Debug.WriteLine($"[Glassquid Bluetooth] Could not enumerate connected devices: {ex.Message}");
            return false;
        }
    }

    private void SetState(BluetoothQuickActionState state)
    {
        if (_disposed) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
