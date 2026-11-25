using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock;

/// <summary>
/// Opens a saved screenshot directly in Snipping Tool's image editor using Windows' native ms-screensketch protocol.
/// </summary>
internal static class SnippingToolImageOpener
{
    private const string SnippingToolAumid = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";

    internal static bool TryOpen(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) return false;

        // 1. Try Windows' native ms-screensketch protocol (the exact same protocol Windows Action Center uses)
        try
        {
            var uri = $"ms-screensketch:edit?&filePath={Uri.EscapeDataString(filePath)}&isTemporary=false&saved=true&source=Toast";
            var psi = new ProcessStartInfo(uri)
            {
                UseShellExecute = true
            };
            Process.Start(psi);

            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                BringToForeground();
            });

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Notifications] ms-screensketch protocol launch failed: {ex.Message}");
        }

        // 2. Fallback: launch the file directly in the system's default image editor / viewer
        try
        {
            var psi = new ProcessStartInfo(filePath)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Notifications] Fallback image launch failed: {ex.Message}");
            return false;
        }
    }

    internal static void BringToForeground()
    {
        try
        {
            var matches = WindowLocator.FindWindowsForAumid(SnippingToolAumid);
            if (matches.Count > 0)
            {
                WindowLocator.TryBringToForeground(matches[0].Handle);
                return;
            }

            foreach (var p in Process.GetProcessesByName("SnippingTool"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != 0)
                    {
                        WindowLocator.TryBringToForeground(p.MainWindowHandle);
                        return;
                    }
                }
            }
        }
        catch { }
    }
}
