using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.Platform.Displays;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>
/// Nghiệm thu không giao diện của Giai đoạn 4. Cấu trúc giống với <see cref="Phase3Acceptance"/>:
/// chạy thực tế một lượt, khẳng định từng hạng mục, ghi báo cáo, chỉ thao tác trong thư mục dữ liệu tạm biệt lập,
/// <strong>tuyệt đối không đụng vào thư viện ứng dụng thật của người dùng</strong>.
/// </summary>
/// <remarks>
/// Các khẳng định tích lũy theo từng đợt (hoàn thành một đợt sẽ bổ sung kiểm tra của đợt đó):
/// <list type="bullet">
/// <item><b>Đợt 1</b>: lưu và đọc danh tính tường minh, quy tắc khớp sở hữu cửa sổ,
/// liệt kê ban đầu và đối soát trạng thái đang chạy, luồng cổ điển ưu tiên kích hoạt.</item>
/// </list>
///
/// <para>
/// Kỷ luật tiến trình mục tiêu: tự động hóa chỉ khởi chạy tiến trình mục tiêu mà chính nó đã ghi lại pid, và khi thu dọn chỉ xử lý theo pid đã ghi;
/// nếu trên màn hình của người dùng đã có cửa sổ mục tiêu thì bỏ qua và đánh dấu "Chưa xác minh", tuyệt đối không dọn theo tên tiến trình.
/// </para>
/// </remarks>
internal static class Phase4Acceptance
{
    private const string ClassicTarget = @"C:\Windows\System32\charmap.exe";

    /// <summary>Danh tính của Notepad bản đóng gói (dạng đo thực tế bởi công cụ thăm dò Giai đoạn 4).</summary>
    private const string NotepadAumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, Glassquid.Platform.Storage.AcceptanceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(arguments);

        var report = new StringBuilder();
        var checks = new List<(string Label, CheckStatus Status, string Detail)>();

        var paths = new AppPaths(workspace.DataRoot);
        paths.EnsureCreated();

        report.AppendLine("Glassquid - Nghiệm thu Giai đoạn 4");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu nghiệm thu: {paths.RootDirectory} (thư mục tạm biệt lập, giữ lại sau khi chạy để đối chiếu)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu thật của người dùng: {realPaths.RootDirectory} — không bị thay đổi trong suốt quá trình");
        report.AppendLine();

        var data = new GlassquidDataStore(paths);
        data.LoadAll();
        using var host = new AppServiceHost(data);
        host.Running.Start();

        Section(report, "1. Danh tính tường minh được lưu vào mục khi nhập");
        CheckImportedIdentity(host, paths, checks, report);

        Section(report, "2. Quy tắc khớp sở hữu cửa sổ (khẳng định hàm thuần trên ảnh chụp giả lập)");
        CheckMatchingRules(checks, report);

        Section(report, "3. Trạng thái đang chạy: liệt kê ban đầu và đối soát xuất hiện/biến mất");
        var runningTargetAvailable = CheckRunningTracker(host, checks, report);

        Section(report, "4. Ưu tiên kích hoạt: lần đầu khởi chạy, lần hai kích hoạt (D-09)");
        if (runningTargetAvailable)
        {
            CheckActivateOrLaunch(host, checks, report);
        }
        else
        {
            Skip(checks, report, "Trên màn hình người dùng đã có cửa sổ Character Map, bỏ qua kiểm tra kích hoạt (tránh tác động vào cửa sổ của người dùng)");
        }

        Section(report, "5. Giá trị mặc định (D-21/D-22): ghi vào tệp cấu hình rồi đọc lại để đối chiếu");
        CheckSettingsDefaults(paths, checks, report);

        Section(report, "6. Sắp đặt vị trí: 4 vị trí x từng màn hình đặt thực tế trên máy (D-15/D-23)");
        CheckPhysicalPlacement(checks, report);

        Section(report, "7. Đỉnh phóng đại điều chỉnh được (D-17): tệp cài đặt điều khiển hình học");
        CheckMagnificationFromSettings(paths, checks, report);

        Section(report, "8. Ghim, sắp xếp thứ tự và gỡ bỏ (tầng dịch vụ của D-02/D-03/R-08)");

        Section(report, "10. Tính đúng đắn với thư viện 500 mục và hàng ghim siêu dài");
        CheckScale(host, paths, checks, report);
        CheckPinnedAndReorder(host, paths, checks, report);

        Section(report, "9. Lối vào tích hợp sẵn (D-14: Recycle Bin mở qua shell; từ chối mục tiêu shell: viết sai)");
        CheckRecycleBinEntry(checks, report);

        // ---- Tổng hợp ----
        report.AppendLine();
        report.AppendLine("Tổng hợp kiểm tra");
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
            $"Tổng cộng {checks.Count} hạng mục kiểm tra: đạt {passed}, thất bại {failed}, bỏ qua {skipped}.");
        report.AppendLine("○ là các hạng mục \"chưa đủ điều kiện nên chưa chạy\" — chúng không được tính là đạt cũng không tính là thất bại, được liệt kê là Chưa xác minh trong báo cáo bàn giao.");
        report.AppendLine("Việc kích hoạt thực tế ứng dụng đóng gói và UWP, tương tác danh sách chọn nhiều cửa sổ, và hiển thị giao diện chấm trạng thái đang chạy");
        report.AppendLine("không được bao phủ trọn vẹn bởi lệnh này; hãy chạy thêm build/build.ps1 -UiAcceptance và đối chiếu trạng thái bao phủ bên ngoài trong báo cáo nghiệm thu tương tác tự động Giai đoạn 4.");

        // Thứ tự rất quan trọng: in -> ghi nhật ký -> xóa thư mục.
        // Bản thân tệp nhật ký nằm trong thư mục biệt lập; nếu xóa thư mục trước, bước ghi nhật ký sẽ tạo lại nó
        // (thực tế đo được: báo cáo ghi "đã dọn dẹp", nhưng thư mục vẫn còn).
        Console.Write(report.ToString());
        AcceptanceEntryPoint.WriteText(workspace.GetOutput("report"), report.ToString());
        ReportWriter.Write(report.ToString(), null);

        return failed == 0 ? 0 : 1;
    }

    // ---- Các phần của Đợt 2 ----

    /// <summary>
    /// D-21/D-22: ghi thực tế cài đặt mặc định xuống đĩa, rồi đọc lại từ tệp để đối chiếu các giá trị mặc định.
    /// </summary>
    /// <remarks>
    /// Chỉ đọc giá trị mặc định trong bộ nhớ thì không được tính — phương thức nghiệm thu ghi rõ "đọc tệp cấu hình để đối chiếu giá trị mặc định".
    /// Tạo một tệp cài đặt riêng trong thư mục biệt lập của chính nghiệm thu, không liên quan đến cài đặt thật của người dùng.
    /// </remarks>
    private static void CheckSettingsDefaults(
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var settingsPaths = new AppPaths(Path.Combine(paths.RootDirectory, "Kiểm tra giá trị mặc định"));
        var settingsStore = new SettingsStore(settingsPaths.SettingsFile);
        settingsStore.Save();

        // Tải lại từ đĩa để đảm bảo nội dung được khẳng định là dữ liệu trong tệp chứ không phải đối tượng trong bộ nhớ.
        var reloaded = new SettingsStore(settingsPaths.SettingsFile);
        _ = reloaded.Load();
        var dock = reloaded.Current.Dock;
        var appearance = reloaded.Current.Appearance;

        Check(checks, "Kích thước biểu tượng mặc định 48 DIP (D-21)", dock.IconSize == 48, $"{dock.IconSize}");
        Check(checks, "Khoảng cách biểu tượng mặc định 8 DIP (D-21)", dock.IconSpacing == 8, $"{dock.IconSpacing}");
        Check(checks, "Đỉnh phóng đại mặc định 1.35× (D-21)", Math.Abs(dock.MagnificationPeak - 1.35) < 0.0001, $"{dock.MagnificationPeak}");
        Check(checks, "Bo góc Dock kính lỏng 40 DIP", appearance.DockCornerRadius == 40, $"{appearance.DockCornerRadius}");
        Check(checks, "Độ trễ rời chuột mặc định khoảng 300 ms (D-19)",
            dock.AutoHideLeaveDelayMs is >= 250 and <= 350, $"{dock.AutoHideLeaveDelayMs} ms");

        report.AppendLine(CultureInfo.InvariantCulture, $"Tệp đối chiếu: {settingsPaths.SettingsFile}");
        report.AppendLine();
    }

    /// <summary>
    /// Đặt thực tế trên máy: với mỗi màn hình × 4 vị trí, đặt một cửa sổ thật theo kết quả tính toán của
    /// <see cref="DockPlacementCalculator"/>, đọc lại hình chữ nhật cửa sổ và khẳng định khớp cũng như không vượt ra ngoài vùng làm việc.
    /// </summary>
    /// <remarks>
    /// Tầng này xác minh việc "cửa sổ WPF + <see cref="DisplayService.MoveWindowToPixelRect"/>
    /// vào đúng vị trí theo kết quả tính toán trong môi trường đa màn hình DPI hỗn hợp thực tế"; bản thân tính chất hình học của phép tính
    /// được khóa bằng kiểm thử đơn vị ngoại tuyến (<c>DockPlacementCalculatorTests</c>), hai tầng gộp lại mới tạo thành D-15.
    /// </remarks>
    private static void CheckPhysicalPlacement(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var displays = DisplayService.Enumerate();

        if (displays.Count == 0)
        {
            Check(checks, "Liệt kê được ít nhất một màn hình", false, "Kết quả liệt kê rỗng");
            return;
        }

        Check(checks, "Liệt kê màn hình khả dụng", true, $"Đã liệt kê được {displays.Count} màn hình");

        if (displays.Count < 2)
        {
            Skip(checks, report, "Không có màn hình phụ, kiểm tra sắp đặt đa màn hình chỉ chạy trên màn hình chính");
        }

        // Một cửa sổ thật đơn giản là đủ: thứ cần xác minh là chuỗi sắp đặt vị trí, không phải giao diện kính.
        var probeWindow = new System.Windows.Window
        {
            Width = 900,
            Height = 90,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Transparent },
        };

        try
        {
            probeWindow.Show();

            var handle = new System.Windows.Interop.WindowInteropHelper(probeWindow).Handle;

            foreach (var display in displays)
            {
                foreach (DockPlacement placement in Enum.GetValues<DockPlacement>())
                {
                    // Hình chữ nhật kỳ vọng: kích thước DIP được quy đổi theo DPI riêng của màn hình này.
                    var widthPixels = Math.Max(1, display.ToPixels(probeWindow.ActualWidth));
                    var heightPixels = Math.Max(1, display.ToPixels(probeWindow.ActualHeight));

                    var expected = DockPlacementCalculator.Calculate(
                        display.WorkArea, placement, 0, 0, widthPixels, heightPixels, display.Dpi);

                    _ = DisplayService.MoveWindowToPixelRect(handle, expected);
                    var actual = DisplayService.GetWindowPixelBounds(handle);

                    var ok = actual is not null
                        && Math.Abs(actual.Value.X - expected.X) <= 1
                        && Math.Abs(actual.Value.Y - expected.Y) <= 1
                        && actual.Value.X >= display.WorkArea.X
                        && actual.Value.Y >= display.WorkArea.Y
                        && actual.Value.Right <= display.WorkArea.Right
                        && actual.Value.Bottom <= display.WorkArea.Bottom;

                    Check(checks, $"{display.DeviceName}{(display.IsPrimary ? " (Màn hình chính)" : string.Empty)} {placement} đặt đúng vị trí tính toán và không tràn khỏi vùng làm việc",
                        ok, $"Kỳ vọng {expected}, thực tế {actual?.ToString() ?? "không đọc được"}");
                }
            }
        }
        finally
        {
            probeWindow.Close();
        }

        report.AppendLine("Đã đặt thực tế và đọc lại đối chiếu cho từng màn hình × 4 vị trí; khi thiếu màn hình phụ sẽ ghi nhận trung thực là Chưa xác minh.");
        report.AppendLine();
    }


    /// <summary>
    /// D-17: ghi một đỉnh phóng đại không mặc định vào tệp cài đặt, đọc lại để điều khiển tính toán hình học,
    /// khẳng định đỉnh phóng đại có hiệu lực thực sự — chuỗi đầy đủ của "điều chỉnh được" là Tệp cài đặt → Đọc → Hình học.
    /// </summary>
    private static void CheckMagnificationFromSettings(
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var settingsPaths = new AppPaths(Path.Combine(paths.RootDirectory, "Kiểm tra đỉnh phóng đại"));
        var store = new SettingsStore(settingsPaths.SettingsFile);
        _ = store.Load();
        store.Current.Dock.MagnificationPeak = 2.0;
        store.Save();

        var reloaded = new SettingsStore(settingsPaths.SettingsFile);
        _ = reloaded.Load();
        var peak = reloaded.Current.Dock.MagnificationPeak;

        Check(checks, "Đỉnh phóng đại trong tệp cài đặt được đọc lại nguyên vẹn", Math.Abs(peak - 2.0) < 0.0001, $"{peak}");

        var geometry = Glassquid.Dock.DockMagnification.Compute(
            5, reloaded.Current.Dock.IconSize, reloaded.Current.Dock.IconSpacing, peak,
            Glassquid.Dock.DockMagnification.SlotCenter(2, reloaded.Current.Dock.IconSize, reloaded.Current.Dock.IconSpacing, peak));

        Check(checks, "Đỉnh phóng đại đọc lại điều khiển hình học: ô dưới con trỏ đạt đúng giá trị đỉnh",
            Math.Abs(geometry[2].Scale - 2.0) < 0.0001, $"Tỷ lệ ô thứ 2 là {geometry[2].Scale}");
    }

    /// <summary>
    /// Khẳng định tầng dịch vụ cho ghim, sắp xếp thứ tự và gỡ bỏ (D-02/D-03/R-08). Các thao tác kéo thả trên giao diện
    /// cuối cùng đều đi xuống tầng này: xem trước chỉ sửa bộ nhớ, xác nhận chỉ ghi đĩa đúng một lần, gỡ bỏ không đụng vào thư viện ứng dụng.
    /// </summary>
    private static void CheckPinnedAndReorder(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        // Chuẩn bị hai mục khả dụng: charmap + một tệp mẫu thật.
        var importResult = host.Import.ImportItems([
            new ImportRequest { Source = ClassicTarget },
            new ImportRequest { Source = @"C:\Windows\System32\drivers\etc\hosts" },
        ]);

        // charmap có thể đã được nhập ở các kiểm tra kích hoạt/đối soát trước đó — gộp "thêm mới + trùng lặp" để lấy mục,
        // chỉ cần cả hai đều dùng được, không bắt buộc cả hai đều phải mới thêm lần này.
        var first = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;
        var second = importResult.Added.Count > 1 ? importResult.Added[1]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;

        if (first is null || second is null || first.Id == second.Id)
        {
            Check(checks, "Nhập mục chuẩn bị cho kiểm tra sắp xếp thành công", false,
                $"Thêm mới {importResult.Added.Count}, trùng lặp {importResult.Duplicates.Count}, thất bại {importResult.Failures.Count}");
            return;
        }

        var layout = host.Layouts;
        _ = layout.LoadDock();
        var saveCount = 0;

        // Máy trạng thái sắp xếp dùng chung một bộ với giao diện Dock: delegate lưu chính là đường dẫn ghi đĩa trong Dock.
        var model = new Glassquid.Dock.DockReorderModel(order =>
        {
            layout.Dock.PinnedItemIds = [.. order];
            layout.SaveDock();
            saveCount++;
        });

        // Ghim hai mục: xem trước vài lần (làm xáo trộn trước) -> trong lúc đó không được ghi tệp bố cục.
        // Trong thư mục sạch lúc này tệp chưa tồn tại — trong lúc xem trước nó phải "thậm chí chưa từng được tạo",
        // đây là bằng chứng mạnh hơn "nội dung không đổi".
        var fileExistedBefore = File.Exists(layout.DockFilePath);
        var before = fileExistedBefore ? File.ReadAllBytes(layout.DockFilePath) : null;
        model.Begin([first.Id, second.Id]);
        model.PreviewMove(second.Id, 0);
        model.PreviewMove(second.Id, 1); // trả lại vị trí cũ một lần, tạo xem trước vô nghĩa
        model.PreviewMove(second.Id, 0);

        var unchangedAfterPreview = fileExistedBefore
            ? before!.SequenceEqual(File.ReadAllBytes(layout.DockFilePath))
            : !File.Exists(layout.DockFilePath);

        Check(checks, "Trong lúc xem trước kéo thả tệp bố cục không thay đổi dù chỉ một byte (R-08)",
            unchangedAfterPreview,
            fileExistedBefore ? "Sau 3 lần xem trước tệp vẫn giữ nguyên nội dung ban đầu" : "Trong suốt lúc xem trước tệp bố cục hoàn toàn chưa được tạo");

        // Xác nhận: ghi đĩa đúng một lần, thứ tự là kết quả xem trước.
        var outcome = model.Commit();
        Check(checks, "Xác nhận ghi đĩa đúng một lần", outcome == Glassquid.Dock.ReorderOutcome.Committed && saveCount == 1,
            $"Kết quả {outcome}, số lần lưu {saveCount}");

        // "Khởi động lại sau khi kéo, thứ tự vẫn khớp": tải lại bố cục từ góc nhìn một tiến trình khác.
        var reloaded = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();
        Check(checks, "Sau khi tải lại (mô phỏng khởi động lại) thứ tự vẫn khớp (D-02)",
            reloaded.Dock.PinnedItemIds.Count == 2 && reloaded.Dock.PinnedItemIds[0] == second.Id,
            $"Thứ tự: {string.Join(',', reloaded.Dock.PinnedItemIds)}");

        // Hủy bằng Esc: ghi đĩa 0 lần.
        saveCount = 0;
        model.Begin([second.Id, first.Id]);
        model.PreviewMove(first.Id, 0);
        _ = model.Cancel();
        Check(checks, "Hủy kéo thả (Esc) không ghi đĩa (R-08)",
            saveCount == 0 && reloaded.Dock.PinnedItemIds[0] == second.Id, $"Số lần lưu {saveCount}");

        // Xác nhận thất bại: khôi phục lại thứ tự ban đầu.
        var failing = new Glassquid.Dock.DockReorderModel(_ => throw new IOException("Diễn tập: đĩa từ chối"));
        failing.Begin([first.Id, second.Id]);
        failing.PreviewMove(first.Id, 1);
        var failureOutcome = failing.Commit();
        Check(checks, "Khi xác nhận thất bại sẽ khôi phục thứ tự ban đầu (R-08)",
            failureOutcome == Glassquid.Dock.ReorderOutcome.SaveFailed
            && failing.Order.SequenceEqual([first.Id, second.Id]),
            $"Kết quả {failureOutcome}");

        // Tiêu chuẩn nghiệm thu của báo cáo phúc tra: **sau khi kéo sắp xếp 5 ứng dụng, khởi động lại vẫn giữ đúng thứ tự,
        // tệp bố cục vẫn chỉ có 5 ID ứng dụng** (không được lẫn entry:* của lối vào tích hợp sẵn).
        CheckFiveAppReorder(host, paths, checks, report);

        // D-03: gỡ bỏ để hủy ghim — không còn trong bố cục, nhưng vẫn còn trong thư viện ứng dụng.
        _ = layout.UnpinFromDock(second.Id);
        layout.SaveDock();
        var reloadedAfterUnpin = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloadedAfterUnpin.LoadDock();

        var stillInLibrary = host.Library.Find(second.Id) is not null
            && !reloadedAfterUnpin.Dock.PinnedItemIds.Contains(second.Id);

        Check(checks, "Sau khi gỡ bỏ và khởi động lại: bố cục không còn mục đó, trình khởi chạy (thư viện ứng dụng) vẫn còn (D-03)",
            stillInLibrary,
            $"Còn trong thư viện={host.Library.Find(second.Id) is not null}, số mục ghim={reloadedAfterUnpin.Dock.PinnedItemIds.Count}");

        report.AppendLine(CultureInfo.InvariantCulture, $"Tệp bố cục: {layout.DockFilePath}");
        report.AppendLine();
    }

    /// <summary>
    /// Khẳng định tầng dịch vụ cho D-14: shell có nhận <c>shell:RecycleBinFolder</c> không, và có nhận mục viết sai không.
    /// </summary>
    /// <remarks>
    /// <strong>Chỉ phân giải, không mở.</strong> Trước đây phần này từng gọi <c>ShellExecutor.Open</c>
    /// để mở Recycle Bin rồi đóng cửa sổ chênh lệch, khiến màn hình người dùng lóe lên một cửa sổ Explorer;
    /// còn mục tiêu <c>shell:</c> viết sai có nguy cơ bật hộp thoại lỗi. Kiểm tra phân giải (<c>SHParseDisplayName</c>)
    /// không mở cửa sổ và không bật hộp thoại, vẫn khẳng định cùng một điều: shell có chấp nhận mục tiêu này không.
    /// Mục "nhấn vào ô lối vào mở ra được thật" thuộc danh sách kiểm tra thủ công (bên cạnh mục 9 của phần sáu).
    /// </remarks>
    private static void CheckRecycleBinEntry(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var accepted = Platform.Shell.ShellTargets.CanResolve("shell:RecycleBinFolder");
        var rejected = Platform.Shell.ShellTargets.CanResolve("shell:GlassquidNoSuchFolder");

        if (accepted is null || rejected is null)
        {
            Skip(checks, report, "Giao diện phân giải shell không khả dụng, bỏ qua kiểm tra mục tiêu Recycle Bin");
            return;
        }

        Check(checks, "Shell chấp nhận shell:RecycleBinFolder (chuỗi lối vào Recycle Bin)", accepted == true,
            "SHParseDisplayName trả về thành công");
        Check(checks, "Shell từ chối mục tiêu shell: viết sai (kiểm tra tồn tại không bị nới lỏng)", rejected == false,
            "SHParseDisplayName trả về thất bại");

        report.AppendLine("Phần này chỉ kiểm tra phân giải, không mở cửa sổ, không bật hộp thoại; việc mở thực tế được bao phủ bởi đối chiếu thủ công.");
        report.AppendLine();
    }

    /// <summary>
    /// Tính đúng đắn theo quy mô (Phần 5 tài liệu khởi công): với thư viện 500 mục + hàng ghim siêu dài,
    /// bố cục, sắp xếp và hình học đều không bị biến dạng. Độ mượt thuộc nghiệm thu hiệu năng ở Giai đoạn 7,
    /// ở đây chỉ khóa tính đúng đắn.
    /// </summary>
    private static void CheckScale(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        // Trong thư viện đã có hơn chục mục thật/mẫu, bổ sung thêm lên 500+.
        var existing = host.Library.Items.Count;
        var seedTarget = 500;
        var added = 0;

        for (var index = existing; index < seedTarget; index++)
        {
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"Mục quy mô {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--scale {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (host.Library.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
        }

        host.Library.Save();

        Check(checks, "Bổ sung thư viện ứng dụng lên trên 500 mục", host.Library.Items.Count >= seedTarget,
            $"Tổng thư viện {host.Library.Items.Count} mục (lần này thêm {added})");

        // Ghim 200 mục (vượt xa lượng một hàng chứa nổi), bao phủ quy mô nội dung cần cuộn khi tràn.
        var layout = host.Layouts;
        var pinned = new List<string>(layout.Dock.PinnedItemIds);
        foreach (var entry in host.Library.Items.Take(200))
        {
            if (entry is not null && entry.IsUsable && !pinned.Contains(entry.Id))
            {
                pinned.Add(entry.Id);
            }
        }

        layout.Dock.PinnedItemIds = pinned;
        layout.SaveDock();

        var reloaded = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();

        Check(checks, "Thứ tự khớp khi tải lại sau khi lưu hàng ghim 200 mục",
            reloaded.Dock.PinnedItemIds.Count == pinned.Count
            && pinned.SequenceEqual(reloaded.Dock.PinnedItemIds),
            $"Đã ghim {pinned.Count} mục, tải lại {reloaded.Dock.PinnedItemIds.Count} mục");

        // Hình học của 200 ô: tổng chiều dài dương và kiểm tra chạm không vượt biên.
        var length = Glassquid.Dock.DockMagnification.RequiredLength(
            pinned.Count, 48, 8, 1.35);
        var geometry = Glassquid.Dock.DockMagnification.Compute(
            pinned.Count, 48, 8, 1.35, null);
        var hit = Glassquid.Dock.DockMagnification.HitTest(
            geometry, 48, geometry[199].Center);

        Check(checks, "Tính toán hình học cho hàng ghim siêu dài đúng với 200 mục",
            length > 0 && hit == 199,
            $"Tổng dài {length:F0} DIP, chỉ số trúng cuối cùng {hit}");

        // Tải lại thư viện (mô phỏng khởi động lại): số mục khớp nhau.
        var reloadedLibrary = new AppLibraryStore(host.Paths.AppLibraryFile);
        reloadedLibrary.Load();
        Check(checks, "Số mục thư viện ứng dụng khớp sau khi khởi động lại",
            reloadedLibrary.Items.Count == host.Library.Items.Count,
            $"Tải lại {reloadedLibrary.Items.Count} mục");

        report.AppendLine();
    }

    /// <summary>
    /// Tiêu chuẩn nghiệm thu của báo cáo phúc tra: sau khi kéo sắp xếp 5 ứng dụng —
    /// khởi động lại vẫn giữ đúng thứ tự, tệp bố cục vẫn chỉ có 5 ID ứng dụng (không có entry:* của lối vào tích hợp sẵn).
    /// </summary>
    /// <remarks>
    /// Nhân tiện xác minh việc dọn dẹp bố cục cũ: <strong>cố tình nhét</strong> vào bố cục 3 lối vào và 1 id lạ,
    /// sau khi làm sạch chỉ được còn lại các ứng dụng trong thư viện, và thứ tự ứng dụng ban đầu không đổi.
    /// </remarks>
    private static void CheckFiveAppReorder(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        const int AppCount = 5;
        var layout = host.Layouts;
        var dockFile = layout.DockFilePath;

        // Tạo 5 mục ứng dụng thật (các mục tổng hợp có sẵn trong thư viện là đủ, lấy 5 mục đầu).
        var apps = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable)
            .Take(AppCount)
            .Select(entry => entry.Id)
            .ToList();

        if (apps.Count < AppCount)
        {
            Skip(checks, report, "Thư viện không đủ 5 mục khả dụng, bỏ qua kiểm tra sắp xếp 5 ứng dụng");
            return;
        }

        // Sắp xếp đi cùng một đường với giao diện: máy trạng thái + kiểm tra hợp lệ DockLayoutPolicy rồi ghi đĩa.
        var saveCount = 0;

        var model = new Glassquid.Dock.DockReorderModel(order =>
        {
            var sanitized = Glassquid.Dock.DockLayoutPolicy.Sanitize(
                order, id => host.Library.Find(id) is not null);

            layout.Dock.PinnedItemIds = sanitized;
            layout.SaveDock();
            saveCount++;
        });

        model.Begin(apps);
        model.PreviewMove(apps[4], 0);   // Kéo mục cuối lên đầu
        model.PreviewMove(apps[0], 2);
        var outcome = model.Commit();

        var expected = new[] { apps[4], apps[1], apps[0], apps[2], apps[3] };

        Check(checks, "Xác nhận kéo thả sắp xếp 5 ứng dụng thành công", outcome == Glassquid.Dock.ReorderOutcome.Committed && saveCount == 1,
            $"Kết quả {outcome}, số lần lưu {saveCount}");

        // "Khởi động lại" = tải lại tệp bố cục từ góc nhìn một tiến trình khác.
        var reloaded = new LayoutStore(
            dockFile,
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();
        var persisted = reloaded.Dock.PinnedItemIds;

        Check(checks, "Thứ tự 5 ứng dụng đúng sau khi khởi động lại (D-02)",
            persisted.Count == AppCount && persisted.SequenceEqual(expected),
            $"Thứ tự đã ghi đĩa: {string.Join(',', persisted)}");

        Check(checks, "Tệp bố cục chỉ có 5 ID ứng dụng, không có lối vào tích hợp sẵn (tiêu chuẩn nghiệm thu của Lỗi 1)",
            persisted.Count == AppCount && !persisted.Any(Glassquid.Dock.DockLayoutPolicy.IsEntryTile),
            $"Tổng {persisted.Count} mục, trong đó entry:* có {persisted.Count(Glassquid.Dock.DockLayoutPolicy.IsEntryTile)} mục");

        // Dọn dẹp bố cục cũ: cố tình ghi vào 3 lối vào + 1 id lạ, chạy lại bước làm sạch.
        var polluted = new List<string>(expected);
        polluted.Insert(1, "entry:launcher");
        polluted.Add("entry:showdesktop");
        polluted.Add("entry:recyclebin");
        polluted.Add("id-muc-da-xoa-tu-lau");
        layout.Dock.PinnedItemIds = polluted;
        layout.SaveDock();

        var cleaned = Glassquid.Dock.DockLayoutPolicy.Sanitize(
            layout.Dock.PinnedItemIds, id => host.Library.Find(id) is not null);

        var cleanedReload = new LayoutStore(
            dockFile,
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        layout.Dock.PinnedItemIds = cleaned;
        layout.SaveDock();
        _ = cleanedReload.LoadDock();

        Check(checks, "3 lối vào và id lạ trong bố cục cũ đã bị xóa sạch, thứ tự ứng dụng giữ nguyên",
            cleanedReload.Dock.PinnedItemIds.SequenceEqual(expected),
            $"Sau khi dọn dẹp: {string.Join(',', cleanedReload.Dock.PinnedItemIds)}");

        report.AppendLine(CultureInfo.InvariantCulture, $"Tệp bố cục: {dockFile}");
        report.AppendLine();
    }
    // ---- Các phần của Đợt 1 ----

    /// <summary>
    /// Ghi ra lối tắt mẫu có danh tính tường minh rồi nhập vào, khẳng định danh tính đi theo vào mục.
    /// </summary>
    private static void CheckImportedIdentity(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var samples = Path.Combine(paths.RootDirectory, "Mẫu danh tính");
        _ = Directory.CreateDirectory(samples);

        var withIdentityPath = Path.Combine(samples, "Notepad có danh tính.lnk");
        var withIdentity = ShortcutWriter.Write(withIdentityPath, new ShortcutDefinition(
            TargetPath: @"C:\Windows\System32\notepad.exe",
            Description: "Mẫu nghiệm thu Giai đoạn 4",
            Aumid: NotepadAumid));

        Check(checks, "Ghi tệp lối tắt mẫu có danh tính thành công", withIdentity, withIdentityPath);

        if (!withIdentity)
        {
            return;
        }

        var resolved = ShortcutResolver.Resolve(withIdentityPath);
        Check(checks, "Đọc lại được danh tính tường minh trên mẫu", resolved?.Aumid == NotepadAumid,
            $"Đọc lại: {resolved?.Aumid ?? "null"}");

        var importResult = host.Import.ImportItems([new ImportRequest { Source = withIdentityPath }]);
        Check(checks, "Nhập mẫu có danh tính thành công", importResult.Added.Count == 1,
            $"Thêm mới {importResult.Added.Count}, thất bại {importResult.Failures.Count}");

        var entry = importResult.Added.Count > 0 ? importResult.Added[0] : null;
        Check(checks, "Danh tính trên mục khớp với mẫu", entry?.AppUserModelId == NotepadAumid,
            $"Danh tính mục: {entry?.AppUserModelId ?? "null"}");

        Check(checks, "Phân giải trong phiên không cần đọc lại tệp vẫn trả về cùng danh tính",
            WindowMatcher.ResolveAumid(entry!) == NotepadAumid,
            WindowMatcher.ResolveAumid(entry!) ?? "null");

        // Nhóm đối chứng: lối tắt thông thường không có danh tính, danh tính giữ null —
        // khớp danh tính là phần tăng cường chứ không phải điều kiện tiên quyết, exe thông thường đi theo đường dẫn ảnh.
        var plainPath = Path.Combine(samples, "Lối tắt thông thường.lnk");
        var plainWritten = ShortcutWriter.Write(plainPath, new ShortcutDefinition(
            TargetPath: @"C:\Windows\System32\charmap.exe",
            Description: "Mẫu đối chứng nghiệm thu Giai đoạn 4"));

        if (plainWritten)
        {
            var plainImport = host.Import.ImportItems([new ImportRequest { Source = plainPath }]);
            var plainEntry = plainImport.Added[0];
            Check(checks, "Lối tắt không có danh tính sau khi nhập có danh tính là null",
                plainEntry is not null && plainEntry.AppUserModelId is null,
                $"Danh tính mục: {plainEntry?.AppUserModelId ?? "null"}");
        }
        else
        {
            Check(checks, "Lối tắt không có danh tính sau khi nhập có danh tính là null", false, "Ghi mẫu đối chứng thất bại");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục mẫu: {samples}");
        report.AppendLine();
    }

    /// <summary>
    /// Khẳng định quy tắc khớp trên ảnh chụp giả lập. Căn cứ thực đo của quy tắc xem tại bản lưu đầu ra thăm dò.
    /// </summary>
    private static void CheckMatchingRules(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        static WindowIdentityInfo Window(
            nint handle,
            string? imagePath,
            string? windowAumid,
            string? processAumid)
            => new(handle, $"Cửa sổ {handle}", 1000, false, imagePath, windowAumid, processAumid);

        // Chương trình cổ điển: mục không có danh tính, đích là exe -> khớp theo đường dẫn ảnh.
        var classic = WindowMatcher.MatchWindows(
            new AppEntry { Id = "c", Kind = AppItemKind.Program, Target = ClassicTarget },
            [Window(1, @"C:\WINDOWS\SYSTEM32\CHARMAP.EXE", null, null)],
            aumid: null);
        Check(checks, "Chương trình cổ điển khớp theo đường dẫn ảnh (không phân biệt hoa thường)",
            classic.Count == 1 && classic[0].Handle == 1, $"Khớp {classic.Count} cửa sổ");

        // Ứng dụng đóng gói: danh tính mục == danh tính gói của tiến trình sở hữu.
        var packaged = WindowMatcher.MatchWindows(
            new AppEntry { Id = "p", Kind = AppItemKind.Program, Target = @"C:\Windows\System32\notepad.exe", AppUserModelId = NotepadAumid },
            [Window(2, @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_...\Notepad.exe", null, NotepadAumid)],
            NotepadAumid);
        Check(checks, "Ứng dụng đóng gói khớp theo danh tính gói của tiến trình",
            packaged.Count == 1 && packaged[0].Handle == 2, $"Khớp {packaged.Count} cửa sổ");

        // UWP: danh tính mục == danh tính được đặt tường minh trên cửa sổ khung.
        const string uwpIdentity = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        var uwp = WindowMatcher.MatchWindows(
            new AppEntry { Id = "u", Kind = AppItemKind.Shortcut, Target = @"C:\Tam\CaiDat.lnk", AppUserModelId = uwpIdentity },
            [Window(3, @"C:\Windows\System32\ApplicationFrameHost.exe", uwpIdentity, null)],
            uwpIdentity);
        Check(checks, "Cửa sổ khung UWP khớp theo danh tính tường minh của cửa sổ",
            uwp.Count == 1 && uwp[0].Handle == 3, $"Khớp {uwp.Count} cửa sổ");

        // Danh tính không khớp: dù đường dẫn tiến trình chủ giống nhau cũng không tính (hai UWP dùng chung một tiến trình chủ).
        var conflict = WindowMatcher.MatchWindows(
            new AppEntry { Id = "x", Kind = AppItemKind.Shortcut, Target = @"C:\Tam\UngDungA.lnk", AppUserModelId = "com.app.a" },
            [Window(4, @"C:\Windows\System32\ApplicationFrameHost.exe", "com.app.b", null)],
            "com.app.a");
        Check(checks, "Cửa sổ có danh tính không khớp thì không khớp, cũng không lùi về đường dẫn tiến trình chủ",
            conflict.Count == 0, $"Khớp {conflict.Count} cửa sổ");

        // Mục tiêu kịch bản không khớp theo ảnh (tiến trình thật là cmd.exe).
        var script = WindowMatcher.MatchWindows(
            new AppEntry { Id = "s", Kind = AppItemKind.Program, Target = @"C:\tools\run.bat" },
            [Window(5, @"C:\tools\run.bat", null, null)],
            aumid: null);
        Check(checks, "Mục tiêu kịch bản không khớp theo ảnh (lùi về khởi chạy thẳng)",
            script.Count == 0, $"Khớp {script.Count} cửa sổ");

        report.AppendLine("Căn cứ quy tắc: đo thực tế thăm dò A Giai đoạn 4 (docs/evidence/Giai-doan-4-Ket-qua-do-tham-do.txt).");
        report.AppendLine();
    }

    /// <summary>
    /// Theo dõi trạng thái đang chạy: lần liệt kê ban đầu phải thấy các cửa sổ đã có; cả lúc xuất hiện và biến mất của tiến trình mục tiêu tự khởi chạy
    /// đều phải được đối soát ra. Trả về "việc khởi chạy mục tiêu có được thực thi hay không".
    /// </summary>
    private static bool CheckRunningTracker(
        AppServiceHost host,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var initial = host.Running.LastWindows.Count;
        Check(checks, "Đã hoàn tất liệt kê ban đầu khi khởi động (thấy các cửa sổ có sẵn trước khi chạy)", initial > 0,
            $"Số cửa sổ ban đầu: {initial}");

        if (Platform.Shell.WindowLocator.FindWindowsForImage(ClassicTarget).Count > 0)
        {
            report.AppendLine("Trên màn hình người dùng đã có cửa sổ Character Map; bỏ qua kiểm tra đối soát xuất hiện/biến mất và đánh dấu là Chưa xác minh.");
            report.AppendLine();
            Skip(checks, report, "Bỏ qua kiểm tra đối soát tập đang chạy (trên màn hình người dùng đã có cửa sổ mục tiêu)");
            return false;
        }

        var importResult = host.Import.ImportItems([new ImportRequest { Source = ClassicTarget }]);
        var entry = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0]
            : null;

        if (entry is null)
        {
            Check(checks, "Nhập mục tiêu thành công", false, "Nhập thất bại");
            return false;
        }

        Check(checks, "Mục tiêu chưa nằm trong tập đang chạy trước khi khởi chạy", !host.Running.IsRunning(entry), "Chưa chạy");

        var outcome = ShellExecutor.Open(ClassicTarget, null, null);
        if (!outcome.Started || outcome.ProcessId == 0)
        {
            Check(checks, "Khởi chạy mục tiêu thành công", false, $"Mã lỗi shell {outcome.ErrorCode}");
            return true;
        }

        try
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"ID tiến trình charmap mục tiêu: {outcome.ProcessId}");

            // Tạo cửa sổ là sự kiện thật bên ngoài, chờ theo chu kỳ là cách phù hợp ở đây.
            var appeared = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                if (host.Running.Reconcile() > 0 && host.Running.IsRunning(entry))
                {
                    appeared = true;
                    break;
                }

                Thread.Sleep(300);
            }

            Check(checks, "Cửa sổ mục tiêu sau khi xuất hiện sẽ vào tập đang chạy (tầng dịch vụ của D-07)", appeared,
                appeared ? "Sau đối soát IsRunning = true" : "Không quan sát thấy cửa sổ trong 10 giây");

            var matching = host.Running.FindWindows(entry);
            Check(checks, "Cửa sổ khớp được đúng là của mục tiêu",
                matching.Count == 0 || matching.All(w => w.ImagePath != null
                    && w.ImagePath.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase)),
                $"Khớp {matching.Count} cửa sổ");
        }
        finally
        {
            // Thu dọn chỉ theo pid đã ghi.
            try
            {
                using var process = Process.GetProcessById((int)outcome.ProcessId);
                if (!process.HasExited)
                {
                    process.Kill();
                    _ = process.WaitForExit(3000);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Tiến trình mục tiêu đã tự thoát.
            }
        }

        var gone = false;
        var goneDeadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < goneDeadline)
        {
            Thread.Sleep(300);

            _ = host.Running.Reconcile();

            if (!host.Running.IsRunning(entry))
            {
                gone = true;
                break;
            }
        }

        Check(checks, "Sau khi tiến trình mục tiêu kết thúc thì rời khỏi tập đang chạy (đối soát bao phủ \"biến mất\")", gone,
            gone ? "Sau đối soát IsRunning = false" : "Sau 10 giây vẫn hiển thị đang chạy");
        report.AppendLine();
        return true;
    }

    /// <summary>
    /// Khẳng định tích hợp tầng dịch vụ của D-09: nhấn lần đầu để khởi chạy, nhấn lần hai để kích hoạt.
    /// </summary>
    private static void CheckActivateOrLaunch(
        AppServiceHost host,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var importResult = host.Import.ImportItems([new ImportRequest { Source = ClassicTarget }]);
        var entry = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0]
            : null;

        if (entry is null)
        {
            Check(checks, "Mục tiêu cho kiểm tra kích hoạt tồn tại", false, "Nhập thất bại");
            return;
        }

        var first = host.Launch.ActivateOrLaunch(entry.Id);
        var spawnedProcessId = host.Launch.LastLaunchedProcessId;
        report.AppendLine(CultureInfo.InvariantCulture, $"Lần đầu ActivateOrLaunch -> {first.Outcome}");

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var windowSeen = false;

            while (DateTime.UtcNow < deadline)
            {
                if (WindowLocator.FindWindowsForImage(ClassicTarget).Count > 0)
                {
                    windowSeen = true;
                    break;
                }

                Thread.Sleep(300);
            }

            Check(checks, "Yêu cầu lần đầu đã khởi chạy ứng dụng (Launched)", first.Outcome == ActivationOutcome.Launched, first.Outcome.ToString());
            Check(checks, "Cửa sổ sau đó xuất hiện thực sự", windowSeen, windowSeen ? "Đã xuất hiện" : "Không xuất hiện trong 10 giây");

            var second = host.Launch.ActivateOrLaunch(entry.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"Lần hai ActivateOrLaunch -> {second.Outcome}");
            Check(checks, "Yêu cầu lần hai kích hoạt cửa sổ đã có thay vì mở thêm một cửa sổ mới (D-09)",
                second.Outcome is ActivationOutcome.Activated or ActivationOutcome.MultipleWindowsFound,
                second.Outcome.ToString());
        }
        finally
        {
            Phase3Acceptance.KillRecordedProcess(spawnedProcessId);
        }

        report.AppendLine();
    }

    private static void Skip(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report,
        string reason)
    {
        // Bỏ qua là một loại kết luận độc lập, **không tính vào đạt**: nó cho biết máy này thiếu điều kiện đó,
        // chứ không phải đã xác minh được gì. Được liệt kê là Chưa xác minh trong báo cáo bàn giao.
        checks.Add((reason, CheckStatus.Skipped, reason));
        report.AppendLine(CultureInfo.InvariantCulture, $"Bỏ qua (Chưa xác minh): {reason}");
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', 46));
    }

    private static void Check(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        string label,
        bool passed,
        string detail)
        => checks.Add((label, passed ? CheckStatus.Passed : CheckStatus.Failed, detail));
}
