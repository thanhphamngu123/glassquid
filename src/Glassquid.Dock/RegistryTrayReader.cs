using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Glassquid.Dock;

/// <summary>Reads running, non-promoted tray apps from the user's registered tray settings.</summary>
internal static class RegistryTrayReader
{
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
    private static readonly string? ProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432")
        ?? Environment.GetEnvironmentVariable("ProgramFiles");

    public static bool? LastKnownUniKeyIsVietnamese { get; set; }

    public static IReadOnlyList<TrayIconItem> ReadRunningHiddenIcons(bool artworkOnly = false)
    {
        var (processesByPath, processesByName) = GetRunningProcesses();
        var result = new List<TrayIconItem>();
        var seenProcessIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTooltips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentExecutablePath = Environment.ProcessPath;

        using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
        if (root is null) return result;

        foreach (var subKeyName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(subKeyName);
            if (key is null) continue;

            var rawPath = key.GetValue("ExecutablePath") as string;
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            var executablePath = ResolvePath(rawPath);
            if (string.IsNullOrWhiteSpace(executablePath)) continue;

            var processName = Path.GetFileNameWithoutExtension(executablePath);
            if (string.IsNullOrWhiteSpace(processName)
                || processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                continue;

            // Match against running processes:
            // Prefer exact executable path match so old historical paths (e.g. app moved or old builds)
            // are not falsely revived when another instance with the same name is running.
            ActiveProcess? process = null;
            if (processesByPath.TryGetValue(executablePath, out var matchedByPath))
            {
                process = matchedByPath;
            }
            else if (processesByName.TryGetValue(processName, out var matchedByName))
            {
                // If path couldn't be resolved for the running process (e.g. system permissions),
                // only allow fallback if the candidate process doesn't already have a known different path.
                if (string.IsNullOrEmpty(matchedByName.ExecutablePath) ||
                    matchedByName.ExecutablePath.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    process = matchedByName;
                }
            }

            if (process is null) continue;

            var isGlassquid = IsCurrentApplicationIcon(executablePath, currentExecutablePath);

            // Windows marks icons shown directly on the taskbar as promoted.
            // Missing values follow TrayInspector's "only hidden" behavior and remain included.
            if (!artworkOnly && !isGlassquid && key.GetValue("IsPromoted") is int promoted && promoted == 1) continue;

            var tooltip = key.GetValue("InitialTooltip") as string;
            if (string.IsNullOrWhiteSpace(tooltip) && File.Exists(executablePath))
            {
                try { tooltip = FileVersionInfo.GetVersionInfo(executablePath).FileDescription; }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            }

            var name = string.IsNullOrWhiteSpace(tooltip)
                ? processName
                : tooltip.Trim();

            var iconId = ReadIconId(key.GetValue("UID") ?? key.GetValue("IconUid"));
            var iconGuid = ReadIconGuid(key.GetValue("IconGuid"));

            // Deduplication:
            // 1. Same executable path and name
            if (!seenPaths.Add($"{executablePath}|{name}")) continue;

            // 2. Same process and same Tray Icon ID / GUID (Windows Shell only allows one icon per uID/GUID)
            if (iconGuid.HasValue && !seenProcessIcons.Add($"guid:{process.Id}:{iconGuid.Value}"))
                continue;
            if (iconId.HasValue && !seenProcessIcons.Add($"id:{process.Id}:{iconId.Value}"))
                continue;

            // 3. Global duplicate Tooltip/Name across duplicate component drivers (e.g. Nvidia containers)
            if (!seenTooltips.Add(name))
                continue;

            var icon = TryCreateImage(key.GetValue("IconSnapshot") as byte[])
                ?? TryExtractExecutableIcon(executablePath);
            var item = new TrayIconItem(name, icon, executablePath, process.Id, iconId, iconGuid);
            if (executablePath.Contains("unikey", StringComparison.OrdinalIgnoreCase)
                || name.Contains("unikey", StringComparison.OrdinalIgnoreCase))
            {
                var isViet = TrayIconContextMenuRelay.QueryUniKeyIsVietnamese();
                if (isViet.HasValue)
                {
                    LastKnownUniKeyIsVietnamese = isViet.Value;
                }

                var useViet = LastKnownUniKeyIsVietnamese ?? true;
                item.IsUniKeyModeVietnamese = useViet;
                var unikeyIcon = TryExtractExecutableIcon(executablePath, useViet ? 1 : 2);
                if (unikeyIcon != null)
                {
                    item.Icon = unikeyIcon;
                }
                item.ToolTip = useViet ? "UniKey (Tiếng Việt - Bấm để chuyển sang E)" : "UniKey (English - Bấm để chuyển sang V)";
            }

            result.Add(item);
        }

        // NotifyIconSettings is a historical catalog and can retain entries
        // after an app unregisters its icon. Keep only icons that Explorer can
        // currently locate, otherwise Glassquid shows dead entries whose menu
        // cannot be invoked.
        return result
            // Glassquid's own icon is registered through NIM_ADD, but Windows may
            // expose it only after the native overflow has opened. Keep it in the
            // Glassquid flyout whenever its process and registry entry are live.
            .Where(item => artworkOnly || IsCurrentApplicationIcon(item.ExecutablePath, currentExecutablePath)
                || TrayIconContextMenuRelay.IsNativeTrayIconAvailable(item))
            .OrderBy(item => item.ToolTip, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCurrentApplicationIcon(string executablePath, string? currentExecutablePath) =>
        !string.IsNullOrWhiteSpace(currentExecutablePath)
        && string.Equals(executablePath, currentExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static (Dictionary<string, ActiveProcess> ByPath, Dictionary<string, ActiveProcess> ByName) GetRunningProcesses()
    {
        var currentSessionId = Environment.ProcessId != 0
            ? Process.GetCurrentProcess().SessionId
            : -1;

        var byPath = new Dictionary<string, ActiveProcess>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, ActiveProcess>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // Only consider processes running in the current interactive session
                if (currentSessionId >= 0 && process.SessionId != currentSessionId)
                    continue;

                var name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var path = TryGetProcessPath(process.Id);
                var candidate = new ActiveProcess(process.Id, process.MainWindowHandle != 0, path);

                if (!string.IsNullOrEmpty(path))
                {
                    if (!byPath.TryGetValue(path, out var curPath) || candidate.HasWindow && !curPath.HasWindow)
                    {
                        byPath[path] = candidate;
                    }
                }

                if (!byName.TryGetValue(name, out var current) || candidate.HasWindow && !current.HasWindow)
                {
                    byName[name] = candidate;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Processes may exit or deny metadata access while this list is built.
            }
            finally
            {
                process.Dispose();
            }
        }

        return (byPath, byName);
    }

    private static string ResolvePath(string path)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
        var replacements = new (string Token, string Path)[]
        {
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", system),
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", programFilesX86),
            ("{905E63B6-C1BF-494E-B29C-65B732D3D21A}", ProgramFiles ?? string.Empty),
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", ProgramFiles ?? string.Empty),
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", local),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", roaming),
        };

        foreach (var (token, replacement) in replacements)
        {
            if (path.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                path = replacement + path[token.Length..];
                break;
            }
        }

        path = Environment.ExpandEnvironmentVariables(path);
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }

    private static BitmapImage? TryCreateImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 8 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal static BitmapSource? TryExtractExecutableIcon(string executablePath, int iconIndex = 0)
    {
        if (!File.Exists(executablePath)) return null;

        nint large = 0;
        nint small = 0;
        try
        {
            if (ExtractIconEx(executablePath, iconIndex, out large, out small, 1) == 0) return null;
            var handle = small != 0 ? small : large;
            if (handle == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or Win32Exception or ExternalException)
        {
            return null;
        }
        finally
        {
            if (large != 0) _ = DestroyIcon(large);
            if (small != 0 && small != large) _ = DestroyIcon(small);
        }
    }

    private static uint? ReadIconId(object? value) => value switch
    {
        int id => unchecked((uint)id),
        uint id => id,
        long id when id >= 0 && id <= uint.MaxValue => (uint)id,
        ulong id when id <= uint.MaxValue => (uint)id,
        byte[] bytes when bytes.Length >= sizeof(uint) => BitConverter.ToUInt32(bytes, 0),
        _ => null,
    };

    private static Guid? ReadIconGuid(object? value)
    {
        if (value is Guid guid && guid != Guid.Empty) return guid;
        if (value is byte[] bytes && bytes.Length == 16)
        {
            var binaryGuid = new Guid(bytes);
            return binaryGuid == Guid.Empty ? null : binaryGuid;
        }

        if (value is string text && Guid.TryParse(text, out var parsed) && parsed != Guid.Empty)
            return parsed;

        return null;
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconEx(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, int flags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static string? TryGetProcessPath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
            {
                var full = new string(buffer, 0, size);
                try { return Path.GetFullPath(full); } catch { return full; }
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private sealed record ActiveProcess(int Id, bool HasWindow, string? ExecutablePath);
}
