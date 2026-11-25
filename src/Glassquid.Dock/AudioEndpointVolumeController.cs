using System;
using System.Runtime.InteropServices;

namespace Glassquid.Dock;

/// <summary>Reads and changes the default render endpoint's master volume via Windows Core Audio.</summary>
internal sealed class AudioEndpointVolumeController : IDisposable
{
    private readonly AudioDeviceManager.IMMDeviceEnumerator _enumerator;
    private readonly AudioDeviceManager.IMMDevice _device;
    private readonly AudioDeviceManager.IAudioEndpointVolume _endpointVolume;
    private bool _disposed;

    public AudioEndpointVolumeController()
    {
        _enumerator = AudioDeviceManager.CreateDeviceEnumerator();
        Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(0, 1, out _device));

        var interfaceId = typeof(AudioDeviceManager.IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(_device.Activate(ref interfaceId, ClsCtxAll, IntPtr.Zero, out var endpoint));
        _endpointVolume = (AudioDeviceManager.IAudioEndpointVolume)endpoint;
    }

    public int GetMasterVolumePercent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Marshal.ThrowExceptionForHR(_endpointVolume.GetMasterVolumeLevelScalar(out var level));
        return Math.Clamp((int)Math.Round(level * 100), 0, 100);
    }

    public void SetMasterVolumePercent(int percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var level = Math.Clamp(percent, 0, 100) / 100f;
        var eventContext = Guid.Empty;
        Marshal.ThrowExceptionForHR(_endpointVolume.SetMasterVolumeLevelScalar(level, ref eventContext));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseComObject(_endpointVolume);
        ReleaseComObject(_device);
        ReleaseComObject(_enumerator);
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private const uint ClsCtxAll = 23;
}
