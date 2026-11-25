using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Core.Imaging;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock;

public record NotificationItem(
    uint Id,
    string AppName,
    string Title,
    string Body,
    string TimeAgo,
    string AppGlyph,
    Color AppColor,
    ImageSource? AppIcon = null,
    string? AppUserModelId = null,
    string? PackageFamilyName = null,
    string? TargetExecutable = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// Background singleton service that integrates with Windows UserNotificationListener,
/// caches notifications in-memory, polls periodically for unpackaged Win32 support,
/// and dynamically supports all Windows apps, screenshot tools, and newly installed games.
/// </summary>
public sealed class WindowsNotificationManager : IDisposable
{
    private static readonly Lazy<WindowsNotificationManager> _lazyInstance = new(() => new WindowsNotificationManager());
    public static WindowsNotificationManager Instance => _lazyInstance.Value;

    private static readonly ConcurrentDictionary<string, ImageSource?> _appIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> _appTargetCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private readonly HashSet<uint> _dismissedIds = new();
    private List<NotificationItem> _cachedNotifications = new();
    private bool _initialized;
    private bool _isListening;
    private bool _hasListenerAccess;
    private bool _isFetching;
    private Timer? _pollingTimer;

    public event EventHandler? NotificationsChanged;

    private WindowsNotificationManager()
    {
    }

    public IReadOnlyList<NotificationItem> GetCachedNotifications()
    {
        lock (_lock)
        {
            if (_cachedNotifications.Count > 0)
            {
                return _cachedNotifications.ToList();
            }
        }
        return Array.Empty<NotificationItem>();
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Run background pre-warming and initial fetch immediately
        Task.Run(async () =>
        {
            await WarmUpAndFetchAsync();
        });

        // Set up 1-second polling timer to guarantee real-time updates for unpackaged Win32 apps
        _pollingTimer = new Timer(async _ =>
        {
            await WarmUpAndFetchAsync();
        }, null, 1000, 1000);
    }

    public async Task RefreshAsync()
    {
        await WarmUpAndFetchAsync();
    }

    private async Task WarmUpAndFetchAsync()
    {
        lock (_lock)
        {
            if (_isFetching) return;
            _isFetching = true;
        }

        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            if (!_hasListenerAccess)
            {
                var access = await listener.RequestAccessAsync();
                _hasListenerAccess = (access == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed);
            }

            if (_hasListenerAccess)
            {
                if (!_isListening)
                {
                    try
                    {
                        listener.NotificationChanged += (_, _) =>
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(150);
                                await WarmUpAndFetchAsync();
                            });
                        };
                        _isListening = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Glassquid NotificationManager] Hook NotificationChanged failed: {ex.Message}");
                    }
                }

                var toasts = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                var items = new List<NotificationItem>();

                if (toasts != null && toasts.Count > 0)
                {
                    // Strict chronological sorting: newest first by CreationTime descending, then by Id descending
                    var sortedToasts = toasts
                        .OrderByDescending(t => t.CreationTime)
                        .ThenByDescending(t => t.Id)
                        .Take(30)
                        .ToList();

                    uint idCounter = 1;
                    foreach (var toast in sortedToasts)
                    {
                        lock (_lock)
                        {
                            if (_dismissedIds.Contains(toast.Id))
                            {
                                continue;
                            }
                        }
                        // 1. App name & identity resolution - wrap in localized try-catch because
                        // toast.AppInfo can throw NotImplementedException in unpackaged Win32 environments
                        string appName = "Hệ thống Windows";
                        string? appUserModelId = null;
                        string? packageFamilyName = null;
                        string? targetExecutable = null;

                        try
                        {
                            var appInfo = toast.AppInfo;
                            if (appInfo != null)
                            {
                                packageFamilyName = appInfo.PackageFamilyName;
                                var rawId = appInfo.Id;
                                if (!string.IsNullOrWhiteSpace(packageFamilyName) && !string.IsNullOrWhiteSpace(rawId))
                                {
                                    appUserModelId = rawId.Contains('!') ? rawId : $"{packageFamilyName}!{rawId}";
                                }
                                else if (!string.IsNullOrWhiteSpace(rawId))
                                {
                                    appUserModelId = rawId;
                                }

                                if (appInfo.DisplayInfo?.DisplayName is { Length: > 0 } displayName)
                                {
                                    appName = displayName;
                                }
                                else if (!string.IsNullOrWhiteSpace(rawId))
                                {
                                    appName = CleanAppId(rawId);
                                }
                            }
                        }
                        catch
                        {
                            // AppInfo property can throw NotImplementedException on certain Win32 toasts
                        }

                        // 2. Visual text elements extraction (filter out empty/whitespace lines)
                        var textList = new List<string>();
                        try
                        {
                            var visual = toast.Notification?.Visual;
                            if (visual != null)
                            {
                                var genericBinding = visual.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                                if (genericBinding != null)
                                {
                                    var elements = genericBinding.GetTextElements();
                                    if (elements != null)
                                    {
                                        foreach (var el in elements)
                                        {
                                            var text = el.Text?.Trim();
                                            if (!string.IsNullOrEmpty(text))
                                                textList.Add(text);
                                        }
                                    }
                                }

                                if (textList.Count == 0 && visual.Bindings != null)
                                {
                                    foreach (var b in visual.Bindings)
                                    {
                                        var elements = b.GetTextElements();
                                        if (elements != null)
                                        {
                                            foreach (var el in elements)
                                            {
                                                var text = el.Text?.Trim();
                                                if (!string.IsNullOrEmpty(text))
                                                    textList.Add(text);
                                            }
                                        }
                                        if (textList.Count > 0) break;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Glassquid NotificationManager] Visual extract error: {ex.Message}");
                        }

                        string title = appName;
                        string body = "Thông báo hệ thống mới";

                        if (textList.Count > 0)
                        {
                            title = textList[0];
                            if (textList.Count > 1)
                            {
                                body = string.Join(" \u00B7 ", textList.Skip(1));
                            }
                            else
                            {
                                body = appName;
                            }

                            // If appName is still fallback and title starts with app brand
                            if (appName == "Hệ thống Windows" && title.Contains(' '))
                            {
                                var parts = title.Split(' ');
                                if (parts.Length >= 2 && (parts[0] == "Antigravity" || parts[0] == "Discord" || parts[0] == "Steam" || parts[0] == "Epic" || parts[0] == "Snipping"))
                                {
                                    appName = string.Join(" ", parts.Take(2));
                                }
                            }
                        }

                        // Normalize / resolve AppUserModelId for known Windows built-in and Store apps
                        if (string.IsNullOrEmpty(appUserModelId))
                        {
                            var combined = $"{appName} {title}";
                            if (combined.Contains("Snipping", StringComparison.OrdinalIgnoreCase) ||
                                combined.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
                                combined.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
                                combined.Contains("Cắt màn hình", StringComparison.OrdinalIgnoreCase) ||
                                combined.Contains("Cắt", StringComparison.OrdinalIgnoreCase))
                            {
                                appUserModelId = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
                                if (appName == "Hệ thống Windows") appName = "Snipping Tool";
                            }
                            else if (combined.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase))
                            {
                                appUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
                                if (appName == "Hệ thống Windows") appName = "ChatGPT";
                            }
                            else if (combined.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
                                     combined.Contains("Cài đặt", StringComparison.OrdinalIgnoreCase))
                            {
                                appUserModelId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
                                if (appName == "Hệ thống Windows") appName = "Cài đặt";
                            }
                            else if (combined.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
                                     combined.Contains("Cửa hàng", StringComparison.OrdinalIgnoreCase))
                            {
                                appUserModelId = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";
                                if (appName == "Hệ thống Windows") appName = "Microsoft Store";
                            }
                        }

                        // 3. Resolve the actual application icon image and cached target
                        var appIcon = await TryResolveAppIconAsync(toast, appName);
                        if (_appTargetCache.TryGetValue(appName, out var cachedTarget))
                        {
                            targetExecutable = cachedTarget;
                        }

                        var timeAgo = FormatTimeAgo(toast.CreationTime);
                        items.Add(new NotificationItem(
                            toast.Id > 0 ? toast.Id : idCounter++,
                            appName,
                            title,
                            body,
                            timeAgo,
                            ResolveAppGlyph(appName, title),
                            ResolveAppColor(appName),
                            appIcon,
                            appUserModelId,
                            packageFamilyName,
                            targetExecutable,
                            toast.CreationTime));
                    }
                }

                bool hasChanged = false;
                lock (_lock)
                {
                    if (!AreNotificationListsEqual(_cachedNotifications, items))
                    {
                        _cachedNotifications = items;
                        hasChanged = true;
                    }
                }

                if (hasChanged)
                {
                    NotificationsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid NotificationManager] WarmUpAndFetch error: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isFetching = false;
            }
        }
    }

    private static BitmapSource NormalizeBitmap(BitmapSource source)
    {
        try
        {
            BitmapSource bgraSource = source;
            if (source.Format != PixelFormats.Bgra32)
            {
                bgraSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            }

            int width = bgraSource.PixelWidth;
            int height = bgraSource.PixelHeight;
            if (width <= 0 || height <= 0) return source;

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bgraSource.CopyPixels(pixels, stride, 0);

            var normalized = IconCanvasNormalizer.Normalize(pixels, width, height);
            if (normalized.Width <= 0 || normalized.Height <= 0) return source;

            var result = BitmapSource.Create(
                normalized.Width, normalized.Height,
                source.DpiX > 0 ? source.DpiX : 96,
                source.DpiY > 0 ? source.DpiY : 96,
                PixelFormats.Bgra32,
                null,
                normalized.Bgra,
                normalized.Width * 4);
            result.Freeze();
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid NotificationManager] NormalizeBitmap error: {ex.Message}");
            return source;
        }
    }

    private static BitmapSource? CreateNormalizedBitmap(Glassquid.Platform.Shell.ExtractedIcon? extracted)
    {
        if (extracted == null || extracted.VisiblePixelCount == 0) return null;
        try
        {
            var normalized = IconCanvasNormalizer.Normalize(extracted.Bgra, extracted.Width, extracted.Height);
            var bmp = BitmapSource.Create(
                normalized.Width, normalized.Height,
                96, 96, PixelFormats.Bgra32, null,
                normalized.Bgra, normalized.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> TryResolveAppIconAsync(Windows.UI.Notifications.UserNotification toast, string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;

        if (_appIconCache.TryGetValue(appName, out var cached))
        {
            return cached;
        }

        ImageSource? icon = null;

        // 1. Try WinRT AppInfo.DisplayInfo.GetLogo (Snipping Tool, ChatGPT, Windows Store Apps)
        try
        {
            var logoRef = toast.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(96, 96));
            if (logoRef != null)
            {
                using var ras = await logoRef.OpenReadAsync();
                using var stream = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ras);
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                icon = NormalizeBitmap(bmp);
            }
        }
        catch
        {
            // Ignore WinRT stream errors
        }

        // 2. If null, try searching apps.json for executable / icon source
        if (icon == null)
        {
            try
            {
                var appsPath = Path.Combine(AppContext.BaseDirectory, "Data", "apps.json");
                if (File.Exists(appsPath))
                {
                    var json = await File.ReadAllTextAsync(appsPath);
                    var index = json.IndexOf($"\"displayName\": \"{appName}\"", StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        var targetKey = "\"target\": \"";
                        var targetIdx = json.IndexOf(targetKey, index, Math.Min(json.Length - index, 1200), StringComparison.OrdinalIgnoreCase);
                        if (targetIdx >= 0)
                        {
                            var start = targetIdx + targetKey.Length;
                            var end = json.IndexOf('"', start);
                            if (end > start)
                            {
                                var targetPath = json[start..end].Replace("\\\\", "\\");
                                if (File.Exists(targetPath))
                                {
                                    _appTargetCache[appName] = targetPath;
                                    icon = CreateNormalizedBitmap(Glassquid.Platform.Shell.IconExtractor.Extract(targetPath));
                                }
                            }
                        }
                    }
                }
            }
            catch { /* Ignore */ }
        }

        // 3. If null, check running processes by appName
        if (icon == null)
        {
            try
            {
                var cleanName = appName.Replace(" ", "");
                var procs = Process.GetProcesses();
                var proc = procs.FirstOrDefault(p =>
                    p.ProcessName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (proc != null && proc.MainModule?.FileName is { Length: > 0 } exePath && File.Exists(exePath))
                {
                    _appTargetCache[appName] = exePath;
                    icon = CreateNormalizedBitmap(Glassquid.Platform.Shell.IconExtractor.Extract(exePath));
                }
            }
            catch { /* Ignore process access errors */ }
        }

        // 4. If null, check Start Menu shortcuts
        if (icon == null)
        {
            try
            {
                var startMenuDirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                };
                foreach (var dir in startMenuDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    var lnk = Directory.GetFiles(dir, $"*{appName}*.lnk", SearchOption.AllDirectories).FirstOrDefault();
                    if (lnk != null)
                    {
                        _appTargetCache[appName] = lnk;
                        icon = CreateNormalizedBitmap(Glassquid.Platform.Shell.IconExtractor.Extract(lnk));
                        if (icon != null) break;
                    }
                }
            }
            catch { /* Ignore shortcut errors */ }
        }

        _appIconCache[appName] = icon;
        return icon;
    }

    private static bool AreNotificationListsEqual(List<NotificationItem> a, List<NotificationItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Title != b[i].Title || a[i].Body != b[i].Body || a[i].TimeAgo != b[i].TimeAgo || a[i].AppIcon != b[i].AppIcon)
                return false;
        }
        return true;
    }

    private static string CleanAppId(string appId)
    {
        int exclam = appId.IndexOf('!');
        var clean = exclam >= 0 ? appId[..exclam] : appId;
        int dot = clean.LastIndexOf('.');
        if (dot >= 0 && dot < clean.Length - 1) clean = clean[(dot + 1)..];
        int underscore = clean.IndexOf('_');
        if (underscore >= 0) clean = clean[..underscore];
        return string.IsNullOrWhiteSpace(clean) ? "Ứng dụng" : clean;
    }

    public void DismissNotification(uint id)
    {
        lock (_lock)
        {
            _dismissedIds.Add(id);
            _cachedNotifications.RemoveAll(n => n.Id == id);
        }

        NotificationsChanged?.Invoke(this, EventArgs.Empty);

        Task.Run(() =>
        {
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                listener.RemoveNotification(id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid NotificationManager] RemoveNotification error: {ex.Message}");
            }
        });
    }

    public void ClearAll()
    {
        List<uint> idsToRemove;
        lock (_lock)
        {
            idsToRemove = _cachedNotifications.Select(n => n.Id).ToList();
            foreach (var id in idsToRemove)
            {
                _dismissedIds.Add(id);
            }
            _cachedNotifications.Clear();
        }

        NotificationsChanged?.Invoke(this, EventArgs.Empty);

        Task.Run(async () =>
        {
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;

                // 1. Remove all currently known notification IDs individually
                foreach (var id in idsToRemove)
                {
                    try
                    {
                        listener.RemoveNotification(id);
                    }
                    catch { }
                }

                // 2. Query any remaining toasts and remove them individually
                var toasts = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                if (toasts != null)
                {
                    foreach (var toast in toasts)
                    {
                        try
                        {
                            listener.RemoveNotification(toast.Id);
                            lock (_lock)
                            {
                                _dismissedIds.Add(toast.Id);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid NotificationManager] ClearAll error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Activates or launches the application that generated the given notification item.
    /// Handles packaged UWP/MSIX Store apps, desktop Win32 apps, running processes, and URI schemes.
    /// </summary>
    public static bool OpenNotificationApp(NotificationItem item)
    {
        if (item == null) return false;

        // 0. Check if the notification registered an authoritative launch URI in Windows Notification Database (wpndatabase.db).
        // For instance, Snipping Tool screenshot toasts store:
        // ms-screensketch:edit?&filePath=...&isTemporary=false&saved=true&source=Toast
        var dbLaunchUri = NotificationDatabaseReader.TryGetLaunchUri(item.Id);
        if (!string.IsNullOrWhiteSpace(dbLaunchUri))
        {
            try
            {
                var psi = new ProcessStartInfo(dbLaunchUri) { UseShellExecute = true };
                Process.Start(psi);

                if (IsScreenshotNotification(item))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(400);
                        SnippingToolImageOpener.BringToForeground();
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Notifications] Direct db launch URI failed: {ex.Message}");
            }
        }

        // The Snipping Tool's screenshot toast represents a saved image, not a
        // request to open the app's empty home screen. Open the matching file directly.
        if (IsScreenshotNotification(item))
        {
            var screenshot = FindScreenshotForNotification(item);
            if (screenshot is null)
            {
                Debug.WriteLine("[Glassquid Notifications] Could not find the screenshot file for this notification.");
                return false;
            }

            try
            {
                if (SnippingToolImageOpener.TryOpen(screenshot)) return true;
                Debug.WriteLine("[Glassquid Notifications] Snipping Tool did not open the saved screenshot.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Notifications] Open screenshot failed: {ex.Message}");
                return false;
            }
        }

        // 1. Try activating existing window by AUMID first (packaged / Store / modern apps)
        if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
        {
            try
            {
                var windows = WindowLocator.FindWindowsForAumid(item.AppUserModelId);
                if (windows.Count > 0)
                {
                    if (WindowLocator.TryBringToForeground(windows[0].Handle))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid NotificationManager] FindWindowsForAumid error: {ex.Message}");
            }
        }

        // 2. Try activating existing window by matching running processes
        try
        {
            var cleanName = item.AppName.Replace(" ", "");
            var procs = Process.GetProcesses();
            var matchedProcs = procs.Where(p =>
                p.ProcessName.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                p.ProcessName.Equals(item.AppName, StringComparison.OrdinalIgnoreCase) ||
                (item.AppName.Length >= 4 && p.ProcessName.StartsWith(item.AppName.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            ).ToList();

            if (matchedProcs.Count > 0)
            {
                // First try direct main window handle
                foreach (var proc in matchedProcs)
                {
                    try
                    {
                        if (proc.MainWindowHandle != 0 && WindowLocator.TryBringToForeground(proc.MainWindowHandle))
                        {
                            return true;
                        }
                    }
                    catch { }
                }

                // Next enumerate top-level candidate windows for matching process ID
                var matchedPids = new HashSet<uint>(matchedProcs.Select(p => (uint)p.Id));
                foreach (var win in WindowLocator.EnumerateTopLevelWindows())
                {
                    if (matchedPids.Contains(win.ProcessId))
                    {
                        if (WindowLocator.TryBringToForeground(win.Handle))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid NotificationManager] Process window focus error: {ex.Message}");
        }

        // 3. Launch via AUMID (shell:AppsFolder\<AUMID>) - universal Windows shell activation
        if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{item.AppUserModelId}",
                    UseShellExecute = true,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid NotificationManager] Shell AUMID launch error: {ex.Message}");
            }
        }

        // 4. Launch via TargetExecutable or shortcut path if resolved
        if (!string.IsNullOrWhiteSpace(item.TargetExecutable) && File.Exists(item.TargetExecutable))
        {
            try
            {
                var psi = new ProcessStartInfo(item.TargetExecutable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(item.TargetExecutable),
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid NotificationManager] TargetExecutable launch error: {ex.Message}");
            }
        }

        // 5. Try searching Start Menu shortcuts (.lnk)
        try
        {
            var startMenuDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            };
            foreach (var dir in startMenuDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var lnk = Directory.GetFiles(dir, $"*{item.AppName}*.lnk", SearchOption.AllDirectories).FirstOrDefault();
                if (lnk == null && item.AppName.Contains(' '))
                {
                    var firstWord = item.AppName.Split(' ')[0];
                    if (firstWord.Length >= 3)
                    {
                        lnk = Directory.GetFiles(dir, $"*{firstWord}*.lnk", SearchOption.AllDirectories).FirstOrDefault();
                    }
                }
                if (lnk != null)
                {
                    Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid NotificationManager] Shortcut search launch error: {ex.Message}");
        }

        // 6. Try well-known URI protocols
        try
        {
            var protocol = ResolveWellKnownProtocol(item.AppName, item.Title);
            if (!string.IsNullOrWhiteSpace(protocol))
            {
                Process.Start(new ProcessStartInfo(protocol) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid NotificationManager] Protocol launch error: {ex.Message}");
        }

        return false;
    }

    public static bool IsScreenshotNotification(NotificationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var identity = $"{item.AppName} {item.AppUserModelId} {item.PackageFamilyName}";
        var fromSnippingTool = identity.Contains("Snipping", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Công cụ Cắt", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Cắt màn hình", StringComparison.OrdinalIgnoreCase);
        var text = $"{item.Title} {item.Body}";
        var mentionsScreenshot = text.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("screen capture", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ảnh chụp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ảnh màn hình", StringComparison.OrdinalIgnoreCase);
        var describesSaveOrCopy = text.Contains("copied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("clipboard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("saved", StringComparison.OrdinalIgnoreCase)
            || text.Contains("đã lưu", StringComparison.OrdinalIgnoreCase)
            || text.Contains("bảng tạm", StringComparison.OrdinalIgnoreCase);
        var snippingSaveMessage = text.Contains("saved to screenshots folder", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lưu vào thư mục ảnh chụp màn hình", StringComparison.OrdinalIgnoreCase);
        return mentionsScreenshot && describesSaveOrCopy && (fromSnippingTool || snippingSaveMessage);
    }

    private static string? FindScreenshotForNotification(NotificationItem item)
    {
        var candidateFolders = new List<string>();
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures))
        {
            candidateFolders.Add(Path.Combine(pictures, "Screenshots"));
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidateFolders.Add(Path.Combine(userProfile, "OneDrive", "Pictures", "Screenshots"));
        }

        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"
        };

        var referenceTime = item.CreatedAt?.UtcDateTime ?? DateTime.UtcNow;

        foreach (var folder in candidateFolders)
        {
            if (!Directory.Exists(folder)) continue;

            try
            {
                var match = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        try
                        {
                            var info = new FileInfo(path);
                            return (Info: info, Valid: info.Exists && info.Length > 0
                                && supportedExtensions.Contains(info.Extension));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return (Info: (FileInfo?)null, Valid: false);
                        }
                    })
                    .Where(candidate => candidate.Valid && candidate.Info is not null)
                    .Select(candidate => (candidate.Info!, Difference: Math.Abs((candidate.Info!.LastWriteTimeUtc - referenceTime).TotalSeconds)))
                    .OrderBy(candidate => candidate.Difference)
                    .Select(candidate => candidate.Item1.FullName)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Glassquid Notifications] Screenshot folder scan failed: {ex.Message}");
            }
        }

        return null;
    }

    private static class NotificationDatabaseReader
    {
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(
            byte[] filenameUtf8,
            out nint ppDb,
            int flags,
            nint zVfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close(nint db);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_prepare_v2(
            nint db,
            byte[] zSqlUtf8,
            int nByte,
            out nint ppStmt,
            nint pzTail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_step(nint stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern nint sqlite3_column_blob(nint stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_column_bytes(nint stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_finalize(nint stmt);

        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_ROW = 100;

        public static string? TryGetLaunchUri(uint notificationId)
        {
            try
            {
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Notifications", "wpndatabase.db");

                if (!File.Exists(dbPath)) return null;

                var dbPathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
                int rc = sqlite3_open_v2(dbPathBytes, out var db, SQLITE_OPEN_READONLY, 0);
                if (rc != 0 || db == 0) return null;

                try
                {
                    var sqlBytes = Encoding.UTF8.GetBytes($"SELECT Payload FROM Notification WHERE Id = {notificationId} LIMIT 1\0");
                    rc = sqlite3_prepare_v2(db, sqlBytes, -1, out var stmt, 0);
                    if (rc == 0 && stmt != 0)
                    {
                        try
                        {
                            if (sqlite3_step(stmt) == SQLITE_ROW)
                            {
                                var blobPtr = sqlite3_column_blob(stmt, 0);
                                var bytes = sqlite3_column_bytes(stmt, 0);
                                if (blobPtr != 0 && bytes > 0)
                                {
                                    var buffer = new byte[bytes];
                                    Marshal.Copy(blobPtr, buffer, 0, bytes);
                                    var xml = Encoding.UTF8.GetString(buffer);

                                    var match = Regex.Match(xml, @"launch=""([^""]+)""");
                                    if (match.Success)
                                    {
                                        var rawLaunch = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                                        if (rawLaunch.Contains(':') && !rawLaunch.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
                                        {
                                            return rawLaunch;
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _ = sqlite3_finalize(stmt);
                        }
                    }
                }
                finally
                {
                    _ = sqlite3_close(db);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Glassquid Notifications] Read wpndatabase.db failed: {ex.Message}");
            }

            return null;
        }
    }

    private static string? ResolveWellKnownProtocol(string appName, string? title)
    {
        var combined = $"{appName} {title}";

        if (combined.Contains("Snipping", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cắt", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-screenclip:";
        }

        if (combined.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cài đặt", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-settings:";
        }

        if (combined.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cửa hàng", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-windows-store:";
        }

        if (combined.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Máy tính", StringComparison.OrdinalIgnoreCase))
        {
            return "calculator:";
        }

        if (combined.Contains("Clock", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Báo thức", StringComparison.OrdinalIgnoreCase))
        {
            return "ms-clock:";
        }

        if (combined.Contains("Calendar", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Lịch", StringComparison.OrdinalIgnoreCase))
        {
            return "outlookcal:";
        }

        if (combined.Contains("Mail", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Outlook", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Thư", StringComparison.OrdinalIgnoreCase))
        {
            return "mailto:";
        }

        if (combined.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "spotify:";
        }

        if (combined.Contains("Discord", StringComparison.OrdinalIgnoreCase))
        {
            return "discord:";
        }

        if (combined.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
        {
            return "tg:";
        }

        return null;
    }

    public void AddTestNotification()
    {
        uint newId = (uint)(Random.Shared.Next(1000, 9999));
        var sampleNotifications = new[]
        {
            new NotificationItem(newId, "Tin nhắn", "Có tin nhắn mới", "Bạn nhận được một tin nhắn từ đồng nghiệp trên Teams.", "Vừa xong", "\uE715", Color.FromRgb(24, 104, 220)),
            new NotificationItem(newId, "Lịch biểu", "Cuộc họp sắp diễn ra", "Thảo luận cập nhật giao diện Glassquid bắt đầu sau 15 phút.", "Vừa xong", "\uE787", Color.FromRgb(255, 149, 0)),
            new NotificationItem(newId, "Microsoft Store", "Đã cập nhật ứng dụng", "12 ứng dụng trong thư viện của bạn vừa được cập nhật bản mới.", "Vừa xong", "\uE719", Color.FromRgb(10, 132, 255)),
        };

        var item = sampleNotifications[Random.Shared.Next(sampleNotifications.Length)];
        lock (_lock)
        {
            _cachedNotifications.Insert(0, item);
        }
        NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTimeAgo(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 45) return "Vừa xong";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} phút trước";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} giờ trước";
        return time.ToString("dd/MM", CultureInfo.InvariantCulture);
    }

    public static string ResolveAppGlyph(string appName, string? title = null)
    {
        var combined = $"{appName} {title}";

        // Screenshots and screen captures
        if (combined.Contains("Snipping", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cắt", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Chụp màn hình", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Lightshot", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("ShareX", StringComparison.OrdinalIgnoreCase))
            return "\uE722"; // Camera / Crop

        // Gaming platforms, games, and game overlays
        if (combined.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Epic", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Valorant", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("League", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Genshin", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Honkai", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Roblox", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Game", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Play", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("EA", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("GOG", StringComparison.OrdinalIgnoreCase))
            return "\uE7FC"; // Gamepad

        // Chat, social and messaging
        if (combined.Contains("Discord", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Zalo", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Teams", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Messenger", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Slack", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Skype", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Viber", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Chat", StringComparison.OrdinalIgnoreCase))
            return "\uE8BD"; // Chat bubble

        // Music, media, video
        if (combined.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Nhạc", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Apple Music", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase))
            return "\uE768"; // Music note

        // Web browsers
        if (combined.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cốc Cốc", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Opera", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Browser", StringComparison.OrdinalIgnoreCase))
            return "\uE774"; // Globe

        // Email
        if (combined.Contains("Mail", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Outlook", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Gmail", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Thư", StringComparison.OrdinalIgnoreCase))
            return "\uE715"; // Mail

        // System, security, updates
        if (combined.Contains("Security", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Bảo mật", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Defender", StringComparison.OrdinalIgnoreCase))
            return "\uE72E"; // Shield

        if (combined.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cập nhật", StringComparison.OrdinalIgnoreCase))
            return "\uE777"; // Sync / Update

        if (combined.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Cửa hàng", StringComparison.OrdinalIgnoreCase))
            return "\uE719"; // Shopping bag

        if (combined.Contains("Calendar", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Lịch", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Reminder", StringComparison.OrdinalIgnoreCase))
            return "\uE787"; // Calendar

        if (combined.Contains("Battery", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Pin", StringComparison.OrdinalIgnoreCase))
            return "\uE83F"; // Battery

        return "\uE7E7"; // Fluent App glyph
    }

    public static Color ResolveAppColor(string appName)
    {
        // Screenshot tools
        if (appName.Contains("Snipping", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Screenshot", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Screen Sketch", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Cắt", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Lightshot", StringComparison.OrdinalIgnoreCase))
            return Color.FromRgb(245, 158, 11); // Amber 500

        // Games and Gaming Overlays
        if (appName.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(27, 40, 56);
        if (appName.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(88, 101, 242);
        if (appName.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(49, 49, 49);
        if (appName.Contains("Riot", StringComparison.OrdinalIgnoreCase) || appName.Contains("Valorant", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(235, 0, 41);
        if (appName.Contains("Genshin", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(79, 70, 229);
        if (appName.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(16, 124, 65);

        // Social and chat
        if (appName.Contains("Zalo", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(0, 104, 255);
        if (appName.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(42, 171, 238);
        if (appName.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(98, 100, 167);
        if (appName.Contains("Messenger", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(0, 132, 255);
        if (appName.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(30, 215, 96);

        // Productivity & Windows
        if (appName.Contains("Security", StringComparison.OrdinalIgnoreCase) || appName.Contains("Bảo mật", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(52, 199, 89);
        if (appName.Contains("Update", StringComparison.OrdinalIgnoreCase) || appName.Contains("Cập nhật", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(10, 132, 255);
        if (appName.Contains("Mail", StringComparison.OrdinalIgnoreCase) || appName.Contains("Outlook", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(0, 120, 212);
        if (appName.Contains("Store", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(14, 165, 233);
        if (appName.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(0, 120, 215);
        if (appName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return Color.FromRgb(234, 67, 53);

        // Fallback for any newly installed app or game: deterministic vibrant HSL color
        var hash = Math.Abs(appName.GetHashCode());
        var hue = hash % 360;
        return HslToRgb(hue, 0.72f, 0.54f);
    }

    private static Color HslToRgb(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s;
        float x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        float m = l - c / 2;
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public void Dispose()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;
    }
}
