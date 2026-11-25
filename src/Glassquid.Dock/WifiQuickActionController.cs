using System.Diagnostics;
using Windows.Devices.Radios;

namespace Glassquid.Dock;

internal enum WifiRadioState
{
    Unavailable,
    Off,
    On,
    Searching,
}

/// <summary>Reads and toggles the Wi-Fi radio when the user operates the Glassquid Wi-Fi panel.</summary>
internal sealed class WifiQuickActionController : IDisposable
{
    private Radio? _radio;
    private bool _disposed;
    private bool _togglePending;

    public event Action<WifiRadioState>? StateChanged;
    public WifiRadioState State { get; private set; } = WifiRadioState.Searching;

    public async Task RefreshAsync()
    {
        if (_disposed || _togglePending) return;
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(candidate => candidate.Kind == RadioKind.WiFi);
            if (!ReferenceEquals(_radio, radio))
            {
                if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
                _radio = radio;
                if (_radio is not null) _radio.StateChanged += OnRadioStateChanged;
            }

            SetState(radio is null ? WifiRadioState.Unavailable : ToState(radio.State));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Could not read radio state: {ex.Message}");
            SetState(WifiRadioState.Unavailable);
        }
    }

    public async Task<bool> ToggleAsync()
    {
        if (_disposed || _togglePending) return false;
        await RefreshAsync();
        if (_radio is null || State == WifiRadioState.Unavailable) return false;

        _togglePending = true;
        SetState(WifiRadioState.Searching);
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[Glassquid Wi-Fi] Radio access was not allowed: {access}.");
                return false;
            }

            var target = _radio.State == RadioState.On ? RadioState.Off : RadioState.On;
            var result = await _radio.SetStateAsync(target);
            if (result != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[Glassquid Wi-Fi] Could not set radio to {target}: {result}.");
                return false;
            }

            await Task.Delay(300);
            return _radio.State == target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Wi-Fi] Could not toggle radio: {ex.Message}");
            return false;
        }
        finally
        {
            _togglePending = false;
            await RefreshAsync();
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
        if (_togglePending || _disposed) return;
        await RefreshAsync();
    }

    private static WifiRadioState ToState(RadioState state) => state switch
    {
        RadioState.On => WifiRadioState.On,
        RadioState.Off => WifiRadioState.Off,
        _ => WifiRadioState.Unavailable,
    };

    private void SetState(WifiRadioState state)
    {
        if (_disposed) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
