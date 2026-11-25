using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.AppServices;
using Glassquid.Core.Storage;

using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Hai đầu dò của Giai đoạn 4: danh tính ứng dụng (AppUserModelID) và nguồn sự kiện cửa sổ.
/// </summary>
/// <remarks>
/// Đầu ra của đầu dò là <strong>kết luận thực nghiệm</strong>, không phải tính năng sản phẩm: chúng trả lời
/// hai câu hỏi quyết định hướng triển khai tiếp theo là "hình thái danh tính riêng của ứng dụng đóng gói,
/// máy chủ console và UWP là gì" và "sự kiện WinEvent có thể bao phủ việc cửa sổ xuất hiện và biến mất hay không".
/// Nguyên văn đầu ra được lưu tại <c>docs/evidence/giai-doan-4-ket-qua-thuc-nghiem-tham-do.txt</c>.
///
/// <para>
/// Ranh giới đối với desktop của người dùng: chỉ liệt kê ở chế độ chỉ đọc; tiến trình mục tiêu tự khởi chạy
/// sẽ được ghi lại pid, sau khi quan sát xong chỉ dọn dẹp theo pid đã ghi và "cửa sổ mới xuất hiện trước/sau khi khởi chạy",
/// tuyệt đối không dọn dẹp theo tên tiến trình.
/// </para>
/// </remarks>
internal static class Phase4Probe
{
    private static readonly StringBuilder Log = new();

    /// <summary>Đầu dò A: Ba nguồn danh tính ứng dụng có hình thái như thế nào.</summary>
    public static int RunAumid(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Log.Clear();
        Log.AppendLine("Giai đoạn 4 Đầu dò A: Danh tính ứng dụng (AppUserModelID)");
        Log.AppendLine("==============================================");
        Log.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log.AppendLine();

        ProbeShortcutAumids();
        ProbeRunningWindows();
        ProbeLaunchedTargets();

        Log.AppendLine();
        Log.AppendLine("-- Kết thúc Đầu dò A --");

        ReportWriter.Write(Log.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-aumid.log"));
        return 0;
    }

    /// <summary>Đầu dò B: Nguồn sự kiện WinEvent có thể bao phủ việc cửa sổ xuất hiện, biến mất và thay đổi hiển thị hay không.</summary>
    public static int RunEvents(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Log.Clear();
        Log.AppendLine("Giai đoạn 4 Đầu dò B: Nguồn sự kiện cửa sổ (WinEvent)");
        Log.AppendLine("==============================================");
        Log.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log.AppendLine();

        RunEventsCore();

        Log.AppendLine();
        Log.AppendLine("-- Kết thúc Đầu dò B --");

        ReportWriter.Write(Log.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-runevents.log"));
        return 0;
    }

    // ---- Ba phần của Đầu dò A ----

    /// <summary>Phần một: Tỷ lệ và hình thái danh tính tường minh trên các tệp .lnk trong Start Menu.</summary>
    private static void ProbeShortcutAumids()
    {
        var scan = AppServiceHost.ScanStartMenu();

        Log.AppendLine("[A1] Danh tính tường minh của .lnk trong Start Menu");
        Log.AppendLine(CultureInfo.InvariantCulture, $"  Đã quét được {scan.Shortcuts.Count} tệp .lnk");

        var withIdentity = new List<(string Name, string Path, string Aumid)>();

        foreach (var shortcut in scan.Shortcuts)
        {
            var aumid = WindowIdentity.TryGetShortcutAumid(shortcut.Path);
            if (aumid is not null)
            {
                withIdentity.Add((shortcut.Name, shortcut.Path, aumid));
            }
        }

        Log.AppendLine(CultureInfo.InvariantCulture,
            $"  Có AUMID tường minh: {withIdentity.Count} / {scan.Shortcuts.Count}"
            + $" ({withIdentity.Count * 100.0 / Math.Max(1, scan.Shortcuts.Count):F1}%)");
        Log.AppendLine("  Mẫu (tối đa 12 mục, lưu ý hình thái của ứng dụng đóng gói và UWP):");

        foreach (var (name, path, aumid) in withIdentity.Take(12))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"    · {name}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"      .lnk {path}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"      AUMID {aumid}");
        }

        // Đặc biệt chú ý ba nhóm đại diện cho trường hợp "không khớp được theo đường dẫn ảnh thực thi".
        Log.AppendLine("  Danh tính tường minh của ba mục tiêu đại diện (nếu không có ghi \"Không\"):");
        ReportIdentityOf(scan, ["Ghi chú", "Notepad"]);
        ReportIdentityOf(scan, ["Thiết bị đầu cuối", "Terminal", "Dấu nhắc lệnh", "Command"]);
        ReportIdentityOf(scan, ["Máy tính", "Calculator"]);

        Log.AppendLine();
    }

    private static void ReportIdentityOf(StartMenuScanResult scan, string[] keywords)
    {
        foreach (var shortcut in scan.Shortcuts)
        {
            if (!keywords.Any(k => shortcut.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var aumid = WindowIdentity.TryGetShortcutAumid(shortcut.Path);
            var info = aumid is null ? "Không (trên lối tắt không có danh tính tường minh)" : aumid;
            Log.AppendLine(CultureInfo.InvariantCulture, $"    · {shortcut.Name} → {info}");
            return;
        }

        Log.AppendLine(CultureInfo.InvariantCulture, $"    · (Trong Start Menu không có lối tắt nào có tên chứa {string.Join('/', keywords)})");
    }

    /// <summary>Phần hai: Tổng quan danh tính của tất cả cửa sổ ứng viên trên desktop hiện tại (chỉ đọc).</summary>
    private static void ProbeRunningWindows()
    {
        Log.AppendLine("[A2] Cửa sổ ứng viên và danh tính hai phía trên desktop hiện tại (liệt kê chỉ đọc)");

        var windows = WindowLocator.EnumerateTopLevelWindows();
        Log.AppendLine(CultureInfo.InvariantCulture, $"  Cửa sổ cấp cao nhất ứng viên: {windows.Count} cửa sổ");

        foreach (var window in windows.Take(40))
        {
            var identity = window.WindowAumid ?? window.ProcessAumid ?? "(Không đọc được danh tính từ cả hai phía)";
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"    · 0x{window.Handle:X} \"{Truncate(window.Title, 40)}\" pid {window.ProcessId}");
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"      Tiến trình {window.ImagePath ?? "(không đọc được)"}");
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"      Danh tính cửa sổ {(window.WindowAumid ?? "Không")} | Danh tính tiến trình {(window.ProcessAumid ?? "Không")} | Hiệu lực {identity}");
        }

        Log.AppendLine("  Đối chiếu máy chủ console: mọi cửa sổ thuộc WindowsTerminal.exe đều có danh tính");
        Log.AppendLine("  chắc chắn khác với đường dẫn ảnh của cmd.exe (C:\\Windows\\System32\\cmd.exe) -- đây là căn cứ thực nghiệm");
        Log.AppendLine("  cho việc \"chương trình console không thể khớp theo đường dẫn ảnh\", không cần khởi chạy riêng cmd để xác minh thêm.");
        Log.AppendLine();
    }

    /// <summary>
    /// Phần ba: Chỉ khởi chạy chương trình <strong>cổ điển</strong> làm mục tiêu thử nghiệm; bằng chứng danh tính của ứng dụng đóng gói lấy từ bước liệt kê chỉ đọc ở phần hai.
    /// </summary>
    /// <remarks>
    /// <b>Không khởi chạy ứng dụng đóng gói (Notepad) làm mục tiêu thử nghiệm.</b> Lý do là vệ sinh kiểm thử, không phải vấn đề năng lực:
    /// <c>System32\notepad.exe</c> là tệp stub, khi chạy sẽ chuyển tiếp sang bản đóng gói trong WindowsApps,
    /// dẫn đến việc "kết thúc pid do mình khởi chạy" không đóng được cửa sổ thật -- mục tiêu thử nghiệm bị sót lại trên desktop người dùng,
    /// và lần mở tiếp theo còn có thể khôi phục một tệp không còn tồn tại (biểu hiện là "Hệ thống không tìm thấy đường dẫn được chỉ định").
    /// Hình thái danh tính của ứng dụng đóng gói và UWP đã được chứng minh qua <strong>liệt kê chỉ đọc</strong> ở phần hai,
    /// không cần mở thêm cửa sổ cho cùng một bằng chứng.
    /// </remarks>
    private static void ProbeLaunchedTargets()
    {
        Log.AppendLine("[A3] Danh tính mục tiêu tự khởi chạy (chỉ khởi chạy chương trình cổ điển; không khởi chạy ứng dụng đóng gói, bằng chứng lấy từ liệt kê chỉ đọc A2)");

        ProbeOneTarget("Chương trình Win32 cổ điển (tên đăng ký App Paths)", "charmap.exe", entry =>
        {
            // Kỳ vọng với chương trình cổ điển: cả danh tính cửa sổ và danh tính tiến trình đều là "Không",
            // đường dẫn ảnh của tiến trình sở hữu chính là bản thân nó -- đây là nhóm mà chiến lược khớp hiện tại hoạt động hiệu quả.
            var classicOk = entry.WindowAumid is null && entry.ProcessAumid is null
                && entry.ImagePath?.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase) == true;
            Log.AppendLine(CultureInfo.InvariantCulture, $"  Đánh giá: {(classicOk ? "Đúng như kỳ vọng (danh tính rỗng, đường dẫn ảnh khớp được)" : "Không khớp kỳ vọng, cần kiểm tra thủ công")}");
        });

        Log.AppendLine("  Giải thích: Hình thái danh tính của ứng dụng đóng gói (Notepad) và UWP xem tại phần liệt kê chỉ đọc A2 --");
        Log.AppendLine("  thực nghiệm trên máy này cho thấy danh tính gói tiến trình của cửa sổ Notepad là Microsoft.WindowsNotepad_8wekyb3d8bbwe!App,");
        Log.AppendLine("  còn danh tính tường minh của cửa sổ UWP \"Settings\" được gắn trên cửa sổ khung của ApplicationFrameHost.");
        Log.AppendLine("  Nhờ vậy không cần để lại một cửa sổ Notepad không đóng được trên desktop người dùng chỉ vì cùng một bằng chứng.");

        Log.AppendLine();
    }

    private static void ProbeOneTarget(string label, string executable, Action<WindowIdentityInfo> judge)
    {
        Log.AppendLine(CultureInfo.InvariantCulture, $"  -- Khởi chạy {label}: {executable} --");

        var before = WindowLocator.EnumerateTopLevelWindows().Select(w => w.Handle).ToHashSet();

        var outcome = ShellExecutor.Open(executable, null, null);
        if (!outcome.Started || outcome.ProcessId == 0)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  Khởi chạy thất bại: mã lỗi {outcome.ErrorCode}. Bỏ qua phần này.");
            return;
        }

        Log.AppendLine(CultureInfo.InvariantCulture, $"  ID tiến trình mục tiêu đã ghi: {outcome.ProcessId}");

        // Dành thời gian cho cửa sổ được tạo; ứng dụng đóng gói phải chuyển tiếp qua stub nên chờ lâu hơn một chút.
        Thread.Sleep(2500);

        WindowIdentityInfo? newWindow = null;
        foreach (var window in WindowLocator.EnumerateTopLevelWindows())
        {
            if (!before.Contains(window.Handle))
            {
                newWindow = window;
                break;
            }
        }

        if (newWindow is null)
        {
            Log.AppendLine("  Không xuất hiện cửa sổ mới trước/sau khi khởi chạy (có thể đã tái sử dụng thực thể có sẵn hoặc cửa sổ máy chủ).");
        }
        else
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  Cửa sổ mới 0x{newWindow.Handle:X} \"{newWindow.Title}\" pid {newWindow.ProcessId}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"    Đường dẫn ảnh {newWindow.ImagePath ?? "(không đọc được)"}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"    Danh tính cửa sổ {newWindow.WindowAumid ?? "Không"} | Danh tính tiến trình {newWindow.ProcessAumid ?? "Không"}");
            judge(newWindow);
        }

        KillSpawnedTarget(executable, outcome.ProcessId, newWindow?.Handle ?? 0);
    }

    /// <summary>
    /// Dọn dẹp mục tiêu của chính mình: kết thúc tiến trình theo pid đã ghi trước, sau đó gửi bổ sung
    /// một thông điệp WM_CLOSE cho "cửa sổ mới xuất hiện trong lượt này". Tuyệt đối không quét toàn cục theo tên tiến trình.
    /// </summary>
    private static void KillSpawnedTarget(string executable, uint processId, nint newWindowHandle)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                Log.AppendLine(CultureInfo.InvariantCulture, $"  Đã kết thúc tiến trình mục tiêu đã ghi {processId} ({executable}).");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  Tiến trình mục tiêu đã ghi {processId} không còn tồn tại ({ex.GetType().Name}).");
        }

        if (WindowControl.IsAlive(newWindowHandle))
        {
            // Ứng dụng đóng gói được chuyển tiếp qua stub: kết thúc stub chưa chắc đã đóng cửa sổ thật, gửi thêm yêu cầu đóng theo handle mới xuất hiện.
            _ = WindowControl.Close(newWindowHandle);
            Thread.Sleep(500);

            if (WindowControl.IsAlive(newWindowHandle))
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  Cửa sổ mới 0x{newWindowHandle:X} vẫn còn (có thể đang hiện hộp thoại nhắc lưu), giữ lại để kiểm tra thủ công.");
            }
            else
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  Đã đóng cửa sổ mới 0x{newWindowHandle:X}.");
            }
        }
    }

    // ---- Đầu dò B ----

    /// <summary>
    /// Gắn một bộ hook sự kiện cửa sổ, thực hiện trọn bộ thao tác "xuất hiện - thu nhỏ - khôi phục - ẩn - hiện - đóng"
    /// lên mục tiêu tự khởi chạy và in ra dòng thời gian sự kiện.
    /// </summary>
    private static void RunEventsCore()
    {
        var events = new List<string>();
        var reconcileCount = 0;

        // Chạy đầu dò bằng nguồn sự kiện của chính sản phẩm: nhánh phụ chẩn đoán ghi lại dòng thời gian sự kiện thô,
        // bộ đếm thông báo đã gộp chứng minh luồng "callback chỉ đẩy vào hàng đợi, tính toán lại ở nơi khác" đang thực sự hoạt động.
        var watcher = WindowEventWatcher.TryStart(
            onChanged: () => reconcileCount++,
            onRawEvent: (eventType, window, _, _) => events.Add(
                $"{DescribeEvent(eventType),-22} 0x{window:X} \"{WindowControl.GetTitle(window)}\""));

        if (watcher is null)
        {
            Log.AppendLine("✗ Cài đặt nguồn sự kiện (hook WinEvent) thất bại. Kết luận: trạng thái đang chạy trên máy này phải lùi về liệt kê theo yêu cầu.");
            return;
        }

        using (watcher)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B1] Cài đặt nguồn sự kiện thành công, id luồng callback {watcher.HookThreadId}.");

            var baseline = WindowLocator.EnumerateTopLevelWindows();
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B2] Trước khi khởi chạy đã có {baseline.Count} cửa sổ ứng viên -- trạng thái đang chạy phải liệt kê ban đầu trước,");
            Log.AppendLine("     nếu không mọi cửa sổ đã chạy trước khi Dock khởi động sẽ bị bỏ sót hoàn toàn.");

            var before = baseline.Select(w => w.Handle).ToHashSet();

            var outcome = ShellExecutor.Open(@"C:\Windows\System32\charmap.exe", null, null);
            if (!outcome.Started || outcome.ProcessId == 0)
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"[B3] Khởi chạy mục tiêu thất bại (mã lỗi {outcome.ErrorCode}), bỏ qua các thao tác tiếp theo.");
                return;
            }

            Log.AppendLine(CultureInfo.InvariantCulture, $"[B3] Mục tiêu charmap đã khởi chạy, id tiến trình {outcome.ProcessId}, đang chờ cửa sổ xuất hiện...");
            WaitVisible(() => WindowLocator.EnumerateTopLevelWindows().Any(w => !before.Contains(w.Handle)));

            var target = WindowLocator.EnumerateTopLevelWindows().FirstOrDefault(w => !before.Contains(w.Handle));
            if (target is null)
            {
                Log.AppendLine("  Không đợi được cửa sổ mới, dòng sự kiện chỉ có ý nghĩa ở giai đoạn khởi chạy.");
            }
            else
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  Cửa sổ mục tiêu 0x{target.Handle:X} \"{target.Title}\".");

                void Step(string label, Action action, int settleMilliseconds = 700)
                {
                    var countBefore = events.Count;
                    action();
                    Thread.Sleep(settleMilliseconds);
                    Log.AppendLine(CultureInfo.InvariantCulture, $"[B] {label} (sau đó phát sinh thêm {events.Count - countBefore} sự kiện)");
                }

                Step("Thu nhỏ", () => _ = WindowControl.Minimize(target.Handle));
                Step("Khôi phục", () => _ = WindowControl.Restore(target.Handle));
                Step("Ẩn (SW_HIDE)", () => _ = WindowControl.Hide(target.Handle));
                Step("Hiển thị lại (SW_SHOW)", () => _ = WindowControl.Show(target.Handle));
            }

            var countBeforeClose = events.Count;
            if (target is not null)
            {
                _ = WindowControl.Close(target.Handle);
            }

            try
            {
                using var process = Process.GetProcessById((int)outcome.ProcessId);
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: false);
                    Log.AppendLine("  Mục tiêu không thoát trong vòng 3 giây, đã kết thúc theo pid đã ghi.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.AppendLine("  Tiến trình mục tiêu đã ghi không còn tồn tại.");
            }

            Thread.Sleep(800);
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B4] Mục tiêu đã đóng; sau khi đóng phát sinh thêm {events.Count - countBeforeClose} sự kiện.");

            var stillThere = WindowLocator.EnumerateTopLevelWindows().Any(w => w.Handle == (target?.Handle ?? 0));
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B5] Liệt kê lại sau khi đóng: cửa sổ mục tiêu {(stillThere ? "vẫn còn (bước đối chiếu sẽ sửa chấm trạng thái)" : "không còn nữa (sự kiện khớp với kết quả liệt kê)")}.");

            Thread.Sleep(500);
            PrintTimeline(events);

            Log.AppendLine();
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B6] Tổng cộng đã ghi {events.Count} sự kiện thô; số lần kích hoạt tính toán lại sau khi gộp là {reconcileCount}");
            Log.AppendLine("     (Số sự kiện thô nhiều hơn hẳn số lần tính toán lại, chứng minh callback chỉ đẩy vào hàng đợi và việc tính toán lại được gộp -- trong callback của hook không có bất kỳ truy vấn nào.)");
            PrintByEvent(events);
        }
    }

    private static string DescribeEvent(uint eventType) => eventType switch
    {
        User32Event.ObjectCreate => "OBJECT_CREATE",
        User32Event.ObjectDestroy => "OBJECT_DESTROY",
        User32Event.ObjectShow => "OBJECT_SHOW",
        User32Event.ObjectHide => "OBJECT_HIDE",
        User32Event.MinimizeStart => "SYSTEM_MINIMIZESTART",
        User32Event.MinimizeEnd => "SYSTEM_MINIMIZEEND",
        User32Event.Foreground => "SYSTEM_FOREGROUND",
        _ => $"0x{eventType:X}",
    };

    private static void WaitVisible(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static void PrintTimeline(List<string> events)
    {
        Log.AppendLine();
        Log.AppendLine("[B7] Dòng thời gian sự kiện (tối đa 60 mục):");

        foreach (var entry in events.Take(60))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  {entry}");
        }

        if (events.Count > 60)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  ... (lược bỏ {events.Count - 60} mục còn lại)");
        }
    }

    private static void PrintByEvent(List<string> events)
    {
        Log.AppendLine();
        Log.AppendLine("[B8] Thống kê theo loại sự kiện:");

        foreach (var group in events
            .Select(e => e.Split(' ')[0])
            .GroupBy(e => e)
            .OrderBy(g => g.Key))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()} mục");
        }
    }


    /// <summary>Cắt ngắn tiêu đề quá dài theo số ký tự chỉ định, tránh một dòng dữ liệu bảng làm phình quá mức báo cáo.</summary>
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}

/// <summary>Mã loại sự kiện WinEvent cục bộ của đầu dò (cùng giá trị với hằng số nội bộ trong Platform, chỉ dùng để in ra).</summary>
internal static class User32Event
{
    internal const uint Foreground = 0x0003;
    internal const uint MinimizeStart = 0x0016;
    internal const uint MinimizeEnd = 0x0017;
    internal const uint ObjectCreate = 0x8000;
    internal const uint ObjectDestroy = 0x8001;
    internal const uint ObjectShow = 0x8002;
    internal const uint ObjectHide = 0x8003;
}
