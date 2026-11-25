using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.App.Startup;

/// <summary>Kiểm thử chấp nhận độc lập dữ liệu, tìm kiếm và hành vi bố cục của giai đoạn 5.</summary>
internal static class Phase5Acceptance
{
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, Glassquid.Platform.Storage.AcceptanceWorkspace acceptanceWorkspace)
    {
        var reportPath = arguments.Count > 0 ? Path.GetFullPath(arguments[0])
            : Path.Combine(Environment.CurrentDirectory, "phase5-acceptance.log");
        var root = acceptanceWorkspace.DataRoot;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new StringBuilder();
        var failed = 0;
        var passed = 0;
        log.AppendLine(CultureInfo.InvariantCulture, $"Kiểm thử chấp nhận dòng lệnh Glassquid giai đoạn 5 | {DateTimeOffset.Now:O}");
        log.AppendLine(CultureInfo.InvariantCulture, $"Sản phẩm build: {typeof(Phase5Acceptance).Assembly.FullName}");
        log.AppendLine(CultureInfo.InvariantCulture, $"Môi trường: {Environment.OSVersion}; {Environment.MachineName}");
        log.AppendLine(CultureInfo.InvariantCulture, $"Dữ liệu kiểm thử: {root}");
        log.AppendLine("Trình điều khiển: lưu tệp thực tế và dịch vụ thuần; không định tuyến WPF, không chèn đầu vào hệ thống");

        void Check(string name, string ids, bool okay, string expected, string actual)
        {
            if (okay) passed++; else failed++;
            log.AppendLine(CultureInfo.InvariantCulture, $"[{(okay ? "Passed" : "Failed")}] {name} | {ids} | Kỳ vọng: {expected} | Thực tế: {actual} | {DateTimeOffset.Now:O}");
        }

        try
        {
            var data = new GlassquidDataStore(paths);
            data.LoadAll();
            for (var i = 0; i < 500; i++)
                data.Library.Add(new AppEntry
                {
                    Id = $"app-{i:000}", DisplayName = i switch
                    {
                        0 => "Launchpad", 1 => "Visual Studio Code", 499 => "Công cụ đích 499", _ => $"Ứng dụng App {i:000}",
                    },
                    Target = Path.Combine(root, $"Đường dẫn mẫu {i:000}"), IsTargetMissing = i % 10 == 0,
                    SearchAliases = i == 499 ? ["last-item"] : [],
                });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var layoutWorkspace = new LauncherWorkspace(host);
            layoutWorkspace.Reconcile(35);
            Check("Phân trang mặc định 500 mục", "L-07/L-08", layoutWorkspace.Layout.Pages.Count == 15 && layoutWorkspace.Layout.Pages[^1].Slots.Count == 10,
                "15 trang, trang cuối 10 mục", $"{layoutWorkspace.Layout.Pages.Count} trang, trang cuối {layoutWorkspace.Layout.Pages[^1].Slots.Count} mục");
            var stamp = File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath);
            var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var coldWatch = Stopwatch.StartNew();
            host.Search.SearchApps("last-item", 500);
            coldWatch.Stop();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) host.Search.SearchApps("last-item", 500);
            watch.Stop();
            var searchId = host.Search.SearchApps("last-item", 500).SingleOrDefault()?.Item.Id;
            Check("Mục cuối cùng có thể tìm kiếm", "L-01-L-06", searchId == "app-499", "app-499", searchId ?? "Không có kết quả");
            Check("Pinyin đầy đủ và chữ cái đầu", "L-03/L-04",
                host.Search.SearchApps("qidongtai", 500).Any(hit => hit.Item.Id == "app-000")
                && host.Search.SearchApps("qdt", 500).Any(hit => hit.Item.Id == "app-000"),
                "Khớp cả hai dạng Pinyin của Launchpad", "Đã thực thi truy vấn");
            Check("Tìm kiếm không ghi lại bố cục", "R-08", stamp == File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath)
                && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "Byte và thời gian sửa đổi không đổi", "Đã đối chiếu");
            log.AppendLine(CultureInfo.InvariantCulture, $"Mẫu hiệu năng: 500 mục; máy {Environment.MachineName}; lần đầu (cold) {coldWatch.Elapsed.TotalMilliseconds:F2} ms; tổng thời gian 100 lần tìm kiếm toàn thư viện (warm) {watch.Elapsed.TotalMilliseconds:F2} ms, trung bình {watch.Elapsed.TotalMilliseconds / 100:F3} ms/lần. Bao gồm khớp và sắp xếp, không tính khởi động tiến trình và đọc đĩa.");
            var edit = layoutWorkspace.BeginEdit();
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "app-000", "app-001", "group-a", 35);
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "app-002", "app-003", "group-b", 35);
            edit.Draft = LauncherLayoutEditor.Move(edit.Draft, "app-499", 0, 2, 35);
            var committed = layoutWorkspace.Commit(edit);
            var committedGroups = layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots)
                .Where(slot => slot.Group is not null).Select(slot => slot.Group!).ToList();
            var firstGroup = committedGroups.SingleOrDefault(group => group.Id == "group-a");
            var secondGroup = committedGroups.SingleOrDefault(group => group.Id == "group-b");
            var committedMove = layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots)
                .Select(slot => slot.ItemId ?? slot.Group?.Id).ToList().IndexOf("app-499") == 2;
            var groupsCorrect = committedGroups.Count == 2
                && firstGroup?.ItemIds.SequenceEqual(["app-001", "app-000"]) == true
                && secondGroup?.ItemIds.SequenceEqual(["app-003", "app-002"]) == true;
            Check("Lưu nhiều nhóm và sắp xếp xuyên trang", "L-09/L-11-L-16", committed && groupsCorrect && committedMove,
                "Thành viên hai nhóm một cấp và thứ tự cấp cao nhất đúng", $"Đã lưu={committed}; số nhóm={committedGroups.Count}; chỉ số mới của mục cuối={layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots).Select(slot => slot.ItemId ?? slot.Group?.Id).ToList().IndexOf("app-499")}");
            var ids = LauncherLayoutRules.AppIds(layoutWorkspace.Layout);
            Check("Sắp xếp không trùng lặp, không thất lạc", "L-09/L-11/L-16", ids.Count == 500 && ids.Distinct().Count() == 500,
                "500 Id duy nhất", $"{ids.Count} mục, duy nhất {ids.Distinct().Count()} mục");
            var reloaded = new GlassquidDataStore(paths);
            reloaded.LoadAll();
            Check("Phiên bản lưu trữ mới tải lại bố cục", "L-09/L-11/L-13", LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).SequenceEqual(ids),
                "Thứ tự khớp", "Phiên bản mới đã đọc lại từ đĩa để đối chiếu");
            var beforeCancel = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var cancelled = layoutWorkspace.BeginEdit();
            cancelled.Draft = LauncherLayoutEditor.Dissolve(cancelled.Draft, "group-a", 35);
            Check("Hủy bản xem trước không ghi xuống đĩa", "R-08", beforeCancel.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "Byte không đổi", "Chưa gọi lưu");
            layoutWorkspace.Reconcile(20);
            Check("Đổi lưới không làm mất thành viên", "L-07", LauncherLayoutRules.AppIds(layoutWorkspace.Layout).Count == 500,
                "500 thành viên", $"{LauncherLayoutRules.AppIds(layoutWorkspace.Layout).Count} thành viên");
        }
        catch (Exception ex)
        {
            failed++;
            log.AppendLine("[Failed] runner | L-01-L-25 | Kỳ vọng: không có ngoại lệ | Thực tế: " + ex);
        }
        log.AppendLine(CultureInfo.InvariantCulture, $"Tổng cộng: Passed={passed} Failed={failed}");
        AcceptanceEntryPoint.WriteText(acceptanceWorkspace.GetOutput("report"), log.ToString());
        ReportWriter.Write(log.ToString(), null);
        return failed == 0 ? 0 : 1;
    }
}
