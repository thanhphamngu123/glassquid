using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Imaging;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.App.Startup;

/// <summary>
/// Chạy không giao diện một lệnh thư viện ứng dụng và xuất báo cáo.
/// </summary>
/// <remarks>
/// Cùng mục đích với <see cref="DiagnosticRunner"/>: giúp sản phẩm bàn giao có thể được xác minh bằng script,
/// thay vì phải nhờ người bấm giao diện. Sản phẩm bàn giao của giai đoạn 3 là "thư viện ứng dụng dùng chung ổn định cùng quy trình nhập và khởi chạy",
/// mọi khâu của quy trình này — phân giải lối tắt, lấy biểu tượng, khử trùng lặp, ghi xuống đĩa, tải lại, định vị lại —
/// đều được chạy thực tế một lượt tại đây.
///
/// <para>
/// Mọi lệnh đều dùng chung một thực thể thư viện ứng dụng trong <see cref="AppServiceHost"/>,
/// do đó "nhập xong liệt kê ngay" sẽ thấy đúng nội dung vừa ghi vào.
/// </para>
/// </remarks>
internal static class AppServiceRunner
{
    public static int Run(AppPaths paths, CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance
            or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance && options.Unknown.Count > 0)
            return AcceptanceArgumentsError("Lệnh nghiệm thu phải chạy độc lập và đầy đủ tham số");

        // Lệnh nghiệm thu phải lấy handle dữ liệu và đầu ra trước, rồi mới tạo thư mục, tải/khôi phục dữ liệu, ghi nhật ký hoặc chạy giao diện.
        switch (options.Verb)
        {
            case AppServiceVerb.Phase3Acceptance:
                if (options.VerbArguments.Count != 3) return AcceptanceArgumentsError("--phase3-acceptance <đầu ra hình ảnh> <đầu ra báo cáo> <thư mục dữ liệu mới>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[2],
                    [new("image", options.VerbArguments[0]), new("report", options.VerbArguments[1])],
                    workspace => Phase3Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase4Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase4-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase4Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase5Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase5-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase5Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase6Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase6-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase6Acceptance.Run(paths, options.VerbArguments, workspace));
        }

        var report = new StringBuilder();
        var exitCode = 0;

        // Đầu dò không cần thư viện ứng dụng và bộ nhớ đệm biểu tượng, nên được xử lý trước kho dữ liệu —
        // chúng chỉ liệt kê chỉ đọc trên màn hình nền, kèm theo mục tiêu tự khởi chạy, tự ghi chép, tự thu dọn.
        switch (options.Verb)
        {
            case AppServiceVerb.ProbeAumid:
                return Phase4Probe.RunAumid(paths);
            case AppServiceVerb.ProbeRunEvents:
                return Phase4Probe.RunEvents(paths);
            case AppServiceVerb.ProbeDragDrop:
                return DragDropProbe.Run(paths);
            case AppServiceVerb.ProbeShowDesktop:
                return ShowDesktopProbe.Run(paths);
        }

        report.AppendLine("Lệnh thư viện ứng dụng Glassquid");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Lệnh: {DescribeVerb(options.Verb)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu: {paths.RootDirectory}");
        report.AppendLine();

        try
        {
            var data = new GlassquidDataStore(paths);
            var loadReport = data.LoadAll();

            foreach (var notice in loadReport.Notices)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Thông báo tải dữ liệu: {notice}");
            }

            using var host = new AppServiceHost(data);

            exitCode = options.Verb switch
            {
                AppServiceVerb.Import => RunImport(host, options, report),
                AppServiceVerb.ScanStartMenu => RunScan(options, report),
                AppServiceVerb.ImportStartMenu => RunImportStartMenu(host, options, report),
                AppServiceVerb.ListLibrary => RunList(host, options, report),
                AppServiceVerb.CheckLibrary => RunCheck(host, report),
                AppServiceVerb.Relocate => RunRelocate(host, options, report),
                AppServiceVerb.Launch => RunLaunch(host, options, report, activate: false),
                AppServiceVerb.Activate => RunLaunch(host, options, report, activate: true),
                AppServiceVerb.VerifyIcons => RunVerifyIcons(host, report),
                AppServiceVerb.IconContactSheet => RunContactSheet(host, options, report),
                AppServiceVerb.ClearIconCache => RunClearIcons(host, report),
            AppServiceVerb.MakeSamples => RunMakeSamples(options, report),
            AppServiceVerb.SeedLibrary => RunSeed(host, options, report),
            _ => 0,
            };

            if (options.Verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance)
            {
                // Quá trình nghiệm thu sẽ tự ghi một báo cáo đầy đủ, không lặp lại phần vỏ bọc ở đây nữa.
                return exitCode;
            }
        }
        catch (StorageException ex)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ Không thể ghi dữ liệu: {ex.Message}");
            exitCode = 1;
        }

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "appservices.log"));
        return exitCode;
    }

    private static int AcceptanceArgumentsError(string usage)
    {
        Console.Error.WriteLine("Tham số nghiệm thu không đủ hoặc xung đột. Cách dùng: " + usage);
        return 2;
    }

    // ---- Các lệnh ----

    private static int RunImport(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        if (options.VerbArguments.Count == 0)
        {
            report.AppendLine("Chưa cung cấp nguồn cần nhập.");
            return 1;
        }

        var requests = options.VerbArguments.Select(CreateRequest).ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"Có {requests.Count} nguồn cần nhập:");

        foreach (var request in requests)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · {(request.IsUrl ? "URL" : "Đường dẫn")} {request.Source}");
        }

        report.AppendLine();

        var result = host.Import.ImportItems(requests);
        DescribeImport(result, report);
        DescribeIconWork(host, report);
        DescribeLibrarySize(host, report);

        return result.Failures.Count > 0 && result.Added.Count == 0 ? 1 : 0;
    }

    private static int RunScan(CommandLineOptions options, StringBuilder report)
    {
        var scan = AppServiceHost.ScanStartMenu();

        report.AppendLine("Quét menu Start");
        report.AppendLine(CultureInfo.InvariantCulture, $"Số thư mục gốc được quét: {scan.Roots.Count}:");

        foreach (var root in scan.Roots)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · {root}");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Số tệp .lnk tìm thấy: {scan.ScannedFileCount}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Sau khi khử trùng lặp: {scan.Shortcuts.Count} (nhường chỗ do trùng tên: {scan.DuplicateNameCount})");

        var limit = ReadIntOption(options.VerbArguments, 0, 40);

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Sắp xếp theo nhóm và tên, liệt kê {Math.Min(limit, scan.Shortcuts.Count)} mục đầu:");

        foreach (var shortcut in scan.Shortcuts.Take(limit))
        {
            var group = shortcut.Group.Length == 0 ? "(Gốc)" : shortcut.Group;
            var owner = shortcut.FromCurrentUser ? "Người dùng hiện tại" : "Tất cả người dùng";
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{owner}] {group} / {shortcut.Name}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {shortcut.Path}");
        }

        return 0;
    }

    private static int RunImportStartMenu(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var scan = AppServiceHost.ScanStartMenu();

        report.AppendLine(CultureInfo.InvariantCulture, $"Tìm thấy {scan.Shortcuts.Count} mục trong menu Start (ban đầu {scan.ScannedFileCount}, nhường chỗ do trùng tên {scan.DuplicateNameCount}).");

        var limit = ReadIntOption(options.VerbArguments, 0, int.MaxValue);
        var selected = scan.Shortcuts.Take(limit).ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"Lần này nhập {selected.Count} mục trong số đó.");
        report.AppendLine();

        var result = host.ImportStartMenuShortcuts(selected);
        DescribeImport(result, report);
        DescribeIconWork(host, report);
        DescribeLibrarySize(host, report);

        return 0;
    }

    private static int RunList(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var store = host.Library;

        var usable = store.Items.Where(entry => entry is not null && entry.IsUsable).ToList();
        var anomalous = store.Items.Count - usable.Count;

        report.AppendLine(CultureInfo.InvariantCulture, $"Thư viện ứng dụng có tổng cộng {usable.Count} bản ghi khả dụng.");

        if (anomalous > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Ngoài ra có {anomalous} mục không thể sử dụng (mục trống hoặc thiếu trường), được giữ nguyên trong tệp.");
        }

        var missing = usable.Count(entry => entry.IsTargetMissing);
        report.AppendLine(CultureInfo.InvariantCulture, $"Trong đó số mục được đánh dấu mất hiệu lực: {missing}");

        var withArguments = usable.Count(entry => !string.IsNullOrEmpty(entry.Arguments));
        report.AppendLine(CultureInfo.InvariantCulture, $"Có tham số khởi chạy: {withArguments}");

        var withCustomIcon = usable.Count(entry => !string.IsNullOrEmpty(entry.CustomIconPath));
        report.AppendLine(CultureInfo.InvariantCulture, $"Có biểu tượng tùy chỉnh: {withCustomIcon}");
        report.AppendLine();

        var limit = ReadIntOption(options.VerbArguments, 0, 20);
        report.AppendLine(CultureInfo.InvariantCulture, $"Chi tiết (theo thứ tự trong thư viện, tối đa {limit} mục):");

        var index = 0;

        foreach (var entry in usable)
        {
            index++;
            var flag = entry.IsTargetMissing ? "Mất hiệu lực" : "Bình thường";

            report.AppendLine(CultureInfo.InvariantCulture, $"  {index,3}. [{DescribeKind(entry.Kind)}/{flag}] {entry.DisplayName}");
            report.AppendLine(CultureInfo.InvariantCulture, $"       id       {entry.Id}");
            report.AppendLine(CultureInfo.InvariantCulture, $"       Đích     {entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       Tham số  {entry.Arguments}");
            }

            if (!string.IsNullOrEmpty(entry.WorkingDirectory))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       Thư mục làm việc {entry.WorkingDirectory}");
            }

            if (!string.IsNullOrEmpty(entry.CustomIconPath))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       Biểu tượng tùy chỉnh {entry.CustomIconPath}");
            }

            if (limit > 0 && index >= limit && index < usable.Count)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  …… (còn lại {usable.Count - index} mục chưa liệt kê)");
                break;
            }
        }

        return 0;
    }

    private static int RunCheck(AppServiceHost host, StringBuilder report)
    {
        var result = host.Maintenance.RefreshMissingTargets();

        report.AppendLine("Kiểm tra lại mục tiêu mất hiệu lực");
        report.AppendLine(CultureInfo.InvariantCulture, $"Mục đã kiểm tra: {result.Checked}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Không tìm thấy mục tiêu: {result.Missing}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Trạng thái thay đổi: {(result.Changed ? "Có, đã lưu" : "Không, chưa ghi tệp")}");
        report.AppendLine();

        if (result.NewlyBroken.Count > 0)
        {
            report.AppendLine("Mới phát hiện mất hiệu lực lần này (giữ nguyên mục, chỉ đánh dấu):");

            foreach (var entry in result.NewlyBroken)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · {entry.DisplayName}");
                report.AppendLine(CultureInfo.InvariantCulture, $"      {entry.Target}");
            }

            report.AppendLine();
        }

        if (result.Recovered.Count > 0)
        {
            report.AppendLine("Mục tiêu đã có lại, đã bỏ đánh dấu mất hiệu lực:");

            foreach (var entry in result.Recovered)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · {entry.DisplayName}");
            }

            report.AppendLine();
        }

        if (!result.Changed && result.Missing == 0)
        {
            report.AppendLine("Mục tiêu của toàn bộ các mục đều tồn tại.");
        }

        return 0;
    }

    private static int RunRelocate(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        if (options.VerbArguments.Count < 2)
        {
            report.AppendLine("Cách dùng: --relocate <id mục> <đường dẫn mới>");
            return 1;
        }

        var outcome = host.Maintenance.Relocate(options.VerbArguments[0], options.VerbArguments[1]);

        report.AppendLine(CultureInfo.InvariantCulture, $"Kết quả: {outcome.Result}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Mô tả: {outcome.Message}");

        if (outcome.Result == RelocateResult.Relocated)
        {
            var entry = host.Library.Find(options.VerbArguments[0]);

            if (entry is not null)
            {
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture, $"  Tên              {entry.DisplayName}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  Mục tiêu mới     {entry.Target}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  Loại             {DescribeKind(entry.Kind)}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  Đánh dấu hỏng    {entry.IsTargetMissing}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  Nguồn biểu tượng {entry.IconSource}");
            }
        }

        return outcome.Result == RelocateResult.Relocated ? 0 : 1;
    }

    private static int RunLaunch(AppServiceHost host, CommandLineOptions options, StringBuilder report, bool activate)
    {
        if (options.VerbArguments.Count == 0)
        {
            report.AppendLine(activate ? "Cách dùng: --activate <id mục>" : "Cách dùng: --launch <id mục>");
            return 1;
        }

        var itemId = options.VerbArguments[0];
        var entry = host.Library.Find(itemId);

        if (entry is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Mục: {entry.DisplayName} ({DescribeKind(entry.Kind)})");
            report.AppendLine(CultureInfo.InvariantCulture, $"Mục tiêu: {entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Tham số: {entry.Arguments}");
            }

            report.AppendLine();
        }

        if (activate)
        {
            var outcome = host.Launch.ActivateOrLaunch(itemId);

            report.AppendLine(CultureInfo.InvariantCulture, $"Kết quả: {DescribeActivation(outcome.Outcome)}");

            foreach (var candidate in outcome.Candidates)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · Cửa sổ ứng viên 0x{candidate.Handle:X} {candidate.Title}");
            }

            if (!string.IsNullOrEmpty(outcome.Message))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Mô tả: {outcome.Message}");
            }

            return outcome.Outcome is ActivationOutcome.Activated or ActivationOutcome.Launched or ActivationOutcome.MultipleWindowsFound
                ? 0
                : 1;
        }

        var result = host.Launch.LaunchItem(itemId);

        report.AppendLine(CultureInfo.InvariantCulture, $"Kết quả: {(result.Succeeded ? "Đã giao cho shell mở" : "Không thể mở")}");

        if (result.Failure is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Phân loại lỗi: {result.Failure}");
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Mô tả: {result.Message}");
        }

        if (result.Succeeded)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Id tiến trình được khởi chạy: {host.Launch.LastLaunchedProcessId}");
        }

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Đọc qua từng tệp PNG trong bộ nhớ đệm bằng bộ giải mã ảnh của hệ thống (WIC).
    /// </summary>
    /// <remarks>
    /// Bước này là bắt buộc: PNG do chúng ta tự ghi theo từng byte, nên chỉ kiểm tra
    /// "tệp tồn tại và độ dài khác 0" chẳng khác nào không kiểm tra. Giải mã bằng WIC là dùng một
    /// bộ giải mã <strong>độc lập với phần cài đặt của chúng ta</strong> để xác nhận đó thực sự là PNG hợp lệ,
    /// đồng thời xác nhận kích thước và pixel thực sự nằm bên trong.
    /// </remarks>
    private static int RunVerifyIcons(AppServiceHost host, StringBuilder report)
    {
        var directory = host.Paths.IconCacheDirectory;

        if (!Directory.Exists(directory))
        {
            report.AppendLine("Thư mục bộ nhớ đệm biểu tượng không tồn tại.");
            return 0;
        }

        var files = Directory.GetFiles(directory, "*.png");

        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục bộ nhớ đệm: {directory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Tệp PNG: {files.Length}");
        report.AppendLine();

        var failures = 0;
        var totalPixels = 0L;
        long smallest = long.MaxValue;
        long largest = 0;

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);

                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                var visible = CountVisiblePixels(frame);
                var pixels = (long)frame.PixelWidth * frame.PixelHeight;

                totalPixels += pixels;
                smallest = Math.Min(smallest, pixels);
                largest = Math.Max(largest, pixels);

                if (visible == 0)
                {
                    failures++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} toàn bộ là pixel trong suốt");
                }
                else
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✓ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} pixel hiển thị {visible} {frame.Format}");
                }
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException or IOException)
            {
                failures++;
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} giải mã thất bại: {ex.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Tổng số pixel: {totalPixels}");

        if (files.Length > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Nhỏ nhất {smallest} pixel mỗi ảnh, lớn nhất {largest} pixel");
        }

        var verdict = failures == 0
            ? "Toàn bộ biểu tượng đều được bộ giải mã hệ thống đọc thành công và có nội dung hiển thị."
            : $"Có {failures} tệp không sử dụng được.";

        report.AppendLine(verdict);

        return failures == 0 ? 0 : 1;
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

    /// <summary>Ghép các biểu tượng trong bộ nhớ đệm thành một bảng tổng hợp (contact sheet) để kiểm tra thủ công rằng biểu tượng đã được lấy đúng.</summary>
    private static int RunContactSheet(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var entries = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable && !string.IsNullOrWhiteSpace(entry.IconSource))
            .Take(ReadIntOption(options.VerbArguments, 0, 60))
            .ToList();

        if (entries.Count == 0)
        {
            report.AppendLine("Không có bản ghi nào có nguồn biểu tượng trong thư viện ứng dụng.");
            return 1;
        }

        const int Cell = 64;
        const int Columns = 10;

        var rows = (entries.Count + Columns - 1) / Columns;
        var stride = Columns * Cell * 4;
        var canvas = new byte[stride * rows * Cell];

        var drawn = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var iconPath = host.Icons.EnsureFor(entry.IconSource)?.Path;

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
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

                // Thu phóng theo láng giềng gần nhất vào từng ô: ở đây chỉ để đối chiếu nội dung biểu tượng, không nội suy.
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
                // Bỏ qua nếu ảnh này không vẽ được, toàn bộ bảng tổng hợp vẫn hữu ích.
            }
        }

        var output = options.VerbArguments.Count > 1
            ? options.VerbArguments[1]
            : Path.Combine(Path.GetTempPath(), "glassquid-icon-contact-sheet.png");

        var sheet = PngWriter.EncodeBgra(canvas, Columns * Cell, rows * Cell);
        File.WriteAllBytes(output, sheet);

        report.AppendLine(CultureInfo.InvariantCulture, $"Bản ghi có nguồn biểu tượng trong thư viện: {entries.Count}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Số lượng thực tế đã vẽ: {drawn}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Bảng tổng hợp: {output} ({Columns * Cell}×{rows * Cell}, {sheet.Length} byte)");

        return drawn > 0 ? 0 : 1;
    }

    private static int RunClearIcons(AppServiceHost host, StringBuilder report)
    {
        var result = host.Icons.Clear();

        report.AppendLine(CultureInfo.InvariantCulture, $"Đã xóa {result.Removed} tệp biểu tượng bộ nhớ đệm có thể tạo lại.");
        report.AppendLine("Không xóa biểu tượng tùy chỉnh (custom-*): chúng không phải bộ nhớ đệm mà được sao chép từ tệp người dùng chọn, xóa đi sẽ không khôi phục lại được.");

        if (result.LeftOthers)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Ngoài ra có {result.LeftAlone} tệp không thuộc bộ nhớ đệm đã được giữ nguyên.");
        }

        report.AppendLine("Nguồn biểu tượng trên các mục vẫn giữ nguyên, sẽ được trích xuất lại ở lần nhập hoặc định vị lại tiếp theo.");
        return 0;
    }

    private static int RunMakeSamples(CommandLineOptions options, StringBuilder report)
    {
        var root = options.VerbArguments.Count > 0 ? options.VerbArguments[0] : null;
        var set = SampleDataBuilder.Build(root);

        report.Append(SampleDataBuilder.Describe(set));
        report.AppendLine();
        report.AppendLine("Bước tiếp theo: dùng --import để nhập các nguồn \"nên nhập thành công\" ở trên,");
        report.AppendLine(CultureInfo.InvariantCulture, $"sau đó xóa {set.BrokenTarget}");
        report.AppendLine("rồi dùng --check-library để quan sát đánh dấu mất hiệu lực, và dùng --relocate để định vị lại.");

        return 0;
    }

    private static int RunSeed(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var count = ReadIntOption(options.VerbArguments, 0, 500);
        var store = host.Library;

        var added = 0;
        var skipped = 0;

        for (var index = 0; index < count; index++)
        {
            // Cố tình dùng tệp hệ thống có thật làm mục tiêu để các mục tổng hợp không bị coi là mất hiệu lực,
            // cũng không bị đánh dấu khi kiểm tra lại. Thứ cần kiểm thử là "thư viện lớn lên vẫn hoạt động bình thường",
            // chứ không phải "việc phán đoán mất hiệu lực có đúng không".
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"Mục tổng hợp {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--seed {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (store.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
            else
            {
                skipped++;
            }
        }

        store.Save();

        report.AppendLine(CultureInfo.InvariantCulture, $"Yêu cầu tạo {count} mục, thực tế thêm mới {added} mục, bỏ qua {skipped} mục do trùng lặp.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư viện ứng dụng hiện có tổng cộng {store.Items.Count} mục.");
        return 0;
    }

    // ---- Tiện ích ----

    /// <summary>Đóng gói một nguồn truyền từ dòng lệnh thành yêu cầu nhập: nếu phân tích được thành http/https thì xử lý như URL.</summary>
    private static ImportRequest CreateRequest(string raw)
    {
        var isUrl = Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return new ImportRequest { Source = raw, IsUrl = isUrl };
    }

    private static void DescribeImport(ImportResult result, StringBuilder report)
    {
        report.AppendLine("Kết quả nhập");
        report.AppendLine(CultureInfo.InvariantCulture, $"  Thêm mới {result.Added.Count} mục");
        report.AppendLine(CultureInfo.InvariantCulture, $"  Trùng lặp {result.Duplicates.Count} mục");
        report.AppendLine(CultureInfo.InvariantCulture, $"  Thất bại {result.Failures.Count} mục");
        report.AppendLine();

        foreach (var entry in result.Added)
        {
            var missing = entry.IsTargetMissing ? " (tạm thời không tìm thấy mục tiêu, đã đánh dấu mất hiệu lực)" : string.Empty;

            report.AppendLine(CultureInfo.InvariantCulture, $"  + [{DescribeKind(entry.Kind)}] {entry.DisplayName}{missing}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      Mục tiêu {entry.Target}");
        }

        foreach (var entry in result.Duplicates)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  = Đã tồn tại: [{DescribeKind(entry.Kind)}] {entry.DisplayName} → {entry.Target}");
        }

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}: {failure.Message}");
        }
    }

    private static void DescribeIconWork(AppServiceHost host, StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Tệp biểu tượng: ghi mới {host.Import.IconsWritten}, tái sử dụng {host.Import.IconsReused}, thư mục bộ nhớ đệm hiện có {host.Icons.FileCount}");
    }

    private static void DescribeLibrarySize(AppServiceHost host, StringBuilder report)
    {
        var usable = host.Library.Items.Count(entry => entry is not null && entry.IsUsable);
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư viện ứng dụng hiện có {usable} bản ghi.");
    }

    private static string DescribeKind(AppItemKind kind) => kind switch
    {
        AppItemKind.Program => "Chương trình",
        AppItemKind.Shortcut => "Lối tắt",
        AppItemKind.Folder => "Thư mục",
        AppItemKind.File => "Tệp",
        AppItemKind.Url => "Trang web",
        _ => kind.ToString(),
    };

    private static string DescribeActivation(ActivationOutcome outcome) => outcome switch
    {
        ActivationOutcome.Activated => "Đã đưa cửa sổ hiện có lên tiền cảnh",
        ActivationOutcome.Launched => "Không có cửa sổ đang chạy, đã khởi chạy",
        ActivationOutcome.MultipleWindowsFound => "Tìm thấy nhiều cửa sổ, cần người dùng chọn",
        ActivationOutcome.Failed => "Thất bại",
        _ => outcome.ToString(),
    };

    private static string DescribeVerb(AppServiceVerb verb) => verb switch
    {
        AppServiceVerb.Import => "Nhập mục",
        AppServiceVerb.ScanStartMenu => "Quét menu Start",
        AppServiceVerb.ImportStartMenu => "Nhập menu Start",
        AppServiceVerb.ListLibrary => "Liệt kê thư viện ứng dụng",
        AppServiceVerb.CheckLibrary => "Kiểm tra lại mục tiêu mất hiệu lực",
        AppServiceVerb.Relocate => "Định vị lại mục",
        AppServiceVerb.Launch => "Mở mục",
        AppServiceVerb.Activate => "Kích hoạt hoặc khởi chạy mục",
        AppServiceVerb.VerifyIcons => "Kiểm tra biểu tượng bộ nhớ đệm",
        AppServiceVerb.IconContactSheet => "Tạo bảng tổng hợp biểu tượng",
        AppServiceVerb.Phase3Acceptance => "Nghiệm thu giai đoạn 3",
        AppServiceVerb.ClearIconCache => "Xóa bộ nhớ đệm biểu tượng",
        AppServiceVerb.MakeSamples => "Tạo mẫu nghiệm thu",
        AppServiceVerb.SeedLibrary => "Tạo mục tổng hợp",
        _ => verb.ToString(),
    };

    private static int ReadIntOption(IReadOnlyList<string> arguments, int index, int fallback)
    {
        if (index >= arguments.Count)
        {
            return fallback;
        }

        return int.TryParse(arguments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
