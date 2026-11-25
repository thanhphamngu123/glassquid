using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Imaging;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Dạng thực thi được của tiêu chuẩn nghiệm thu Giai đoạn 3.
/// </summary>
/// <remarks>
/// Mọi yêu cầu được liệt kê trong Mục 5 của hướng dẫn khởi chạy đều trở thành các kiểm tra chạy thực tế tại đây:
/// nhập một mục cho mỗi loại trong 4 loại + trang web, đường dẫn có tiếng Việt và khoảng trắng, giữ nguyên tham số, giữ lại mục có đường dẫn hỏng và cho phép tìm lại vị trí,
/// báo cáo từng mục khi nhập mà không làm hỏng dữ liệu hiện có, và vẫn hoạt động bình thường ở quy mô 500 mục.
///
/// <para>
/// Cùng lý do như <see cref="SelfTest"/>: giao kết luận "đã xác minh" cho một lệnh có thể chạy lại được,
/// thay vì dựa vào trí nhớ sau một lần thao tác thủ công. Lệnh này <strong>chỉ chạm vào</strong>
/// <c>apps.json</c> và thư mục bộ nhớ đệm biểu tượng — giữ nguyên cấu hình và bố cục,
/// tránh biến quy trình nghiệm thu thành quy trình phá hỏng cấu hình của người dùng.
/// </para>
/// </remarks>
internal static class Phase3Acceptance
{
    /// <summary>
    /// Chạy toàn bộ các kiểm tra nghiệm thu.
    /// </summary>
    /// <param name="realPaths">Thư mục dữ liệu thực của người dùng. **Không bị thay đổi**, chỉ dùng để đối chiếu trong báo cáo.</param>
    /// <param name="arguments">Tùy chọn: tham số đầu tiên là đường dẫn xuất bảng liên hệ biểu tượng.</param>
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, Glassquid.Platform.Storage.AcceptanceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(arguments);

        var report = new StringBuilder();
        var checks = new List<(string Label, CheckStatus Status, string Detail)>();

        var paths = new AppPaths(workspace.DataRoot);
        paths.EnsureCreated();

        report.AppendLine("Nghiệm thu Glassquid Giai đoạn 3");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu nghiệm thu: {paths.RootDirectory} (thư mục tạm tách biệt, giữ lại sau khi chạy để đối chiếu)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu thực của người dùng: {realPaths.RootDirectory} — hoàn toàn không bị thay đổi");
        report.AppendLine();

        var set = SampleDataBuilder.Build();
        report.AppendLine(SampleDataBuilder.Describe(set));

        var data = new GlassquidDataStore(paths);
        data.LoadAll();

        using var host = new AppServiceHost(data);

        Section(report, "I. Nhập các nguồn hợp lệ cần được chấp nhận");
        var result = ImportAll(host, set, report);
        Check(checks, "Tất cả các nguồn được chấp nhận đều thêm mới thành công", result.Added.Count == set.ShouldImport.Count,
            $"Kỳ vọng {set.ShouldImport.Count} mục, thực tế {result.Added.Count} mục");
        Check(checks, "Không có nguồn được chấp nhận nào bị lỗi", result.Failures.Count == 0,
            $"Lỗi {result.Failures.Count} mục");

        Section(report, "II. Các nguồn bị từ chối phải báo lỗi từng mục");
        var rejected = ImportRejected(host, set, report);
        Check(checks, "Không có nguồn bị từ chối nào lọt vào thư viện ứng dụng", rejected.Added.Count == 0,
            $"Đã thêm {rejected.Added.Count} mục");
        Check(checks, "Mỗi nguồn lỗi đều được báo cáo riêng từng mục",
            rejected.Failures.Count == set.ShouldFail.Count,
            $"Kỳ vọng {set.ShouldFail.Count} mục lỗi, thực tế {rejected.Failures.Count} mục ({DescribeFailures(rejected)})");
        Check(checks, "Đường dẫn không tồn tại được báo là NotFound",
            rejected.Failures.Any(failure => failure.Reason == ImportFailureReason.NotFound),
            DescribeFailures(rejected));
        Check(checks, "Lối tắt không đọc được đích được báo là ShortcutUnreadable",
            rejected.Failures.Any(failure => failure.Reason == ImportFailureReason.ShortcutUnreadable),
            DescribeFailures(rejected));

        Section(report, "III. Bốn loại mục và trang web đều có trong thư viện");
        var byKey = MapByKey(host, set);
        Check(checks, "Mục chương trình tồn tại",
            byKey.ContainsKey("program"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Mục thư mục tồn tại",
            byKey.ContainsKey("folder"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Mục tệp tin tồn tại",
            byKey.ContainsKey("file"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Mục lối tắt (có tham số) tồn tại",
            byKey.ContainsKey("shortcut-arguments"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Mục trang web tồn tại (tệp .url)",
            byKey.ContainsKey("url-file"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Mục trang web tồn tại (lối tắt trỏ tới URL)",
            byKey.ContainsKey("shortcut-url"), $"Khớp {byKey.Count} mẫu: {DescribeKeys(byKey)}");
        Check(checks, "Hai URL chỉ khác cổng được xem là hai bản ghi riêng",
            byKey.TryGetValue("url-port-8001", out var portA)
            && byKey.TryGetValue("url-port-8002", out var portB)
            && !string.Equals(portA.Target, portB.Target, StringComparison.Ordinal),
            $"8001 → {byKey.GetValueOrDefault("url-port-8001")?.Target ?? "(thiếu)"}; "
            + $"8002 → {byKey.GetValueOrDefault("url-port-8002")?.Target ?? "(thiếu)"}");
        Check(checks, "Tệp có đuôi .url nhưng nội dung không phải trang web được xử lý như tệp thường",
            byKey.TryGetValue("bogus-url-file", out var bogus) && bogus.Kind == AppItemKind.File,
            byKey.TryGetValue("bogus-url-file", out var shownBogus) ? $"Thực tế [{shownBogus.Kind}] {shownBogus.Target}" : "Thiếu mục");

        Check(checks, "Lối tắt có tham số giữ nguyên tham số",
            byKey.TryGetValue("shortcut-arguments", out var withArgs)
            && withArgs.Arguments == "--flag \"giá trị có khoảng trắng\" --second",
            byKey.TryGetValue("shortcut-arguments", out var shown) ? $"Thực tế \"{shown.Arguments}\"" : "Thiếu mục");

        Check(checks, "Lối tắt có tham số giữ nguyên thư mục làm việc",
            byKey.TryGetValue("shortcut-arguments", out var withDir) && !string.IsNullOrEmpty(withDir.WorkingDirectory),
            byKey.TryGetValue("shortcut-arguments", out var shownDir) ? $"Thực tế \"{shownDir.WorkingDirectory}\"" : "Thiếu mục");

        Check(checks, "Chỉ số biểu tượng của lối tắt không bị gộp phẳng thành chính tệp đích",
            byKey.TryGetValue("shortcut-arguments", out var withIcon)
            && withIcon.IconSource is not null
            && withIcon.IconSource.EndsWith(",3", StringComparison.Ordinal),
            byKey.TryGetValue("shortcut-arguments", out var shownIcon) ? $"Thực tế \"{shownIcon.IconSource}\"" : "Thiếu mục");

        Check(checks, "Lối tắt trỏ tới URL được nhận diện là trang web thay vì giữ lại dạng .lnk",
            byKey.TryGetValue("shortcut-url", out var web)
            && web.Kind == AppItemKind.Url
            && web.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            byKey.TryGetValue("shortcut-url", out var shownWeb) ? $"Thực tế [{shownWeb.Kind}] {shownWeb.Target}" : "Thiếu mục");

        Check(checks, "Đường dẫn có tiếng Việt và khoảng trắng được giữ nguyên vẹn",
            byKey.TryGetValue("file", out var chinese) && chinese.Target.Contains("Báo cáo quý 2026", StringComparison.Ordinal),
            byKey.TryGetValue("file", out var shownChinese) ? $"Thực tế \"{shownChinese.Target}\"" : "Thiếu mục");

        Section(report, "IV. Bộ nhớ đệm biểu tượng");
        report.AppendLine(CultureInfo.InvariantCulture, $"Ghi mới {host.Import.IconsWritten}, tái sử dụng {host.Import.IconsReused}, tổng cộng {host.Icons.FileCount} tệp trong thư mục đệm");
        report.AppendLine();
        var iconVerdict = VerifyIcons(paths, report);
        Check(checks, "Mỗi biểu tượng có nguồn khác nhau nên không có tệp trùng lặp", host.Import.IconsReused == 0,
            $"Tái sử dụng {host.Import.IconsReused}");
        Check(checks, "Tất cả biểu tượng trong bộ nhớ đệm đều giải mã được và có nội dung hiển thị", iconVerdict.Failures == 0,
            $"Lỗi {iconVerdict.Failures}, tổng số {iconVerdict.Total}");

        Section(report, "V. Mục vẫn còn sau khi khởi động lại (tải lại tệp từ tiến trình khác)");
        var reloaded = new AppLibraryStore(paths.AppLibraryFile);
        reloaded.Load();
        var reloadedUsable = reloaded.Items.Count(entry => entry is not null && entry.IsUsable);
        Check(checks, "Số lượng mục không đổi sau khi tải lại", reloadedUsable == host.Library.Items.Count(entry => entry is not null && entry.IsUsable),
            $"Tải lại {reloadedUsable} mục, ban đầu {host.Library.Items.Count} mục");
        Check(checks, "Tham số vẫn còn sau khi tải lại",
            reloaded.Items.Any(entry => entry?.Arguments == "--flag \"giá trị có khoảng trắng\" --second"),
            $"Tải lại {reloadedUsable} mục");

        Section(report, "VI. Đích bị hỏng: giữ lại mục, đánh dấu mất đích, có thể tìm lại vị trí");
        var deleted = SampleDataBuilder.DeleteTarget(set.BrokenTarget);
        report.AppendLine(CultureInfo.InvariantCulture, $"Xóa đích: {(deleted ? "Thành công" : "Thất bại")} → {set.BrokenTarget}");
        report.AppendLine();

        var brokenEntry = byKey.GetValueOrDefault("broken-later");
        Check(checks, "Đích của mẫu hỏng đã bị xóa", deleted, set.BrokenTarget);

        var missingReport = host.Maintenance.RefreshMissingTargets();
        Check(checks, "Kiểm tra lại phát hiện đúng 1 mục bị mất đích", missingReport.Missing == 1, $"Thực tế {missingReport.Missing} mục");
        Check(checks, "Trạng thái mất đích thay đổi nên đã ghi lại vào tệp", missingReport.Changed, $"Changed={missingReport.Changed}, mất đích {missingReport.Missing} mục");

        var afterCheck = new AppLibraryStore(paths.AppLibraryFile);
        afterCheck.Load();
        var stillThere = brokenEntry is not null && afterCheck.Find(brokenEntry.Id) is not null;
        Check(checks, "Mục bị mất đích vẫn còn trong tệp, không bị loại bỏ", stillThere, stillThere ? "Bản ghi vẫn còn trong tệp" : "Bản ghi đã biến mất");
        Check(checks, "Mục được đánh dấu mất đích sau khi tải lại",
            brokenEntry is not null && afterCheck.Find(brokenEntry.Id)?.IsTargetMissing == true,
            brokenEntry is null ? "Thiếu mục mẫu" : $"Cờ đã lưu={afterCheck.Find(brokenEntry.Id)?.IsTargetMissing}");

        Section(report, "VII. Mục bị mất đích không làm gián đoạn việc khởi chạy");
        if (brokenEntry is not null)
        {
            var launch = host.Launch.LaunchItem(brokenEntry.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"Mở mục bị mất đích → Thành công={launch.Succeeded}, Phân loại={launch.Failure}, Thông báo={launch.Message}");
            report.AppendLine();

            Check(checks, "Mở mục bị mất đích thất bại nhưng có phân loại rõ ràng", !launch.Succeeded && launch.Failure == LaunchFailureReason.TargetMissing,
                $"Thành công={launch.Succeeded}, Phân loại={launch.Failure}");
            Check(checks, "Thông báo trả về bằng tiếng Việt và hướng dẫn tìm lại vị trí",
                launch.Message?.Contains("tìm lại vị trí", StringComparison.Ordinal) == true,
                launch.Message ?? "(không có thông báo)");
        }

        var healthy = byKey.GetValueOrDefault("program");
        if (healthy is not null)
        {
            // Chỉ đối chiếu **phán đoán** ở tầng dịch vụ, không mở thật mục đó lên.
            //
            // Trước đây chỗ này gọi LaunchItem để mở thật một lần, cái giá phải trả là để lại cửa sổ trên màn hình người dùng:
            // mở tài liệu sẽ bật Notepad, mở thư mục sẽ bật Explorer; nếu đích của bản ghi đó
            // vừa hay không còn nữa (thư mục mẫu đã bị dọn), Notepad còn bật hộp thoại "Hệ thống không tìm thấy đường dẫn được chỉ định".
            // "Khi thư viện có mục mất đích, mục bình thường không bị tính là mất đích" vốn là bài toán phán đoán;
            // thao tác mở thật được bao phủ bởi lệnh `--launch` và kiểm tra thủ công.
            var healthyGone = LaunchService.IsTargetGone(healthy);
            var brokenStillReported = brokenEntry is null
                || host.Launch.LaunchItem(brokenEntry.Id).Failure == LaunchFailureReason.TargetMissing;

            report.AppendLine(CultureInfo.InvariantCulture,
                $"Mục bình thường ({healthy.DisplayName}) đích tồn tại={!healthyGone}, chưa bị đánh dấu mất đích={!healthy.IsTargetMissing}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"Mục bị mất đích vẫn được báo đúng là TargetMissing={brokenStillReported}");
            report.AppendLine();

            Check(checks, "Khi thư viện có mục mất đích, mục bình thường không bị mất đích",
                !healthyGone && !healthy.IsTargetMissing && brokenStillReported,
                healthyGone ? "Đích của mục bình thường bị phán đoán là không tồn tại" : "Mục bình thường được phán đoán bình thường, mục mất đích vẫn được báo đúng");
        }

        Section(report, "VIII. Tìm lại vị trí");
        var relocationTarget = Path.Combine(set.Directory, "Đích sau khi tìm lại vị trí.cmd");
        File.WriteAllText(relocationTarget, "@echo off" + Environment.NewLine, new UTF8Encoding(false));

        if (brokenEntry is not null)
        {
            // Việc tìm lại vị trí sẽ sửa tại chỗ bản ghi này, nên cần lưu lại đường dẫn cũ trước,
            // nếu không khi nhập lại sau đó sẽ lấy nhầm đường dẫn mới và ra kết quả "Trùng lặp 1".
            var originalBrokenTarget = brokenEntry.Target;

            var outcome = host.Maintenance.Relocate(brokenEntry.Id, relocationTarget);
            report.AppendLine(CultureInfo.InvariantCulture, $"Tìm lại vị trí → {outcome.Result}: {outcome.Message}");
            report.AppendLine();

            Check(checks, "Tìm lại vị trí thành công", outcome.Result == RelocateResult.Relocated, outcome.Message);

            var afterRelocate = new AppLibraryStore(paths.AppLibraryFile);
            afterRelocate.Load();
            var relocated = afterRelocate.Find(brokenEntry.Id);

            Check(checks, "Đích mới đã được lưu xuống đĩa", relocated?.Target == relocationTarget, relocated?.Target ?? "Bản ghi đã biến mất");
            Check(checks, "Cờ mất đích đã được xóa", relocated?.IsTargetMissing == false, $"{relocated?.IsTargetMissing}");
            Check(checks, "Nguồn biểu tượng đi theo đích mới", relocated?.IconSource == relocationTarget, relocated?.IconSource ?? "(trống)");

            // Phải chặn trường hợp đụng độ: không được phép đổi nếu đã có bản ghi khác trỏ tới vị trí này.
            var collision = host.Maintenance.Relocate(healthy?.Id ?? string.Empty, relocationTarget);
            Check(checks, "Từ chối tìm lại vị trí tới nơi đã bị chiếm dụng",
                collision.Result == RelocateResult.ConflictsWithExisting,
                $"Thực tế {collision.Result}");

            // Trang web không hỗ trợ tìm lại vị trí.
            var urlEntry = byKey.GetValueOrDefault("url-file");
            if (urlEntry is not null)
            {
                var urlRelocate = host.Maintenance.Relocate(urlEntry.Id, relocationTarget);
                Check(checks, "Trang web không chấp nhận tìm lại vị trí",
                    urlRelocate.Result == RelocateResult.NotRelocatable,
                    $"Thực tế {urlRelocate.Result}");
            }

            // Việc tìm lại vị trí đã giải phóng vị trí cũ, nên vị trí cũ phải có thể nhập lại được.
            // Kết quả rà soát phát hiện: khóa cũ vẫn nằm lại trong chỉ mục chống trùng chưa được dọn, nên nhập lại đường dẫn cũ ra
            // "Thêm mới 0, Trùng lặp 1", một lượt nhập hợp lệ bị từ chối.
            //
            // Tệp ở đường dẫn cũ đã bị xóa ở Mục VI, cần tạo lại đúng tệp đó trước —
            // nếu không quá trình nhập sẽ báo đúng là "Không tìm thấy", khi đó lại kiểm tra sang chuyện khác.
            File.WriteAllText(originalBrokenTarget, "@echo off" + Environment.NewLine);

            var reimport = host.Import.ImportItems([new ImportRequest { Source = originalBrokenTarget }]);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"Nhập lại vị trí cũ {originalBrokenTarget} → Thêm mới {reimport.Added.Count}, Trùng lặp {reimport.Duplicates.Count}, Lỗi {reimport.Failures.Count}");
            report.AppendLine();

            Check(checks, "Sau khi tìm lại vị trí, vị trí cũ có thể được nhập lại",
                reimport.Added.Count == 1 && reimport.Duplicates.Count == 0 && reimport.Failures.Count == 0,
                reimport.Failures.Count > 0
                    ? $"Lỗi: {reimport.Failures[0].Message}"
                    : $"Thêm mới {reimport.Added.Count}, Trùng lặp {reimport.Duplicates.Count}");
        }

        Section(report, "IX. Quét menu Start");
        var scan = AppServiceHost.ScanStartMenu();
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục gốc được quét: {string.Join("; ", scan.Roots)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Tìm thấy {scan.ScannedFileCount} tệp .lnk, sau khi lọc trùng còn {scan.Shortcuts.Count} mục (nhường chỗ do trùng tên {scan.DuplicateNameCount} mục)");

        var samplesByGroup = scan.Shortcuts
            .GroupBy(shortcut => shortcut.Group.Length == 0 ? "(Gốc)" : shortcut.Group, StringComparer.CurrentCulture)
            .OrderByDescending(group => group.Count())
            .Take(15)
            .ToList();

        foreach (var group in samplesByGroup)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {group.Count(),3}  · {group.Key}");
        }

        report.AppendLine();
        Check(checks, "Cả hai thư mục gốc của menu Start đều được quét", scan.Roots.Count == 2, $"Thực tế {scan.Roots.Count} thư mục");
        Check(checks, "Đã quét được các lối tắt thực tế", scan.ScannedFileCount > 0, $"Quét được {scan.ScannedFileCount} tệp .lnk");
        Check(checks, "Việc lọc trùng có hiệu lực hoặc thực sự không cần lọc trùng",
            scan.ScannedFileCount >= scan.Shortcuts.Count,
            $"Ban đầu {scan.ScannedFileCount}, sau lọc trùng {scan.Shortcuts.Count}");

        Section(report, "X. Các mục trong menu Start thực sự nhập được (lấy 40 mục đầu)");
        var sampleForImport = scan.Shortcuts.Take(40).ToList();
        var menuImport = host.ImportStartMenuShortcuts(sampleForImport);
        report.AppendLine(CultureInfo.InvariantCulture, $"Thử nhập {sampleForImport.Count} mục: thêm mới {menuImport.Added.Count}, trùng lặp {menuImport.Duplicates.Count}, lỗi {menuImport.Failures.Count}");

        if (menuImport.Failures.Count > 0)
        {
            report.AppendLine("Chi tiết lỗi:");

            foreach (var failure in menuImport.Failures)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}: {failure.Message}");
            }
        }

        report.AppendLine();

        var kinds = menuImport.Added
            .GroupBy(entry => entry.Kind)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"Phân bố loại của các mục thêm mới: {string.Join(", ", kinds)}");
        report.AppendLine();

        Check(checks, "Phần lớn các mục trong menu Start đều nhập được", menuImport.Added.Count >= sampleForImport.Count / 2,
            $"Thử {sampleForImport.Count} mục, thêm mới {menuImport.Added.Count} mục, lỗi {menuImport.Failures.Count} mục");

        Section(report, "XI. Quy mô 500 mục");
        var seeded = Seed(host, 500);
        report.AppendLine(CultureInfo.InvariantCulture, $"Mục tổng hợp: thêm mới {seeded.Added} mục, bỏ qua do trùng lặp {seeded.Skipped} mục");

        var bigStore = new AppLibraryStore(paths.AppLibraryFile);
        bigStore.Load();
        var bigCount = bigStore.Items.Count(entry => entry is not null && entry.IsUsable);
        report.AppendLine(CultureInfo.InvariantCulture, $"Tải lại: {bigCount} mục");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bigMissing = host.Maintenance.RefreshMissingTargets(persist: false);
        sw.Stop();

        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian kiểm tra lại mất đích toàn thư viện: {sw.Elapsed.TotalMilliseconds:F1} ms (đã kiểm tra {bigMissing.Checked} mục)");
        report.AppendLine();

        Check(checks, "Thư viện ứng dụng đạt từ 500 mục trở lên", bigCount >= 500, $"Thực tế {bigCount} mục");
        Check(checks, "Tải lại 500 mục không gặp bất thường", bigCount == host.Library.Items.Count(entry => entry is not null && entry.IsUsable),
            "Số lượng mục sau khi tải lại không khớp với trong bộ nhớ");
        Check(checks, "Kiểm tra lại toàn thư viện hoàn tất trong vòng 2 giây", sw.Elapsed.TotalSeconds < 2.0, $"Thời gian {sw.Elapsed.TotalMilliseconds:F0} ms");

        Section(report, "XII. Kích hoạt cửa sổ: đang chạy thì không mở thực thể thứ hai");
        var activation = RunActivationCheck(host, report);

        if (activation.Skipped)
        {
            // Chưa đủ điều kiện (thiếu chương trình đích, hoặc trên màn hình người dùng đang mở sẵn cửa sổ đích): ghi nhận đúng là bỏ qua,
            // tuyệt đối không giả mạo thành "Đạt".
            Skipped(checks, "Kiểm tra kích hoạt cửa sổ", activation.Detail);
        }
        else
        {
            Check(checks, "Yêu cầu lần thứ nhất sẽ khởi chạy ứng dụng", activation.Launched, activation.Detail);
            Check(checks, "Yêu cầu lần thứ hai tìm thấy cửa sổ đang chạy", activation.FoundWindow, activation.Detail);
            Check(checks, "Yêu cầu lần thứ hai đưa cửa sổ ra phía trước thay vì mở thêm cái mới", activation.Activated, activation.Detail);
        }

        Section(report, "XIII. Xuất bảng liên hệ biểu tượng làm tài liệu đối chiếu thủ công");
        var sheetPath = arguments[0];
        var drawn = RenderContactSheet(host, workspace.GetOutput("image"), sheetPath, report);
        Check(checks, "Bảng liên hệ có chứa biểu tượng", drawn > 0, $"Đã vẽ {drawn} biểu tượng");

        Section(report, "XIV. Khôi phục chéo thư mục: đổi sang thư mục dữ liệu khác vẫn dùng được");
        var crossDirectory = RunCrossDirectoryRestore(host, paths, workspace, report);
        Check(checks, "Sau khi khôi phục sang thư mục dữ liệu khác, tệp biểu tượng đi kèm đúng vị trí", crossDirectory.FileRestored, crossDirectory.Detail);
        Check(checks, "Tệp apps.json sau khôi phục trỏ về thư mục mới thay vì thư mục cũ", crossDirectory.ReferenceRepointed, crossDirectory.Detail);
        Check(checks, "Sau khi xóa thư mục gốc ban đầu, biểu tượng vẫn đọc được", crossDirectory.StillReadable, crossDirectory.Detail);

        // ---- Tổng kết ----
        report.AppendLine();
        report.AppendLine("Tổng kết kiểm tra");
        report.AppendLine(new string('-', 46));

        foreach (var (label, status, detail) in checks)
        {
            var mark = status switch
            {
                CheckStatus.Passed => "✓",
                CheckStatus.Skipped => "○",
                _ => "✗",
            };

            report.AppendLine(CultureInfo.InvariantCulture, $"{mark} {label}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    {detail}");
        }

        var failed = checks.Count(check => check.Status == CheckStatus.Failed);
        var skipped = checks.Count(check => check.Status == CheckStatus.Skipped);
        var passed = checks.Count(check => check.Status == CheckStatus.Passed);

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Tổng cộng {checks.Count} mục kiểm tra: đạt {passed}, thất bại {failed}, bỏ qua {skipped} (○ là mục chưa đủ điều kiện nên chưa xác minh).");

        AcceptanceEntryPoint.WriteText(workspace.GetOutput("report"), report.ToString());
        ReportWriter.Write(report.ToString(), null);

        return failed == 0 ? 0 : 1;
    }

    // ---- Các phần ----

    private static ImportResult ImportAll(AppServiceHost host, SampleSet set, StringBuilder report)
    {
        var requests = set.ShouldImport.Select(source => new ImportRequest
        {
            Source = source.Path,

            // Dùng chung quy tắc phán đoán với lệnh --import: chuỗi phân tích được thành http/https được xử lý như trang web.
            IsUrl = Uri.TryCreate(source.Path, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        }).ToList();

        var result = host.Import.ImportItems(requests);

        report.AppendLine(CultureInfo.InvariantCulture, $"Nhập {requests.Count} nguồn: thêm mới {result.Added.Count}, trùng lặp {result.Duplicates.Count}, lỗi {result.Failures.Count}");
        report.AppendLine();

        foreach (var entry in result.Added)
        {
            var missing = entry.IsTargetMissing ? " (đã đánh dấu mất đích)" : string.Empty;
            report.AppendLine(CultureInfo.InvariantCulture, $"  + [{entry.Kind}] {entry.DisplayName}{missing}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      Đích {entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"      Tham số {entry.Arguments}");
            }
        }

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}: {failure.Message}");
        }

        report.AppendLine();
        return result;
    }

    private static ImportResult ImportRejected(AppServiceHost host, SampleSet set, StringBuilder report)
    {
        var requests = set.ShouldFail.Select(source => new ImportRequest { Source = source.Path }).ToList();
        var result = host.Import.ImportItems(requests);

        report.AppendLine(CultureInfo.InvariantCulture, $"Nhập {requests.Count} nguồn lỗi: thêm mới {result.Added.Count}, lỗi {result.Failures.Count}");
        report.AppendLine();

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}: {failure.Message}");
        }

        report.AppendLine();
        return result;
    }

    /// <summary>Đối chiếu bản ghi trong thư viện về lại Key của mẫu dựa theo đích và tham số.</summary>
    /// <remarks>
    /// Đối chiếu chính xác theo đích trước, rồi mới lùi về theo tên hiển thị. Nếu chỉ đối chiếu theo tên hiển thị, hai URL chỉ khác cổng
    /// đều suy ra tên máy chủ là <c>localhost</c> và sẽ cùng rơi vào một bản ghi —
    /// đó là lỗi nhìn nhầm của kịch bản nghiệm thu, không phải lỗi sản phẩm.
    /// </remarks>
    private static Dictionary<string, AppEntry> MapByKey(AppServiceHost host, SampleSet set)
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        var all = host.Library.Items.Where(entry => entry is not null && entry.IsUsable).ToList();

        foreach (var source in set.ShouldImport)
        {
            Func<AppEntry, bool> matches;

            if (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // URL bắt buộc phải đối chiếu chính xác theo đích. Với hai URL chỉ khác cổng, dùng cách đoán theo tên tệp sẽ
                // rơi cả hai vào cùng một bản ghi (vì đoạn đường dẫn của chúng giống nhau), từ đó che khuất lỗi thực sự.
                matches = entry => string.Equals(entry.Target, source.Path, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var fileName = Path.GetFileNameWithoutExtension(source.Path);
                matches = entry => entry.DisplayName == fileName
                    || entry.Target.Contains(fileName, StringComparison.Ordinal);
            }

            var match = all.FirstOrDefault(matches);

            if (match is not null)
            {
                map[source.Key] = match;
            }
        }

        return map;
    }

    private static (int Total, int Failures) VerifyIcons(AppPaths paths, StringBuilder report)
    {
        if (!Directory.Exists(paths.IconCacheDirectory))
        {
            return (0, 0);
        }

        var files = Directory.GetFiles(paths.IconCacheDirectory, "*.png");
        var failures = 0;

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);

                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var visible = CountVisiblePixels(frame);

                if (visible == 0)
                {
                    failures++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} toàn pixel trong suốt");
                }
                else
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✓ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} pixel hiển thị {visible}");
                }
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException or IOException)
            {
                failures++;
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} giải mã thất bại: {ex.Message}");
            }
        }

        report.AppendLine();
        return (files.Length, failures);
    }

    private static int CountVisiblePixels(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var count = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static (int Added, int Skipped) Seed(AppServiceHost host, int count)
    {
        var added = 0;
        var skipped = 0;

        for (var index = 0; index < count; index++)
        {
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"Mục tổng hợp {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--seed {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (host.Library.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
            else
            {
                skipped++;
            }
        }

        host.Library.Save();
        return (added, skipped);
    }

    private static int RenderContactSheet(AppServiceHost host, Stream output, string outputPath, StringBuilder report)
    {
        var entries = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable && !string.IsNullOrWhiteSpace(entry.IconSource))
            .GroupBy(entry => entry.IconSource!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(60)
            .ToList();

        const int Cell = 64;
        const int Columns = 10;

        var rows = Math.Max(1, (entries.Count + Columns - 1) / Columns);
        var stride = Columns * Cell * 4;
        var canvas = new byte[stride * rows * Cell];

        var drawn = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var iconPath = host.Icons.PathFor(entries[index].IconSource!);

            if (!File.Exists(iconPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(iconPath);

                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var source = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);

                var width = source.PixelWidth;
                var height = source.PixelHeight;
                var sourceStride = width * 4;
                var pixels = new byte[sourceStride * height];
                source.CopyPixels(pixels, sourceStride, 0);

                var column = index % Columns;
                var row = index / Columns;

                for (var y = 0; y < Cell; y++)
                {
                    var sourceY = y * height / Cell;

                    for (var x = 0; x < Cell; x++)
                    {
                        var sourceX = x * width / Cell;
                        var from = (sourceY * sourceStride) + (sourceX * 4);
                        var to = ((row * Cell + y) * stride) + ((column * Cell + x) * 4);

                        canvas[to] = pixels[from];
                        canvas[to + 1] = pixels[from + 1];
                        canvas[to + 2] = pixels[from + 2];
                        canvas[to + 3] = pixels[from + 3];
                    }
                }

                drawn++;
            }
            catch (Exception ex) when (ex is FileFormatException or IOException)
            {
                // Nếu biểu tượng này không vẽ được thì bỏ qua, tổng thể bảng liên hệ vẫn có ích.
            }
        }

        var png = PngWriter.EncodeBgra(canvas, Columns * Cell, rows * Cell);
        output.Write(png);
        output.Flush();

        report.AppendLine(CultureInfo.InvariantCulture, $"Có {entries.Count} mục có nguồn biểu tượng (đã lọc trùng theo nguồn), thực tế đã vẽ {drawn} biểu tượng");
        report.AppendLine(CultureInfo.InvariantCulture, $"Bảng liên hệ: {outputPath} ({Columns * Cell}×{rows * Cell})");
        report.AppendLine();

        return drawn;
    }

    // ---- Công cụ xuất ----

    /// <summary>
    /// Xác minh luồng "đang chạy thì kích hoạt".
    /// </summary>
    /// <remarks>
    /// Dùng <c>charmap.exe</c> (Character Map) làm mục tiêu thử nghiệm: đây là một trong số ít chương trình Win32 cổ điển
    /// vẫn tự sở hữu cửa sổ của mình. Bước này <strong>bắt buộc dùng ứng dụng thật</strong> — quyền sở hữu cửa sổ được xác định theo
    /// đường dẫn đầy đủ của tệp ảnh tiến trình, không thể dùng ứng dụng thế thân hay cửa sổ giả để xác minh chuỗi này.
    ///
    /// <para>
    /// <b>Giới hạn đã biết (đo thực tế)</b>: việc khớp theo đường dẫn tệp ảnh chỉ hiệu quả với các ứng dụng mà
    /// "tiến trình sở hữu cửa sổ chính là chương trình đích". Khi hệ thống chuyển giao việc thực thi cho tiến trình khác thì không khớp được, thực tế có 3 nhóm:
    /// chương trình console được lưu trữ bởi <c>WindowsTerminal.exe</c>,
    /// các stub trong <c>System32</c> (như <c>notepad.exe</c>) thực chất mở phiên bản đóng gói trong WindowsApps,
    /// cửa sổ của ứng dụng UWP thuộc về <c>ApplicationFrameHost.exe</c>.
    /// Ba nhóm này sẽ thoái hóa thành "khởi chạy lại lần nữa" — với ứng dụng đơn thực thể thì hệ thống tự đưa cửa sổ có sẵn lên trước,
    /// nên biểu hiện vẫn đúng, nhưng với chương trình đa thực thể (như cmd) sẽ mở thêm một cửa sổ nữa.
    /// Giải pháp trọn vẹn là chuyển sang dùng AppUserModelID để xác định quyền sở hữu, thuộc mục D-09 / D-10 của Giai đoạn 4.
    /// </para>
    /// </remarks>
    private static (bool Launched, bool FoundWindow, bool Activated, bool Skipped, string Detail) RunActivationCheck(
        AppServiceHost host,
        StringBuilder report)
    {
        const string Target = @"C:\Windows\System32\charmap.exe";

        if (!File.Exists(Target))
        {
            report.AppendLine($"Máy này không có {Target}, bỏ qua kiểm tra kích hoạt cửa sổ.");
            report.AppendLine();
            return (false, false, false, true, "Thiếu chương trình đích trên máy, chưa thực hiện");
        }

        var importResult = host.Import.ImportItems([new ImportRequest { Source = Target }]);

        var entry = importResult.Added.Count > 0
            ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;

        if (entry is null)
        {
            report.AppendLine("Không thể nhập chương trình đích vào thư viện ứng dụng, bỏ qua kiểm tra kích hoạt cửa sổ.");
            report.AppendLine();
            return (false, false, false, false, "Tạo mục đích thất bại");
        }

        // Người dùng có thể đang tự mở charmap trên màn hình. Lúc này không thể tắt nó (đó là cửa sổ của người dùng),
        // cũng không thể trộn trạng thái của nó vào cùng một kết luận — ghi chú trung thực rồi bỏ qua.
        // Kiểm tra xem có cửa sổ nào đang mở theo **đường dẫn tệp ảnh**, không quét toàn cục theo tên tiến trình.
        if (Platform.Shell.WindowLocator.FindWindowsForImage(Target).Count > 0)
        {
            report.AppendLine("Trên màn hình người dùng đã có cửa sổ Character Map đang chạy;");
            report.AppendLine("Để không chạm vào cửa sổ của người dùng, lượt kiểm tra kích hoạt này được bỏ qua và đánh dấu chưa xác minh.");
            report.AppendLine();
            return (false, false, false, true, "Trên màn hình người dùng đã có cửa sổ đích đang chạy, bỏ qua để không chạm vào cửa sổ của người dùng");
        }

        var first = host.Launch.ActivateOrLaunch(entry.Id);
        report.AppendLine(CultureInfo.InvariantCulture, $"Lần 1 ActivateOrLaunch → {first.Outcome} (PID tiến trình {host.Launch.LastLaunchedProcessId})");

        // Ghi lại tiến trình đích do chính mình khởi chạy: khi dọn dẹp chỉ dựa vào PID này, tuyệt đối không dọn theo tên tiến trình.
        var spawnedProcessId = host.Launch.LastLaunchedProcessId;

        // Cửa sổ không xuất hiện ngay lập tức, cần chờ một chút. Chỗ này là chờ sự kiện thực tế từ bên ngoài,
        // không phải "chờ các tác vụ giao diện đã xếp hàng chạy xong", nên dùng vòng lặp thăm dò là cách làm phù hợp.
        var windows = Array.Empty<Core.Contracts.WindowCandidate>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var title = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var located = Platform.Shell.WindowLocator.FindWindowsForImage(Target);

            if (located.Count > 0)
            {
                windows = located
                    .Select(window => new Core.Contracts.WindowCandidate { Handle = window.Handle, Title = window.Title })
                    .ToArray();
                title = located[0].Title;
                break;
            }

            Thread.Sleep(250);
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"Cửa sổ tìm thấy theo đường dẫn tệp ảnh: {windows.Length} cửa sổ{(title.Length > 0 ? $", tiêu đề \"{title}\"" : string.Empty)}");

        var second = host.Launch.ActivateOrLaunch(entry.Id);
        report.AppendLine(CultureInfo.InvariantCulture, $"Lần 2 ActivateOrLaunch → {second.Outcome}");

        if (second.Candidates.Count > 0)
        {
            foreach (var candidate in second.Candidates)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · Cửa sổ ứng viên 0x{candidate.Handle:X} {candidate.Title}");
            }
        }

        if (!string.IsNullOrEmpty(second.Message))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Giải thích: {second.Message}");
        }

        report.AppendLine();

        // Không để chương trình đích sót lại trên màn hình người dùng. Chỉ kết thúc tiến trình **do chính mình khởi chạy và ghi nhận**;
        // cửa sổ charmap do người dùng tự mở giữa chừng sẽ không bị đường dẫn này động tới.
        KillRecordedProcess(spawnedProcessId);

        var launched = first.Outcome == ActivationOutcome.Launched;
        var found = windows.Length > 0;
        var activated = second.Outcome is ActivationOutcome.Activated or ActivationOutcome.MultipleWindowsFound;

        var detail = windows.Length == 0
            ? "Không khớp được cửa sổ nào — khâu khớp theo đường dẫn tệp ảnh có thể đã hỏng"
            : $"Lần 1 {first.Outcome}, lần 2 {second.Outcome}, cửa sổ \"{title}\"";

        return (launched, found, activated, false, detail);
    }

    /// <summary>Kết quả kiểm tra khôi phục chéo thư mục.</summary>
    /// <param name="FileRestored">Tệp biểu tượng đã được sao chép sang thư mục mới theo quá trình khôi phục.</param>
    /// <param name="ReferenceRepointed">Tệp apps.json đã lưu trỏ về thư mục mới.</param>
    /// <param name="StillReadable">Sau khi xóa thư mục ban đầu, biểu tượng vẫn đọc được.</param>
    /// <param name="Detail">Mô tả hiển thị trong báo cáo.</param>
    private sealed record CrossDirectoryRestoreResult(
        bool FileRestored,
        bool ReferenceRepointed,
        bool StillReadable,
        string Detail);

    /// <summary>
    /// Sao lưu ra tệp lưu trữ, khôi phục sang <strong>một thư mục dữ liệu khác</strong>, rồi xóa thư mục ban đầu.
    /// </summary>
    /// <remarks>
    /// Đây là mô hình thực tế của việc đổi máy tính / di chuyển thư mục dữ liệu. Lỗi phát hiện ở vòng rà soát thứ hai chính là tại đây:
    /// tệp biểu tượng đã được sao chép sang thư mục mới, nhưng <c>CustomIconPath</c> vẫn trỏ về thư mục cũ,
    /// hễ xóa thư mục cũ là biểu tượng hỏng ngay — hơn nữa tiêu chí lúc đó là "chỉ ghi lại khi đường dẫn cũ không tồn tại" nên trên
    /// cùng một máy sẽ không bao giờ kích hoạt, vì thư mục cũ vẫn còn.
    /// </remarks>
    private static CrossDirectoryRestoreResult RunCrossDirectoryRestore(
        AppServiceHost host,
        AppPaths paths,
        Glassquid.Platform.Storage.AcceptanceWorkspace workspace,
        StringBuilder report)
    {
        // Gắn cho một bản ghi biểu tượng tùy chỉnh: đây là dữ liệu người dùng được sao chép từ đĩa vào icons/,
        // không thuộc loại bộ nhớ đệm có thể trích xuất lại, vì vậy mới cần đi theo bản sao lưu.
        var entry = host.Library.Items.FirstOrDefault(item => item is not null && item.IsUsable);

        if (entry is null)
        {
            return new CrossDirectoryRestoreResult(false, false, false, "Không có mục khả dụng nào trong thư viện ứng dụng");
        }

        var iconSource = Path.Combine(paths.RootDirectory, "Nghiệm thu biểu tượng tùy chỉnh.png");
        File.WriteAllBytes(iconSource, PngWriter.EncodeBgra(new byte[16 * 16 * 4], 16, 16));

        if (!host.Maintenance.SetCustomIcon(entry.Id, iconSource))
        {
            return new CrossDirectoryRestoreResult(false, false, false, "Thiết lập biểu tượng tùy chỉnh thất bại");
        }

        var originalIcon = host.Library.Find(entry.Id)!.CustomIconPath!;
        var archiveDirectory = Path.Combine(paths.BackupDirectory, "Nghiệm thu lưu trữ");

        var backup = new Core.Storage.BackupService(paths).BackupTo(archiveDirectory);

        if (!backup.Succeeded)
        {
            return new CrossDirectoryRestoreResult(false, false, false, backup.Error ?? "Sao lưu thất bại");
        }

        // Ngôi nhà mới: một thư mục dữ liệu khác, đại diện cho %APPDATA% trên một máy tính khác.
        var newHome = new AppPaths(workspace.CreateChildDataRoot("phase3-cross-directory-restore"));
        var restore = new Core.Storage.BackupService(newHome).RestoreFrom(archiveDirectory);

        report.AppendLine(CultureInfo.InvariantCulture, $"Sao lưu → {archiveDirectory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Khôi phục → {newHome.RootDirectory}: {(restore.Succeeded ? "Thành công" : restore.Error)}");
        report.AppendLine("Đích khôi phục nằm trong không gian làm việc nghiệm thu tách biệt lần này, được giữ lại để đối chiếu.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Đã sửa lại {restore.RepairedIconReferences} tham chiếu biểu tượng");
        report.AppendLine();

        if (!restore.Succeeded)
        {
            return new CrossDirectoryRestoreResult(false, false, false, restore.Error ?? "Khôi phục thất bại");
        }

        var iconInNewHome = AppPaths.ManagedCustomIconPath(newHome.RootDirectory, Path.GetFileName(originalIcon));
        var fileRestored = File.Exists(iconInNewHome);

        // Tệp apps.json đã lưu phải trỏ tới thư mục mới. Bước này từng bị bỏ sót khi chạy trên cùng một máy:
        // thư mục cũ vẫn còn, đường dẫn cũ vẫn phân giải được bình thường, nên không ai sửa lại nó.
        var storedLibrary = new AppLibraryStore(newHome.AppLibraryFile);
        storedLibrary.Load();
        var storedPath = storedLibrary.Find(entry.Id)?.CustomIconPath;
        var referenceRepointed = string.Equals(storedPath, iconInNewHome, StringComparison.OrdinalIgnoreCase);

        report.AppendLine(CultureInfo.InvariantCulture, $"Tệp biểu tượng trong thư mục mới: {iconInNewHome} ({(fileRestored ? "tồn tại" : "thiếu")})");
        report.AppendLine(CultureInfo.InvariantCulture, $"Tham chiếu đã lưu: {storedPath}");
        report.AppendLine();

        // Xóa tệp biểu tượng gốc, rồi đọc lại một lần nữa — đây mới là trạng thái sau khi "đổi máy tính" hoàn tất:
        // bản cũ đã không còn nữa, tham chiếu trong thư mục mới phải tự đứng vững được.
        // (Cố ý chỉ xóa duy nhất tệp này chứ không xóa cả thư mục dữ liệu gốc —
        // làm vậy sẽ phá luôn thư mục dữ liệu của chính lần nghiệm thu này.)
        File.Delete(originalIcon);

        var reloaded = new AppLibraryStore(newHome.AppLibraryFile);
        reloaded.Load();
        var finalPath = reloaded.Find(entry.Id)?.CustomIconPath;
        var stillReadable = File.Exists(finalPath);

        report.AppendLine(CultureInfo.InvariantCulture, $"Sau khi xóa biểu tượng gốc: tham chiếu {finalPath}, tệp {(stillReadable ? "đọc được" : "thiếu")}");
        report.AppendLine();

        var detail = $"Biểu tượng {Path.GetFileName(originalIcon)} → thư mục mới {(fileRestored ? "đã có mặt" : "thiếu")}, "
            + $"tham chiếu {(referenceRepointed ? "đã được sửa lại" : $"vẫn là {storedPath}")}, "
            + $"sau khi xóa biểu tượng gốc {(stillReadable ? "vẫn đọc được" : "bị hỏng")}";

        return new CrossDirectoryRestoreResult(fileRestored, referenceRepointed, stillReadable, detail);
    }

    /// <summary>
    /// Kết thúc tiến trình đích do chính bước nghiệm thu khởi chạy và ghi nhận.
    /// </summary>
    /// <remarks>
    /// Cố ý không dọn theo tên tiến trình: làm vậy sẽ tắt nhầm cửa sổ cùng tên do người dùng tự mở.
    /// Chỉ nhận đúng PID đã ghi lại lúc khởi chạy; nếu tiến trình không còn thì coi như đã thoát.
    /// Nghiệm thu Giai đoạn 4 dùng chung kỷ luật này nên chia sẻ ở mức internal.
    /// </remarks>
    internal static void KillRecordedProcess(uint processId)
    {
        if (processId == 0)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Tiến trình đã không còn (hoặc không lấy được handle) — nó vốn là mục tiêu do chính mình bật lên,
            // thoát là đạt mục đích rồi, không tính là lỗi.
        }
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', 46));
    }

    private static void Check(List<(string Label, CheckStatus Status, string Detail)> checks, string label, bool passed, string detail)
        => checks.Add((label, passed ? CheckStatus.Passed : CheckStatus.Failed, detail));

    /// <summary>Kiểm tra chưa chạy do không đủ điều kiện — không tính là đạt cũng không tính là thất bại.</summary>
    private static void Skipped(List<(string Label, CheckStatus Status, string Detail)> checks, string label, string detail)
        => checks.Add((label, CheckStatus.Skipped, detail));

    private static string DescribeFailures(ImportResult result)
        => result.Failures.Count == 0
            ? "Không có lỗi nào"
            : string.Join("; ", result.Failures.Select(failure => $"{failure.Reason}"));

    private static string DescribeKeys(Dictionary<string, AppEntry> map)
        => map.Count == 0 ? "Không khớp được mục nào" : string.Join(", ", map.Keys);
}
