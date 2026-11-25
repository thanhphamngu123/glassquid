using System.Diagnostics;
using Windows.Devices.Radios;

namespace Glassquid.Dock;

internal enum AirplaneModeQuickActionState
{
    Unavailable,
    Off,
    On,
    Searching,
}

/// <summary>Controls the Wi-Fi, Bluetooth, and mobile radios used by the Glassquid airplane tile.</summary>
internal sealed class AirplaneModeQuickActionController : IDisposable
{
    private readonly Dictionary<string, Radio> _radios = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _restoreRadioKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _togglePending;
    private bool _disposed;

    public event Action<AirplaneModeQuickActionState>? StateChanged;

    public AirplaneModeQuickActionState State { get; private set; } = AirplaneModeQuickActionState.Searching;

    public async Task RefreshAsync()
    {
        if (_disposed || _togglePending) return;

        try
        {
            var radios = (await Radio.GetRadiosAsync())
                .Where(radio => IsAirplaneRadio(radio.Kind))
                .ToArray();
            var nextRadios = radios.ToDictionary(GetRadioKey, StringComparer.OrdinalIgnoreCase);

            foreach (var (key, radio) in _radios.ToArray())
            {
                if (nextRadios.TryGetValue(key, out var next) && ReferenceEquals(radio, next)) continue;
                radio.StateChanged -= OnRadioStateChanged;
                _radios.Remove(key);
            }

            foreach (var (key, radio) in nextRadios)
            {
                if (_radios.TryGetValue(key, out var current) && ReferenceEquals(current, radio)) continue;
                _radios[key] = radio;
                radio.StateChanged += OnRadioStateChanged;
            }

            if (_radios.Count == 0)
            {
                SetState(AirplaneModeQuickActionState.Unavailable);
                return;
            }

            SetState(_radios.Values.Any(radio => radio.State == RadioState.On)
                ? AirplaneModeQuickActionState.Off
                : AirplaneModeQuickActionState.On);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Airplane] Could not read radio state: {ex.Message}");
            SetState(AirplaneModeQuickActionState.Unavailable);
        }
    }

    public async Task<bool> ToggleAsync()
    {
        if (_disposed || _togglePending) return false;
        await RefreshAsync();
        if (_radios.Count == 0 || State == AirplaneModeQuickActionState.Unavailable) return false;

        _togglePending = true;
        SetState(AirplaneModeQuickActionState.Searching);
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Debug.WriteLine($"[Glassquid Airplane] Radio access was not allowed: {access}.");
                return false;
            }

            // The mode is represented by all available Wi-Fi/Bluetooth/mobile radios
            // being off. If any is on, this click enables airplane mode.
            var turnOn = _radios.Values.Any(radio => radio.State == RadioState.On);

            List<KeyValuePair<string, Radio>> targets;
            if (turnOn)
            {
                _restoreRadioKeys = _radios
                    .Where(pair => pair.Value.State == RadioState.On)
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                targets = _radios.Where(pair => pair.Value.State == RadioState.On).ToList();
            }
            else
            {
                targets = _radios
                    .Where(pair => _restoreRadioKeys.Contains(pair.Key) && pair.Value.State == RadioState.Off)
                    .ToList();

                // If the mode was already active when Glassquid opened, we have no saved
                // pre-airplane state. Restore Wi-Fi as the least surprising way to exit it.
                if (targets.Count == 0)
                {
                    var fallback = _radios.FirstOrDefault(pair => pair.Value.Kind == RadioKind.WiFi && pair.Value.State == RadioState.Off);
                    if (fallback.Value is not null) targets.Add(fallback);
                }
            }

            var targetState = turnOn ? RadioState.Off : RadioState.On;
            var allAllowed = true;
            foreach (var (_, radio) in targets)
            {
                var result = await radio.SetStateAsync(targetState);
                if (result == RadioAccessStatus.Allowed) continue;
                allAllowed = false;
                Debug.WriteLine($"[Glassquid Airplane] Could not set {radio.Kind} to {targetState}: {result}.");
            }

            if (!turnOn) _restoreRadioKeys.Clear();
            await Task.Delay(350);
            var anyRadioOn = _radios.Values.Any(radio => radio.State == RadioState.On);
            var reachedTarget = turnOn ? !anyRadioOn : anyRadioOn;
            return allAllowed && reachedTarget;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Airplane] Could not toggle radios: {ex.Message}");
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
        foreach (var radio in _radios.Values)
            radio.StateChanged -= OnRadioStateChanged;
        _radios.Clear();
        StateChanged = null;
    }

    private async void OnRadioStateChanged(Radio sender, object args)
    {
        if (!_togglePending) await RefreshAsync();
    }

    private void SetState(AirplaneModeQuickActionState state)
    {
        if (_disposed) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static bool IsAirplaneRadio(RadioKind kind) =>
        kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband;

    private static string GetRadioKey(Radio radio) => $"{radio.Kind}|{radio.Name}";
}
