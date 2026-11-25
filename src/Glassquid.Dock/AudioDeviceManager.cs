using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glassquid.Dock;

/// <summary>Information about an audio output playback endpoint.</summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; set; }
    public string IconGlyph { get; init; } = "\uE7F5";
}

/// <summary>Information about an application's audio session in the volume mixer.</summary>
public sealed class AudioSessionItem
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ImageSource? Icon { get; init; }
    public string FallbackGlyph { get; init; } = "\uE767";
    public float Volume { get; set; }
    public bool IsMuted { get; set; }
    public Action<float>? SetVolumeAction { get; init; }
}

/// <summary>Interacts with Windows Core Audio to enumerate endpoints, switch default output, and control per-app mixer sessions.</summary>
public static class AudioDeviceManager
{
    private const uint DEVICE_STATE_ACTIVE = 1;
    private const uint CLSCTX_ALL = 23;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    public static List<AudioDeviceInfo> GetPlaybackDevices(out string defaultId)
    {
        var devices = new List<AudioDeviceInfo>();
        defaultId = string.Empty;

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;

        try
        {
            enumerator = CreateDeviceEnumerator();

            // Read default render device ID
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) == 0 && defaultDevice != null)
            {
                if (defaultDevice.GetId(out var defId) == 0)
                {
                    defaultId = defId ?? string.Empty;
                }
            }

            // Enumerate active playback devices
            if (enumerator.EnumAudioEndpoints(0, DEVICE_STATE_ACTIVE, out collection) == 0 && collection != null)
            {
                if (collection.GetCount(out var count) == 0)
                {
                    var pkeyName = new PROPERTYKEY
                    {
                        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                        pid = 14
                    };

                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice? dev = null;
                        IPropertyStore? store = null;
                        try
                        {
                            if (collection.Item(i, out dev) == 0 && dev != null)
                            {
                                if (dev.GetId(out var id) == 0 && !string.IsNullOrEmpty(id))
                                {
                                    var name = "Âm thanh";
                                    if (dev.OpenPropertyStore(0, out store) == 0 && store != null)
                                    {
                                        if (store.GetValue(ref pkeyName, out var val) == 0 && val.vt == 31 && val.pwszVal != IntPtr.Zero)
                                        {
                                            name = Marshal.PtrToStringUni(val.pwszVal) ?? name;
                                        }
                                    }

                                    var isDef = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                                    var glyph = ResolveDeviceGlyph(name);

                                    devices.Add(new AudioDeviceInfo
                                    {
                                        Id = id,
                                        Name = name,
                                        IsDefault = isDef,
                                        IconGlyph = glyph
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[AudioDeviceManager] Error reading device {i}: {ex.Message}");
                        }
                        finally
                        {
                            ReleaseCom(store);
                            ReleaseCom(dev);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceManager] Error enumerating devices: {ex.Message}");
        }
        finally
        {
            ReleaseCom(defaultDevice);
            ReleaseCom(collection);
            ReleaseCom(enumerator);
        }

        return devices;
    }

    public static bool SetDefaultPlaybackDevice(string deviceId) => SetDefaultPlaybackDevice(deviceId, out _);

    public static bool SetDefaultPlaybackDevice(string deviceId, out int multimediaHr)
    {
        multimediaHr = 0;
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            multimediaHr = policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            _ = policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            _ = policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            return multimediaHr == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceManager] SetDefaultPlaybackDevice error: {ex.Message}");
            multimediaHr = ex.HResult;
            return false;
        }
    }

    public static List<AudioSessionItem> GetAppAudioSessions()
    {
        var items = new List<AudioSessionItem>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? defaultDevice = null;
        object? sessionManagerObj = null;

        try
        {
            enumerator = CreateDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) != 0 || defaultDevice == null)
            {
                return items;
            }

            var asmGuid = new Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4");
            if (defaultDevice.Activate(ref asmGuid, CLSCTX_ALL, IntPtr.Zero, out sessionManagerObj) != 0 || sessionManagerObj == null)
            {
                return items;
            }

            if (sessionManagerObj is not IAudioSessionManager2 mgr2)
            {
                return items;
            }

            if (mgr2.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null)
            {
                return items;
            }

            if (sessionEnum.GetCount(out var count) != 0)
            {
                return items;
            }

            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl? ctl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctl) == 0 && ctl != null)
                    {
                        var ctl2 = ctl as IAudioSessionControl2;
                        uint pid = 0;
                        ctl2?.GetProcessId(out pid);

                        // Don't show inactive/expired sessions or glassquid self session if muted
                        string procName = "Âm thanh hệ thống";
                        string dispName = "System Sounds";
                        ImageSource? icon = null;

                        if (pid != 0)
                        {
                            try
                            {
                                var p = Process.GetProcessById((int)pid);
                                procName = p.ProcessName;
                                dispName = !string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.MainWindowTitle : p.ProcessName;
                                icon = TryGetProcessIcon((int)pid);
                            }
                            catch
                            {
                                procName = $"PID {pid}";
                                dispName = procName;
                            }
                        }

                        if (ctl is ISimpleAudioVolume vol)
                        {
                            vol.GetMasterVolume(out var level);
                            vol.GetMute(out var muted);

                            // Keep a reference to the COM volume interface
                            var capturedVol = vol;
                            items.Add(new AudioSessionItem
                            {
                                ProcessId = pid,
                                ProcessName = procName,
                                DisplayName = dispName,
                                Icon = icon,
                                FallbackGlyph = pid == 0 ? "\uE767" : "\uE71D",
                                Volume = Math.Clamp(level, 0f, 1f),
                                IsMuted = muted,
                                SetVolumeAction = (newVol) =>
                                {
                                    try
                                    {
                                        var empty = Guid.Empty;
                                        capturedVol.SetMasterVolume(Math.Clamp(newVol, 0f, 1f), ref empty);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[AudioDeviceManager] SetMasterVolume error: {ex.Message}");
                                    }
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioDeviceManager] Error reading audio session {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceManager] Error enumerating app sessions: {ex.Message}");
        }
        finally
        {
            ReleaseCom(defaultDevice);
            ReleaseCom(enumerator);
        }

        return items;
    }

    private static BitmapSource? TryGetProcessIcon(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            var exePath = proc.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (ico != null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        ico.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
            }
        }
        catch
        {
            // Process might be elevated or 32/64 bit mismatch
        }
        return null;
    }

    private static string ResolveDeviceGlyph(string deviceName)
    {
        var lower = deviceName.ToLowerInvariant();
        if (lower.Contains("headphone") || lower.Contains("headset") || lower.Contains("hitune") ||
            lower.Contains("earphone") || lower.Contains("buds") || lower.Contains("airpod"))
        {
            return "\uE7F6"; // Headphone icon
        }

        if (lower.Contains("display") || lower.Contains("hdmi") || lower.Contains("tv") || lower.Contains("monitor"))
        {
            return "\uE7F4"; // Monitor / TV display icon
        }

        return "\uE7F5"; // Speakers icon
    }

    private static void ReleaseCom(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try
            {
                Marshal.FinalReleaseComObject(comObj);
            }
            catch
            {
                // Ignore release errors
            }
        }
    }

    #region COM Interop Definitions

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.Interface)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propCount);
        [PreserveSig] int GetAt(uint prop, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT val);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    public enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr ppFormat, IntPtr ppEndpointFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, out IntPtr pmftDefaultPeriod, out IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, int bFxStore, IntPtr pKey, IntPtr pProp);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, int bFxStore, IntPtr pKey, IntPtr pProp);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class CPolicyConfigClient { }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid AudioSessionGuid, int StreamFlags, out IntPtr SimpleAudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionIndex, out IAudioSessionControl Session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    #endregion
}
