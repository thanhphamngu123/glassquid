using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Glassquid.Dock;

/// <summary>
/// Controller for synchronizing with Windows 11 Do Not Disturb (Focus Assist / Quiet Hours) state.
/// Uses Windows Notification Facility (WNF) and native shell fallback queries.
/// </summary>
public static class WindowsDoNotDisturbController
{
    // WNF State Name for Quiet Hours / Do Not Disturb active profile changes
    private const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(
        ref ulong StateName,
        IntPtr TypeId,
        IntPtr ExplicitScope,
        out uint ChangeStamp,
        IntPtr Buffer,
        ref uint BufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtUpdateWnfStateData(
        ref ulong StateName,
        IntPtr Buffer,
        uint Length,
        IntPtr TypeId,
        IntPtr ExplicitScope,
        uint MatchingChangeStamp,
        uint CheckStamp);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    // QUERY_USER_NOTIFICATION_STATE values
    private const int QUNS_NOT_PRESENT = 1;
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;
    private const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    private const int QUNS_QUIET_TIME = 6;
    private const int QUNS_APP = 7;

    public static event EventHandler<bool>? StateChanged;

    private static bool? _lastKnownState;

    /// <summary>
    /// Checks if Windows 11 Do Not Disturb (Focus Assist) is currently active.
    /// </summary>
    public static bool IsActive()
    {
        // 1. Primary: WNF query (fast, kernel-backed, Windows 10/11)
        try
        {
            ulong stateName = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            uint changeStamp = 0;
            uint bufferSize = sizeof(uint);
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                int status = NtQueryWnfStateData(ref stateName, IntPtr.Zero, IntPtr.Zero, out changeStamp, buffer, ref bufferSize);
                if (status == 0)
                {
                    uint val = (uint)Marshal.ReadInt32(buffer);
                    bool active = val > 0;
                    if (_lastKnownState != active)
                    {
                        _lastKnownState = active;
                        StateChanged?.Invoke(null, active);
                    }
                    return active;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid DND] WNF Query failed: {ex.Message}");
        }

        // 2. Fallback: Shell Notification State
        try
        {
            if (SHQueryUserNotificationState(out int quns) == 0)
            {
                if (quns is QUNS_BUSY or QUNS_QUIET_TIME)
                {
                    if (_lastKnownState != true)
                    {
                        _lastKnownState = true;
                        StateChanged?.Invoke(null, true);
                    }
                    return true;
                }
                if (quns == QUNS_ACCEPTS_NOTIFICATIONS)
                {
                    if (_lastKnownState != false)
                    {
                        _lastKnownState = false;
                        StateChanged?.Invoke(null, false);
                    }
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid DND] SHQueryUserNotificationState failed: {ex.Message}");
        }

        // 3. Fallback: CloudStore Registry inspection
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current");
            if (baseKey != null)
            {
                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    if (subName.Contains("windows.data.donotdisturb.quiethourssettings", StringComparison.OrdinalIgnoreCase))
                    {
                        using var leafKey = baseKey.OpenSubKey($@"{subName}\windows.data.donotdisturb.quiethourssettings");
                        if (leafKey?.GetValue("Data") is byte[] data)
                        {
                            var text = Encoding.Unicode.GetString(data);
                            if (text.Contains("PriorityOnly", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("AlarmsOnly", StringComparison.OrdinalIgnoreCase))
                            {
                                if (_lastKnownState != true)
                                {
                                    _lastKnownState = true;
                                    StateChanged?.Invoke(null, true);
                                }
                                return true;
                            }
                            if (text.Contains("Unrestricted", StringComparison.OrdinalIgnoreCase))
                            {
                                if (_lastKnownState != false)
                                {
                                    _lastKnownState = false;
                                    StateChanged?.Invoke(null, false);
                                }
                                return false;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid DND] Registry check failed: {ex.Message}");
        }

        return _lastKnownState ?? false;
    }

    /// <summary>
    /// Updates the Do Not Disturb state in Windows 11.
    /// </summary>
    public static bool SetActive(bool enable)
    {
        bool success = false;

        // 1. Update WNF
        try
        {
            ulong stateName = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(buffer, enable ? 1 : 0);
                int status = NtUpdateWnfStateData(ref stateName, buffer, sizeof(uint), IntPtr.Zero, IntPtr.Zero, 0, 0);
                if (status == 0) success = true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid DND] WNF Update failed: {ex.Message}");
        }

        // 2. Also sync Toast Enabled registry key if applicable
        try
        {
            using var notifKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            notifKey?.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", enable ? 0 : 1, RegistryValueKind.DWord);
        }
        catch { /* Ignore */ }

        _lastKnownState = enable;
        StateChanged?.Invoke(null, enable);
        return success;
    }

    /// <summary>
    /// Toggles the Do Not Disturb state.
    /// </summary>
    public static bool Toggle()
    {
        bool current = IsActive();
        bool next = !current;
        SetActive(next);
        return next;
    }
}
