using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glassquid.Dock;

internal sealed record KeyboardLayoutOption(
    nint Handle,
    string LanguageCode,
    string DisplayName,
    string LayoutName,
    bool IsCurrent);

internal static class KeyboardLayoutCatalog
{
    private const uint InputLanguageChangeRequest = 0x0050;
    private const byte VkLWin = 0x5B;
    private const byte VkSpace = 0x20;
    private const uint KeyEventKeyUp = 0x0002;
    private const string KeyboardLayoutsRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    public static IReadOnlyList<KeyboardLayoutOption> Read(nint targetWindow)
    {
        var targetThread = targetWindow == 0 ? 0 : GetWindowThreadProcessId(targetWindow, out _);
        var current = GetKeyboardLayout(targetThread);
        var count = Math.Max(0, GetKeyboardLayoutList(0, null));
        var handles = new List<nint>(Math.Max(count, 1));
        if (count > 0)
        {
            var buffer = new nint[count];
            var read = Math.Clamp(GetKeyboardLayoutList(buffer.Length, buffer), 0, buffer.Length);
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != 0 && !handles.Contains(buffer[index])) handles.Add(buffer[index]);
            }
        }

        if (current != 0 && !handles.Contains(current)) handles.Insert(0, current);
        if (handles.Count == 0) return Array.Empty<KeyboardLayoutOption>();

        var currentIndex = handles.IndexOf(current);
        if (currentIndex < 0)
        {
            var currentLanguageId = GetLanguageId(current);
            currentIndex = handles.FindIndex(handle => GetLanguageId(handle) == currentLanguageId);
        }
        if (currentIndex > 0)
        {
            var currentHandle = handles[currentIndex];
            handles.RemoveAt(currentIndex);
            handles.Insert(0, currentHandle);
            currentIndex = 0;
        }

        var options = new List<KeyboardLayoutOption>(handles.Count);
        for (var index = 0; index < handles.Count; index++)
        {
            var handle = handles[index];
            var languageId = GetLanguageId(handle);
            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(languageId);
            }
            catch (CultureNotFoundException)
            {
                continue;
            }

            options.Add(new KeyboardLayoutOption(
                handle,
                culture.ThreeLetterISOLanguageName.ToUpperInvariant(),
                culture.EnglishName,
                ReadLayoutName(languageId) ?? culture.EnglishName,
                index == currentIndex));
        }

        return options;
    }

    public static bool RequestChange(nint targetWindow, nint layoutHandle) =>
        targetWindow != 0 && layoutHandle != 0
        && PostMessage(targetWindow, InputLanguageChangeRequest, 0, layoutHandle);

    public static bool IsActive(nint targetWindow, nint layoutHandle)
    {
        return layoutHandle != 0 && GetCurrentLayout(targetWindow) == layoutHandle;
    }

    public static nint GetCurrentLayout(nint targetWindow)
    {
        if (targetWindow == 0) return 0;
        var threadId = GetWindowThreadProcessId(targetWindow, out _);
        return threadId == 0 ? 0 : GetKeyboardLayout(threadId);
    }

    public static int GetShortcutCycles(
        nint targetWindow,
        nint desiredLayout,
        IReadOnlyList<KeyboardLayoutOption> layouts)
    {
        if (targetWindow == 0 || desiredLayout == 0 || layouts.Count < 2) return 0;

        var currentLayout = GetCurrentLayout(targetWindow);
        var currentIndex = -1;
        var desiredIndex = -1;
        for (var index = 0; index < layouts.Count; index++)
        {
            if (layouts[index].Handle == currentLayout) currentIndex = index;
            if (layouts[index].Handle == desiredLayout) desiredIndex = index;
        }

        return currentIndex < 0 || desiredIndex < 0
            ? 0
            : (desiredIndex - currentIndex + layouts.Count) % layouts.Count;
    }

    public static async Task SendWindowsSpaceCyclesAsync(int cycleCount)
    {
        if (cycleCount <= 0) return;

        keybd_event(VkLWin, 0, 0, 0);
        try
        {
            // Give the shell time to open its layout switcher before cycling entries.
            await Task.Delay(45);
            for (var index = 0; index < cycleCount; index++)
            {
                keybd_event(VkSpace, 0, 0, 0);
                keybd_event(VkSpace, 0, KeyEventKeyUp, 0);
                await Task.Delay(55);
            }
        }
        finally
        {
            keybd_event(VkLWin, 0, KeyEventKeyUp, 0);
        }
    }

    public static nint GetFocusedWindow(nint targetWindow)
    {
        if (targetWindow == 0) return 0;
        var threadId = GetWindowThreadProcessId(targetWindow, out _);
        if (threadId == 0) return targetWindow;

        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref info) && info.FocusWindow != 0
            ? info.FocusWindow
            : targetWindow;
    }

    private static ushort GetLanguageId(nint layoutHandle) =>
        unchecked((ushort)(layoutHandle.ToInt64() & 0xFFFF));

    private static string? ReadLayoutName(ushort languageId)
    {
        var layoutId = $"0000{languageId:X4}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $"{KeyboardLayoutsRegistryPath}\\{layoutId}", writable: false);
            return key?.GetValue("Layout Text") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetKeyboardLayoutList(int bufferCount, [Out] nint[]? layouts);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }


    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
