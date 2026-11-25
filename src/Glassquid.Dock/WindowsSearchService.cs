using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Platform.Shell;
using Microsoft.Win32;

namespace Glassquid.Dock;

/// <summary>
/// Đại diện cho một mục tìm kiếm hoặc mục mở gần đây (App hoặc File).
/// </summary>
public sealed class SearchItemModel : INotifyPropertyChanged
{
    public required string DisplayName { get; init; }
    public required string TargetPath { get; init; }
    public string? Arguments { get; init; }
    public required string Subtitle { get; init; }
    public required string RelativeTimeText { get; init; }
    public DateTime LastModified { get; init; }
    public bool IsApp { get; init; }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Bản ghi lưu trữ lịch sử tìm kiếm / mở gần đây của người dùng.
/// </summary>
public sealed class SearchHistoryRecord
{
    public required string DisplayName { get; set; }
    public required string TargetPath { get; set; }
    public string? Arguments { get; set; }
    public required string Subtitle { get; set; }
    public bool IsApp { get; set; }
    public DateTime TimeUtc { get; set; }
}

/// <summary>
/// Dịch vụ truy vấn các ứng dụng, tài liệu gần đây và tìm kiếm tệp tin qua Windows Search API.
/// </summary>
public static class WindowsSearchService
{
    private static readonly SearchIconLruCache IconCache = new(maxBytes: 16L * 1024 * 1024, maxCount: 512);
    private static readonly Dictionary<string, string> DesktopAndStartMenuShortcuts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> AppsByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> AppsByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object HistoryLock = new();
    private static readonly object AppsLock = new();
    private static DateTime _lastAppsScan = DateTime.MinValue;
    private static bool _appsCacheLoadedFromDisk;
    private static bool _appsScanInProgress;

    private static readonly Dictionary<string, string> KnownFolderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}", Environment.GetFolderPath(Environment.SpecialFolder.Programs) },
        { "{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}", Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) },
        { "{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) },
        { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) },
        { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System) },
        { "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
        { "{9E3995AB-1F9C-4F13-B827-48B24B6C7174}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar") }
    };

    private static readonly Dictionary<string, string> WellKnownUwpNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", "Snipping Tool" },
        { "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal" },
        { "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Notepad" },
        { "Microsoft.WindowsCamera_8wekyb3d8bbwe!App", "Camera" },
        { "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", "Photos" },
        { "Microsoft.Paint_8wekyb3d8bbwe!App", "Paint" },
        { "Microsoft.Windows.Explorer", "File Explorer" },
        { "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", "Settings" },
        { "Microsoft.Windows.RemoteDesktop", "Remote Desktop" },
        { "Microsoft.WindowsMediaPlayer32", "Windows Media Player" }
    };

    private static readonly HashSet<string> ValidDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf",
        ".txt", ".md", ".json", ".xml", ".yaml", ".csv",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg",
        ".mp4", ".mkv", ".avi", ".mov", ".mp3", ".wav",
        ".zip", ".rar", ".7z", ".tar", ".gz"
    };

    private static string GetHistoryFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Glassquid");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "recent-searches.json");
    }

    private static string GetDockAppsCacheFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Glassquid");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "dock-apps-cache.json");
    }

    public sealed class SearchDockAppsCacheData
    {
        public int Version { get; set; } = 1;
        public DateTime CachedAtUtc { get; set; }
        public List<SearchDockAppRecord> Records { get; set; } = [];
    }

    public sealed class SearchDockAppRecord
    {
        public required string Name { get; set; }
        public required string TargetPath { get; set; }
        public string? ShortcutPath { get; set; }
    }

    /// <summary>
    /// Ghi nhận mục người dùng vừa tìm kiếm và chọn mở vào lịch sử.
    /// </summary>
    public static void RecordSearchHistory(SearchItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (HistoryLock)
        {
            try
            {
                var filePath = GetHistoryFilePath();
                var history = new List<SearchHistoryRecord>();
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var existing = JsonSerializer.Deserialize<List<SearchHistoryRecord>>(json, JsonOptions);
                        if (existing is not null) history = existing;
                    }
                }

                // Loại bỏ mục trùng lặp nếu đã có trước đó
                history.RemoveAll(h => string.Equals(h.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(h.TargetPath, item.TargetPath, StringComparison.OrdinalIgnoreCase));

                // Đưa mục vừa chọn lên đầu tiên
                history.Insert(0, new SearchHistoryRecord
                {
                    DisplayName = item.DisplayName,
                    TargetPath = item.TargetPath,
                    Arguments = item.Arguments,
                    Subtitle = item.Subtitle,
                    IsApp = item.IsApp,
                    TimeUtc = DateTime.UtcNow
                });

                // Giữ tối đa 30 mục
                if (history.Count > 30)
                {
                    history = history.Take(30).ToList();
                }

                var serialized = JsonSerializer.Serialize(history, JsonOptions);
                File.WriteAllText(filePath, serialized);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] RecordSearchHistory error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Đọc lịch sử tìm kiếm do người dùng đã thực hiện gần đây.
    /// </summary>
    private static List<SearchHistoryRecord> LoadSearchHistory()
    {
        lock (HistoryLock)
        {
            try
            {
                var filePath = GetHistoryFilePath();
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var list = JsonSerializer.Deserialize<List<SearchHistoryRecord>>(json, JsonOptions);
                        if (list is not null) return list;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] LoadSearchHistory error: {ex.Message}");
            }
            return [];
        }
    }

    /// <summary>
    /// Lấy danh sách các tệp tin và ứng dụng mở gần đây từ Windows.
    /// </summary>
    public static async Task<IReadOnlyList<SearchItemModel>> GetRecentItemsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<SearchItemModel>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Quét ứng dụng hệ thống (Start Menu + shell:AppsFolder)
            EnsureAppsCache();

            // 2. ƯU TIÊN SỐ 1: Các mục người dùng VỪA TÌM KIẾM gần đây
            var userHistory = LoadSearchHistory();
            foreach (var h in userHistory)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (seenNames.Contains(h.DisplayName) || seenPaths.Contains(h.TargetPath)) continue;

                // Kiểm tra xem ứng dụng / tệp tin còn tồn tại không
                if (!h.IsApp && !File.Exists(h.TargetPath) && !Directory.Exists(h.TargetPath))
                    continue;

                seenNames.Add(h.DisplayName);
                seenPaths.Add(h.TargetPath);

                results.Add(new SearchItemModel
                {
                    DisplayName = h.DisplayName,
                    TargetPath = h.TargetPath,
                    Arguments = h.Arguments,
                    Subtitle = h.Subtitle,
                    RelativeTimeText = FormatRelativeTime(h.TimeUtc),
                    LastModified = h.TimeUtc,
                    IsApp = h.IsApp
                });
            }

            // 3. Thu thập thêm lịch sử ứng dụng từ Windows (UserAssist + JumplistData)
            var appTimes = CollectAppUsageTimes();
            foreach (var (rawKey, timeUtc) in appTimes.OrderByDescending(kv => kv.Value))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (results.Count >= 18) break;

                if (IsIgnoredSystemEntry(rawKey)) continue;

                var (displayName, targetPath) = ResolveAppDetails(rawKey);
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(targetPath))
                    continue;

                if (seenNames.Contains(displayName) || seenPaths.Contains(targetPath))
                    continue;

                seenNames.Add(displayName);
                seenPaths.Add(targetPath);

                var subtitle = IsSystemApp(displayName, targetPath) ? "Hệ thống" : "Ứng dụng";
                results.Add(new SearchItemModel
                {
                    DisplayName = displayName,
                    TargetPath = targetPath,
                    Subtitle = subtitle,
                    RelativeTimeText = FormatRelativeTime(timeUtc),
                    LastModified = timeUtc,
                    IsApp = true
                });
            }

            // 4. Thu thập các tệp tin / tài liệu thực sự mở gần đây từ Windows Recent
            try
            {
                var recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (Directory.Exists(recentFolder))
                {
                    var dirInfo = new DirectoryInfo(recentFolder);
                    var lnkFiles = dirInfo.GetFiles("*.lnk")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(60);

                    foreach (var file in lnkFiles)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        if (results.Count >= 25) break;

                        var rawName = Path.GetFileNameWithoutExtension(file.Name);
                        if (IsIgnoredRecentShortcut(rawName)) continue;

                        string? targetPath = null;
                        try
                        {
                            var resolved = ShortcutResolver.Resolve(file.FullName);
                            if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.Target))
                            {
                                targetPath = resolved.Target;
                            }
                        }
                        catch { }

                        targetPath ??= file.FullName;

                        if (!File.Exists(targetPath)) continue;

                        var ext = Path.GetExtension(targetPath);
                        if (!ValidDocExtensions.Contains(ext)) continue;

                        var displayName = Path.GetFileName(targetPath);
                        if (seenNames.Contains(displayName) || seenPaths.Contains(targetPath))
                            continue;

                        seenNames.Add(displayName);
                        seenPaths.Add(targetPath);

                        results.Add(new SearchItemModel
                        {
                            DisplayName = displayName,
                            TargetPath = targetPath,
                            Subtitle = GetCategorySubtitle(targetPath, isApp: false),
                            RelativeTimeText = FormatRelativeTime(file.LastWriteTimeUtc),
                            LastModified = file.LastWriteTimeUtc,
                            IsApp = false
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] Error reading Recent folder: {ex.Message}");
            }

            return (IReadOnlyList<SearchItemModel>)results.Take(25).ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Tìm kiếm ứng dụng và tệp tin qua Start Menu và Windows Search Indexer API.
    /// </summary>
    public static async Task<IReadOnlyList<SearchItemModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetRecentItemsAsync(cancellationToken);
        }

        var trimmed = query.Trim();
        return await Task.Run(() =>
        {
            var results = new List<SearchItemModel>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            EnsureAppsCache();

            // 1. Tìm trong danh mục Ứng dụng đã cài đặt (Ưu tiên các phím tắt .lnk thực tế)
            KeyValuePair<string, string>[] appsSnapshot;
            lock (AppsLock)
            {
                appsSnapshot = AppsByName.ToArray();
            }

            foreach (var kvp in appsSnapshot)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var appName = kvp.Key;
                var appPath = kvp.Value;

                if (appName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    if (seenNames.Contains(appName)) continue;
                    seenNames.Add(appName);
                    seenPaths.Add(appPath);

                    results.Add(new SearchItemModel
                    {
                        DisplayName = appName,
                        TargetPath = appPath,
                        Subtitle = "Ứng dụng",
                        RelativeTimeText = "Ứng dụng",
                        LastModified = DateTime.UtcNow,
                        IsApp = true
                    });

                    if (results.Count >= 10) break;
                }
            }

            // 2. Tìm qua Windows Search OLE DB Provider (SystemIndex) cho tệp tin
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    var safeQuery = trimmed.Replace("'", "''");
                    cmd.CommandText = $"SELECT TOP 25 System.ItemName, System.ItemUrl, System.Kind, System.DateModified FROM SystemIndex WHERE SCOPE='file:' AND System.ItemName LIKE '%{safeQuery}%' ORDER BY System.DateModified DESC";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        if (results.Count >= 25) break;

                        var itemName = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var itemUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var dateMod = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3);

                        if (string.IsNullOrWhiteSpace(itemUrl) || string.IsNullOrWhiteSpace(itemName))
                            continue;

                        if (itemName.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || itemName.Contains("--"))
                            continue;

                        var filePath = ConvertFileUrlToLocalPath(itemUrl);
                        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) && !Directory.Exists(filePath))
                            continue;

                        var ext = Path.GetExtension(filePath);
                        var isApp = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase);

                        var displayName = Path.GetFileNameWithoutExtension(filePath);
                        if (seenNames.Contains(displayName) || seenPaths.Contains(filePath))
                            continue;

                        seenNames.Add(displayName);
                        seenPaths.Add(filePath);

                        results.Add(new SearchItemModel
                        {
                            DisplayName = displayName,
                            TargetPath = filePath,
                            Subtitle = GetCategorySubtitle(filePath, isApp),
                            RelativeTimeText = dateMod != DateTime.MinValue ? FormatRelativeTime(dateMod.ToUniversalTime()) : "Tệp tin",
                            LastModified = dateMod,
                            IsApp = isApp
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] OleDb Search query error: {ex.Message}");
            }

            return (IReadOnlyList<SearchItemModel>)results;
        }, cancellationToken);
    }

    /// <summary>
    /// Khởi chạy một ứng dụng hoặc tệp tin với độ tin cậy tuyệt đối.
    /// </summary>
    public static bool LaunchAppOrFile(SearchItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Ghi lại vào lịch sử tìm kiếm ngay lập tức
        RecordSearchHistory(item);

        var target = item.TargetPath;

        // Bước 1: Nếu là đường dẫn tệp tin hoặc .lnk thực tế trên ổ đĩa -> Mở trực tiếp
        if (File.Exists(target) || Directory.Exists(target))
        {
            try
            {
                var workDir = File.Exists(target) ? (Path.GetDirectoryName(target) ?? string.Empty) : target;
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    Arguments = item.Arguments ?? string.Empty,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] Direct launch failed: {ex.Message}");
            }
        }

        // Bước 2: Giải mã KnownFolder GUID nếu đường dẫn chứa GUID (như {6D809377...}\CPUID\CPU-Z\cpuz.exe)
        var resolvedGuidPath = ResolveKnownFolderGuids(target);
        if (File.Exists(resolvedGuidPath) || Directory.Exists(resolvedGuidPath))
        {
            try
            {
                var workDir = File.Exists(resolvedGuidPath) ? (Path.GetDirectoryName(resolvedGuidPath) ?? string.Empty) : resolvedGuidPath;
                Process.Start(new ProcessStartInfo
                {
                    FileName = resolvedGuidPath,
                    Arguments = item.Arguments ?? string.Empty,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                });
                return true;
            }
            catch { }
        }

        // Bước 3: Tìm kiếm phím tắt .lnk trong Start Menu hoặc Desktop theo tên ứng dụng
        if (item.IsApp)
        {
            string? shortcutPath = null;
            lock (AppsLock)
            {
                DesktopAndStartMenuShortcuts.TryGetValue(item.DisplayName, out shortcutPath);
            }

            if (!string.IsNullOrWhiteSpace(shortcutPath) && File.Exists(shortcutPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = shortcutPath,
                        Arguments = item.Arguments ?? string.Empty,
                        WorkingDirectory = Path.GetDirectoryName(shortcutPath) ?? string.Empty,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch { }
            }
        }

        // Bước 4: Nếu là ứng dụng Windows Store / UWP (shell:AppsFolder) -> Dùng COM Shell.Application InvokeVerb
        var appId = target;
        if (appId.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            appId = appId["shell:AppsFolder\\".Length..];
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                dynamic? folder = shell?.NameSpace("shell:AppsFolder");
                dynamic? folderItem = folder?.ParseName(appId);

                if (folderItem is null && folder is not null)
                {
                    foreach (var fi in folder.Items())
                    {
                        if (string.Equals((string)fi.Name, item.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals((string)fi.Path, appId, StringComparison.OrdinalIgnoreCase))
                        {
                            folderItem = fi;
                            break;
                        }
                    }
                }

                if (folderItem is not null)
                {
                    try
                    {
                        folderItem.InvokeVerb("Open");
                        return true;
                    }
                    catch { }

                    try
                    {
                        folderItem.InvokeVerb();
                        return true;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] Shell InvokeVerb failed: {ex.Message}");
        }

        // Bước 5: Fallback qua explorer.exe shell:AppsFolder\<appId>
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{appId}",
                UseShellExecute = true
            });
            return true;
        }
        catch { }

        // Bước 6: Fallback khởi chạy lệnh hệ thống (như cmd, powershell, wt, notepad, calc)
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.DisplayName,
                UseShellExecute = true
            });
            return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Nạp icon bất đồng bộ cho danh sách item.
    /// </summary>
    public static async Task LoadIconsAsync(IEnumerable<SearchItemModel> items, Action<SearchItemModel, ImageSource> onIconLoaded, CancellationToken cancellationToken = default)
    {
        var itemList = items.ToList();
        await Task.Run(() =>
        {
            foreach (var item in itemList)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var icon = GetOrExtractIcon(item.TargetPath, item.DisplayName);
                if (icon is not null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            item.Icon = icon;
                            onIconLoaded(item, icon);
                        }
                    }));
                }
            }
        }, cancellationToken);
    }

    private static ImageSource? GetOrExtractIcon(string path, string displayName)
    {
        if (IconCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            // Nếu có shortcut .lnk thực tế, ưu tiên trích xuất từ .lnk
            var extractPath = path;
            string? lnkPath = null;
            lock (AppsLock)
            {
                DesktopAndStartMenuShortcuts.TryGetValue(displayName, out lnkPath);
            }

            if (!File.Exists(extractPath) && !string.IsNullOrWhiteSpace(lnkPath) && File.Exists(lnkPath))
            {
                extractPath = lnkPath;
            }

            var extracted = IconExtractor.Extract(extractPath);
            if (extracted is not null && extracted.VisiblePixelCount > 0)
            {
                var bmp = BitmapSource.Create(
                    extracted.Width, extracted.Height,
                    96, 96, PixelFormats.Bgra32, null,
                    extracted.Bgra, extracted.Width * 4);
                bmp.Freeze();
                IconCache.Set(path, bmp);
                return bmp;
            }

            if (File.Exists(extractPath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(extractPath);
                if (sysIcon is not null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    IconCache.Set(path, bmp);
                    return bmp;
                }
            }
        }
        catch { }

        return null;
    }

    private static void EnsureAppsCache()
    {
        // 1. Nạp từ cache cục bộ (dock-apps-cache.json) nếu bộ nhớ chưa được nạp
        if (!_appsCacheLoadedFromDisk)
        {
            lock (AppsLock)
            {
                if (!_appsCacheLoadedFromDisk)
                {
                    LoadDockAppsCacheFromDisk();
                    _appsCacheLoadedFromDisk = true;
                }
            }
        }

        // 2. Kiểm tra xem có cần quét làm mới không
        bool needsRefresh;
        lock (AppsLock)
        {
            needsRefresh = AppsByName.Count == 0 || (DateTime.UtcNow - _lastAppsScan >= TimeSpan.FromMinutes(10));
        }

        if (!needsRefresh)
        {
            return;
        }

        // 3. Nếu đã có dữ liệu trong RAM (từ disk cache hoặc lần quét trước),
        // chạy quét làm mới ngầm trên background thread để không làm nghẽn UI/search caller!
        bool hasAppsInMemory;
        lock (AppsLock)
        {
            hasAppsInMemory = AppsByName.Count > 0;
        }

        if (hasAppsInMemory)
        {
            Task.Run(() => RescanAppsAndUpdateCache());
        }
        else
        {
            RescanAppsAndUpdateCache();
        }
    }

    private static void LoadDockAppsCacheFromDisk()
    {
        try
        {
            var filePath = GetDockAppsCacheFilePath();
            if (!File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var cache = JsonSerializer.Deserialize<SearchDockAppsCacheData>(json);
            if (cache is null || cache.Version != 1 || cache.Records.Count == 0)
            {
                return;
            }

            DesktopAndStartMenuShortcuts.Clear();
            AppsByName.Clear();
            AppsByPath.Clear();

            foreach (var rec in cache.Records)
            {
                if (string.IsNullOrWhiteSpace(rec.Name) || string.IsNullOrWhiteSpace(rec.TargetPath))
                    continue;

                // Nếu là file vật lý (.lnk hoặc .exe thông thường), kiểm tra tồn tại để loại bỏ shortcut rác/đã gỡ
                if (rec.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    rec.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(rec.TargetPath))
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(rec.ShortcutPath) && File.Exists(rec.ShortcutPath))
                {
                    DesktopAndStartMenuShortcuts[rec.Name] = rec.ShortcutPath;
                }

                AppsByName[rec.Name] = rec.TargetPath;
                AppsByPath[rec.TargetPath] = rec.Name;
            }

            _lastAppsScan = cache.CachedAtUtc;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] LoadDockAppsCacheFromDisk error: {ex.Message}");
        }
    }

    private static void SaveDockAppsCacheToDisk(
        Dictionary<string, string> appsByName,
        Dictionary<string, string> shortcuts,
        DateTime cachedAtUtc)
    {
        try
        {
            var filePath = GetDockAppsCacheFilePath();
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var cache = new SearchDockAppsCacheData
            {
                Version = 1,
                CachedAtUtc = cachedAtUtc,
                Records = new List<SearchDockAppRecord>(appsByName.Count)
            };

            foreach (var (name, target) in appsByName)
            {
                shortcuts.TryGetValue(name, out var scPath);
                cache.Records.Add(new SearchDockAppRecord
                {
                    Name = name,
                    TargetPath = target,
                    ShortcutPath = scPath
                });
            }

            var tempPath = filePath + ".tmp";
            var json = JsonSerializer.Serialize(cache, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] SaveDockAppsCacheToDisk error: {ex.Message}");
        }
    }

    private static void RescanAppsAndUpdateCache()
    {
        lock (AppsLock)
        {
            if (_appsScanInProgress)
            {
                return;
            }
            _appsScanInProgress = true;
        }

        try
        {
            var shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Quét Start Menu & Desktop (Lấy các phím tắt .lnk thực tế)
            var scan = StartMenuScanner.Scan();
            foreach (var s in scan.Shortcuts)
            {
                if (File.Exists(s.Path))
                {
                    shortcuts[s.Name] = s.Path;
                    appsByName[s.Name] = s.Path;
                    appsByPath[s.Path] = s.Name;
                }
            }

            // Quét thêm Desktop của người dùng
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (Directory.Exists(userDesktop))
            {
                foreach (var file in Directory.GetFiles(userDesktop, "*.lnk"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    shortcuts[name] = file;
                    appsByName[name] = file;
                    appsByPath[file] = name;
                }
            }

            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (Directory.Exists(commonDesktop))
            {
                foreach (var file in Directory.GetFiles(commonDesktop, "*.lnk"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    shortcuts[name] = file;
                    appsByName[name] = file;
                    appsByPath[file] = name;
                }
            }

            // 2. Quét shell:AppsFolder qua COM Shell.Application (Chỉ thêm các app UWP chưa có .lnk)
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is not null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    dynamic? folder = shell?.NameSpace("shell:AppsFolder");
                    if (folder is not null)
                    {
                        foreach (var item in folder.Items())
                        {
                            string name = item.Name;
                            string path = item.Path;

                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                                continue;

                            appsByPath[path] = name;

                            // Nếu app này chưa có file .lnk trên ổ đĩa, thì lưu đường dẫn shell:AppsFolder
                            if (!appsByName.ContainsKey(name))
                            {
                                appsByName[name] = $"shell:AppsFolder\\{path}";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsSearchService] Shell.Application scan error: {ex.Message}");
            }

            var nowUtc = DateTime.UtcNow;

            // Cập nhật bộ nhớ an toàn dưới lock
            lock (AppsLock)
            {
                DesktopAndStartMenuShortcuts.Clear();
                foreach (var kvp in shortcuts)
                    DesktopAndStartMenuShortcuts[kvp.Key] = kvp.Value;

                AppsByName.Clear();
                foreach (var kvp in appsByName)
                    AppsByName[kvp.Key] = kvp.Value;

                AppsByPath.Clear();
                foreach (var kvp in appsByPath)
                    AppsByPath[kvp.Key] = kvp.Value;

                _lastAppsScan = nowUtc;
            }

            // Ghi xuống file cache cục bộ (dock-apps-cache.json)
            SaveDockAppsCacheToDisk(appsByName, shortcuts, nowUtc);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] EnsureAppsCache error: {ex.Message}");
        }
        finally
        {
            lock (AppsLock)
            {
                _appsScanInProgress = false;
            }
        }
    }

    private static Dictionary<string, DateTime> CollectAppUsageTimes()
    {
        var appTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // A. UserAssist
        try
        {
            const string userAssistPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
            using var baseKey = Registry.CurrentUser.OpenSubKey(userAssistPath);
            if (baseKey is not null)
            {
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    using var countKey = baseKey.OpenSubKey($@"{subKeyName}\Count");
                    if (countKey is null) continue;

                    foreach (var valName in countKey.GetValueNames())
                    {
                        if (countKey.GetValue(valName) is byte[] bytes && bytes.Length >= 72)
                        {
                            var fileTime = BitConverter.ToInt64(bytes, 60);
                            if (fileTime > 0)
                            {
                                try
                                {
                                    var timeUtc = DateTime.FromFileTimeUtc(fileTime);
                                    var decoded = DecodeRot13(valName);
                                    if (!appTimes.TryGetValue(decoded, out var existing) || existing < timeUtc)
                                    {
                                        appTimes[decoded] = timeUtc;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] UserAssist read error: {ex.Message}");
        }

        // B. JumplistData
        try
        {
            const string jlPath = @"Software\Microsoft\Windows\CurrentVersion\Search\JumplistData";
            using var key = Registry.CurrentUser.OpenSubKey(jlPath);
            if (key is not null)
            {
                foreach (var valName in key.GetValueNames())
                {
                    if (key.GetValue(valName) is long fileTime && fileTime > 0)
                    {
                        try
                        {
                            var timeUtc = DateTime.FromFileTimeUtc(fileTime);
                            if (!appTimes.TryGetValue(valName, out var existing) || existing < timeUtc)
                            {
                                appTimes[valName] = timeUtc;
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsSearchService] JumplistData read error: {ex.Message}");
        }

        return appTimes;
    }

    private static (string? DisplayName, string? TargetPath) ResolveAppDetails(string rawKey)
    {
        // 1. Kiểm tra UWP đã biết
        if (WellKnownUwpNames.TryGetValue(rawKey, out var knownName))
        {
            return (knownName, $"shell:AppsFolder\\{rawKey}");
        }

        lock (AppsLock)
        {
            // 2. Kiểm tra trong shell:AppsFolder cache
            if (AppsByPath.TryGetValue(rawKey, out var appsFolderName))
            {
                if (DesktopAndStartMenuShortcuts.TryGetValue(appsFolderName, out var realLnk) && File.Exists(realLnk))
                {
                    return (appsFolderName, realLnk);
                }
                return (appsFolderName, $"shell:AppsFolder\\{rawKey}");
            }

            // 3. Giải mã KnownFolder GUID
            var resolvedPath = ResolveKnownFolderGuids(rawKey);
            if (File.Exists(resolvedPath))
            {
                var name = Path.GetFileNameWithoutExtension(resolvedPath);
                return (name, resolvedPath);
            }

            // 4. Nếu là phím tắt có tên trong Desktop hoặc StartMenu
            var possibleName = Path.GetFileNameWithoutExtension(rawKey);
            if (DesktopAndStartMenuShortcuts.TryGetValue(possibleName, out var smLnk) && File.Exists(smLnk))
            {
                return (possibleName, smLnk);
            }

            // 5. Kiểm tra trực tiếp theo tên ứng dụng trong StartMenu / Desktop
            if (DesktopAndStartMenuShortcuts.TryGetValue(rawKey, out var directLnk) && File.Exists(directLnk))
            {
                return (rawKey, directLnk);
            }
            if (AppsByName.TryGetValue(rawKey, out var appByNamePath))
            {
                return (rawKey, appByNamePath);
            }
        }

        return (null, null);
    }

    private static string ResolveKnownFolderGuids(string path)
    {
        var resolved = path;
        foreach (var (guid, folderPath) in KnownFolderMap)
        {
            if (resolved.StartsWith(guid, StringComparison.OrdinalIgnoreCase))
            {
                return folderPath + resolved[guid.Length..];
            }
        }
        return resolved;
    }

    private static bool IsIgnoredSystemEntry(string name)
    {
        return name.StartsWith("Microsoft.AutoGenerated", StringComparison.OrdinalIgnoreCase)
            || name.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Glassquid", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Install", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Helper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredRecentShortcut(string rawName)
    {
        return rawName.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)
            || rawName.Contains("--")
            || rawName.Contains("AumId=", StringComparison.OrdinalIgnoreCase)
            || rawName.Contains("PrintScreen", StringComparison.OrdinalIgnoreCase)
            || rawName.StartsWith('#')
            || rawName.StartsWith('$');
    }

    private static bool IsSystemApp(string name, string targetPath)
    {
        return name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Command Prompt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Terminal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Task Manager", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
            || targetPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeRot13(string input)
    {
        var chars = input.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(((c - 'A' + 13) % 26) + 'A');
            }
            else if (c is >= 'a' and <= 'z')
            {
                chars[i] = (char)(((c - 'a' + 13) % 26) + 'a');
            }
        }
        return new string(chars);
    }

    private static string GetCategorySubtitle(string path, bool isApp)
    {
        if (isApp) return "Ứng dụng";
        if (Directory.Exists(path)) return "Thư mục";

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".docx" or ".doc" => "Tài liệu Word",
            ".xlsx" or ".xls" or ".csv" => "Bảng tính Excel",
            ".pptx" or ".ppt" => "Bản trình bày PowerPoint",
            ".pdf" => "Tệp PDF",
            ".txt" or ".md" or ".json" or ".xml" or ".yaml" => "Văn bản",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" => "Hình ảnh",
            ".mp4" or ".mkv" or ".avi" or ".mov" => "Video",
            ".mp3" or ".wav" or ".flac" or ".aac" => "Âm thanh",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Tệp nén",
            _ => "Tệp tin"
        };
    }

    private static string FormatRelativeTime(DateTime timeUtc)
    {
        var diff = DateTime.UtcNow - timeUtc;
        if (diff.TotalMinutes < 2) return "Vừa xong";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} phút trước";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} giờ trước";
        if (diff.TotalDays < 2) return "Hôm qua";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} ngày trước";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} tuần trước";
        return timeUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static string ConvertFileUrlToLocalPath(string fileUrl)
    {
        if (!fileUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return fileUrl;

        var path = fileUrl["file:".Length..].TrimStart('/');
        return path.Replace('/', '\\');
    }

    internal static SearchIconLruCache GetIconCacheForTesting() => IconCache;

    internal static void ResetAppsCacheForTesting()
    {
        lock (AppsLock)
        {
            _appsCacheLoadedFromDisk = false;
            _lastAppsScan = DateTime.MinValue;
            DesktopAndStartMenuShortcuts.Clear();
            AppsByName.Clear();
            AppsByPath.Clear();
        }
    }

    internal static void PopulateAppsCacheForTesting(
        IEnumerable<KeyValuePair<string, string>> apps,
        IEnumerable<KeyValuePair<string, string>>? shortcuts = null)
    {
        lock (AppsLock)
        {
            DesktopAndStartMenuShortcuts.Clear();
            AppsByName.Clear();
            AppsByPath.Clear();

            if (shortcuts is not null)
            {
                foreach (var (k, v) in shortcuts)
                    DesktopAndStartMenuShortcuts[k] = v;
            }

            foreach (var (k, v) in apps)
            {
                AppsByName[k] = v;
                AppsByPath[v] = k;
            }

            _appsCacheLoadedFromDisk = true;
            _lastAppsScan = DateTime.UtcNow;
        }
    }
}

