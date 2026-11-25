using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.Core.Storage;
using Glassquid.Platform;
using Glassquid.UI.Theming;

namespace Glassquid.App.Startup;

/// <summary>
/// Kiểm chứng không giao diện những hạng mục dựng bắt buộc phải đúng trước tiên,
/// nếu không giao diện tạo ra hoàn toàn không đáng để xem.
/// </summary>
/// <remarks>
/// Được thiết kế để vừa chạy thủ công vừa chạy bằng kịch bản. Lớp này kiểm tra môi trường,
/// chạy thử toàn bộ lớp dữ liệu — ghi, lưu, nạp lại — và xác nhận vùng lưu trữ
/// của sản phẩm này không chồng lấn với các sản phẩm tham chiếu, vốn là yêu cầu bắt buộc trong bản kế hoạch.
///
/// Nó cũng kiểm tra xem các chuỗi hằng không phải ASCII trong mã nguồn có vượt qua được quá trình biên dịch hay không.
/// Tệp mã nguồn dùng UTF-8 không BOM; trên máy có trang mã ANSI là GBK, tệp bị giải mã sai
/// sẽ âm thầm biến thành chữ lỗi trên giao diện; so sánh chuỗi hằng với chuỗi thoát của nó
/// giúp phát hiện điều này ngay lúc dựng thay vì đợi đến khi chụp màn hình.
/// </remarks>
internal static class SelfTest
{
    public static int Run(AppPaths paths)
        => Run(paths, null);

    public static int Run(AppPaths paths, Stream? acceptanceReport)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine("Glassquid Tự kiểm tra");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Phiên bản sản phẩm: {typeof(SelfTest).Assembly.GetName().Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {Environment.Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Kiến trúc tiến trình: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine();

        report.AppendLine("[Khả năng hệ thống]");
        report.AppendLine(CultureInfo.InvariantCulture, $"Hệ điều hành: Windows {WindowsCapabilities.OsVersion} (Build {WindowsCapabilities.BuildNumber})");
        report.AppendLine(CultureInfo.InvariantCulture, $"Chất liệu nền hệ thống: {Yes(WindowsCapabilities.SupportsSystemBackdrop)} (cần Build 22621 trở lên)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Tùy chọn bo góc cửa sổ: {Yes(WindowsCapabilities.SupportsWindowCornerPreference)} (cần Build 22000 trở lên)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Thanh tiêu đề tối: {Yes(WindowsCapabilities.SupportsImmersiveDarkMode)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Chủ đề hệ thống: {(SystemThemeReader.IsLightTheme() ? "Sáng" : "Tối")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Hiệu ứng trong suốt hệ thống: {(SystemThemeReader.IsTransparencyEnabled() ? "Bật" : "Tắt")}");
        if (!WindowsCapabilities.IsSupported)
        {
            failures++;
            report.AppendLine("✗ Phiên bản Windows này thấp hơn phạm vi hỗ trợ (cần Windows 11 Build 22000 trở lên)");
        }

        report.AppendLine();

        CheckTextEncoding(report, ref failures);
        CheckStorage(paths, report, ref failures);
        CheckThemeResolution(report, ref failures);
        CheckSingleInstance(report, ref failures);
        CheckStorageIsolation(report);

        report.AppendLine();
        report.AppendLine(new string('=', 46));
        report.AppendLine(failures == 0
            ? "Kết quả: Toàn bộ kiểm tra đều đạt."
            : $"Kết quả: {failures} mục kiểm tra không đạt.");
        report.AppendLine(acceptanceReport is null
            ? string.Create(CultureInfo.InvariantCulture, $"Nhật ký tự kiểm tra: {Path.Combine(paths.LogDirectory, "selftest.txt")}")
            : "Báo cáo tự kiểm tra: nội dung được ghi vào luồng báo cáo do bên gọi chỉ định.");

        var text = report.ToString();
        if (acceptanceReport is null)
        {
            ReportWriter.Write(text, Path.Combine(paths.LogDirectory, "selftest.txt"));
        }
        else
        {
            AcceptanceEntryPoint.WriteText(acceptanceReport, text);
            ReportWriter.Write(text, null);
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Xác nhận chuỗi hằng không phải ASCII được giải mã đúng; thực chất đây là
    /// bước kiểm tra cách trình biên dịch đọc tệp mã nguồn UTF-8 của chúng ta.
    /// </summary>
    private static void CheckTextEncoding(StringBuilder report, ref int failures)
    {
        report.AppendLine("[Mã hóa văn bản]");

        // Một chuỗi hằng viết trực tiếp ký tự có dấu trong mã nguồn, chuỗi còn lại viết cùng nội dung bằng chuỗi thoát.
        // Dạng chuỗi thoát không bị ảnh hưởng bởi cách giải mã tệp.
        const string Literal = "Màn hình kính";
        const string Expected = "M\u00E0n h\u00ECnh k\u00EDnh";

        var literalOk = string.Equals(Literal, Expected, StringComparison.Ordinal);
        report.AppendLine(CultureInfo.InvariantCulture, $"Chuỗi hằng Unicode khớp với chuỗi thoát: {Yes(literalOk)} ({Literal})");

        if (!literalOk)
        {
            failures++;
            report.AppendLine("✗ Mã hóa tệp nguồn có thể đã bị giải mã sai, văn bản tiếng Việt trên giao diện sẽ bị lỗi chữ.");
        }

        // Cho một giá trị có dấu đi qua vòng lặp JSON thực tế mà phần cài đặt sử dụng.
        var sample = "Bảng khởi chạy và Thanh dock";
        var bytes = Encoding.UTF8.GetBytes(sample);
        var roundTripped = Encoding.UTF8.GetString(bytes);
        var roundTripOk = string.Equals(sample, roundTripped, StringComparison.Ordinal);
        report.AppendLine(CultureInfo.InvariantCulture, $"Khứ hồi UTF-8 nhất quán: {Yes(roundTripOk)}");

        if (!roundTripOk)
        {
            failures++;
        }

        report.AppendLine();
    }

    /// <summary>
    /// Xác nhận thư mục dữ liệu thật khả dụng,
    /// sau đó diễn tập luồng ghi trong hộp cát.
    /// </summary>
    /// <remarks>
    /// Các kiểm tra ghi, nạp lại và khôi phục được cố ý chạy trên một thư mục dùng một lần,
    /// thay vì thư mục riêng của sản phẩm. Tự kiểm tra không được làm thay đổi
    /// cấu hình của người dùng chỉ vì đã chạy một lần.
    /// </remarks>
    private static void CheckStorage(AppPaths paths, StringBuilder report, ref int failures)
    {
        report.AppendLine("[Đọc/ghi dữ liệu]");

        try
        {
            paths.EnsureCreated();
            report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu đã sẵn sàng: {paths.RootDirectory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ Không thể tạo thư mục dữ liệu: {ex.Message}");
            report.AppendLine();
            return;
        }

        // Đọc tài liệu thật là an toàn và rất đáng làm: nó giúp báo cáo xem dữ liệu hiện có
        // của người dùng hiện tại có lành mạnh hay không.
        try
        {
            var liveReport = new GlassquidDataStore(paths).LoadAll();
            report.AppendLine(CultureInfo.InvariantCulture, $"Số thông báo khi nạp dữ liệu hiện có: {liveReport.Notices.Count} (phải là 0)");

            if (liveReport.HasNotices)
            {
                failures++;
                foreach (var notice in liveReport.Notices)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  · {notice}");
                }
            }
        }
        catch (StorageException ex)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ Đọc dữ liệu hiện có thất bại: {ex.Message}");
        }

        var sandbox = new AppPaths(Path.Combine(Path.GetTempPath(), "Glassquid-selftest-" + Guid.NewGuid().ToString("n")));

        try
        {
            sandbox.EnsureCreated();

            var store = new GlassquidDataStore(sandbox);

            // Lần nạp đầu tiên trên thư mục hoàn toàn mới phải báo FirstRun, chứ không phải lỗi.
            var firstLoad = store.LoadAll();
            report.AppendLine(CultureInfo.InvariantCulture, $"Số thông báo khi nạp lần đầu trên thư mục mới: {firstLoad.Notices.Count} (phải là 0)");

            store.Settings.Current.Appearance.AccentColor = "#0A84FF";
            store.Settings.Save();

            var saved = File.Exists(sandbox.SettingsFile);
            report.AppendLine(CultureInfo.InvariantCulture, $"Đã ghi cài đặt: {Yes(saved)}");

            if (!saved)
            {
                failures++;
                report.AppendLine("✗ Tệp cài đặt chưa được tạo.");
            }

            // Bản sao lưu được tạo ở bước thay thế;
            // và chỉ từ lần lưu thứ hai mới có phiên bản trước để giữ lại. Chỉ lưu một lần rồi đi tìm .bak
            // là đang kiểm tra sai thứ rồi.
            store.Settings.Current.Appearance.ReduceMotion = true;
            store.Settings.Save();

            var backup = File.Exists(sandbox.SettingsFile + AtomicFile.BackupSuffix);
            report.AppendLine(CultureInfo.InvariantCulture, $"Giữ lại phiên bản trước (.bak) sau lần lưu thứ hai: {Yes(backup)}");

            if (!backup)
            {
                failures++;
                report.AppendLine("✗ Thay thế nguyên tử không giữ lại bản sao lưu phiên bản trước.");
            }

            // Dùng một thực thể lưu trữ thứ hai để nạp lại, tránh trường hợp có nội dung được lấy từ bộ nhớ.
            var secondLoad = new GlassquidDataStore(sandbox).LoadAll();
            var reloadOk = secondLoad.Notices.Count == 0;
            report.AppendLine(CultureInfo.InvariantCulture, $"Nạp lại không có bất thường: {Yes(reloadOk)}");

            if (!reloadOk)
            {
                failures++;
                foreach (var notice in secondLoad.Notices)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  · {notice}");
                }
            }

            // Tài liệu bị hỏng phải được giữ lại, chứ không được vứt bỏ.
            var corruption = CheckCorruptionRecovery();
            report.AppendLine(CultureInfo.InvariantCulture, $"Giữ lại tệp hỏng thay vì vứt bỏ: {Yes(corruption.Preserved)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Khôi phục từ bản sao lưu sau khi hỏng: {Yes(corruption.Recovered)}");

            if (!corruption.Preserved || !corruption.Recovered)
            {
                failures++;
            }
        }
        catch (StorageException ex)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ Đọc/ghi dữ liệu thất bại: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(sandbox.RootDirectory);
        }

        report.AppendLine();
    }

    /// <summary>
    /// Cố tình làm hỏng một tệp cài đặt để kiểm tra xem trình nạp có cứu lại bản sao lưu hay không,
    /// thay vì làm mất dữ liệu của người dùng.
    /// </summary>
    private static (bool Preserved, bool Recovered) CheckCorruptionRecovery()
    {
        var sandbox = new AppPaths(Path.Combine(Path.GetTempPath(), "Glassquid-corrupt-" + Guid.NewGuid().ToString("n")));

        try
        {
            sandbox.EnsureCreated();

            var seed = new GlassquidDataStore(sandbox);
            seed.LoadAll();
            seed.Settings.Save();

            // Lần lưu thứ hai để bước thay thế có phiên bản trước giữ lại làm bản sao lưu.
            seed.Settings.Current.Appearance.ReduceMotion = true;
            seed.Settings.Save();

            File.WriteAllText(sandbox.SettingsFile, "{ this is not valid json");

            var damaged = new GlassquidDataStore(sandbox);
            var outcome = damaged.Settings.Load();

            var preserved = outcome.PreservedFile is not null && File.Exists(outcome.PreservedFile);
            var recovered = outcome.Status == LoadStatus.RecoveredFromBackup;

            return (preserved, recovered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            return (false, false);
        }
        finally
        {
            TryDeleteDirectory(sandbox.RootDirectory);
        }
    }

    private static void CheckThemeResolution(StringBuilder report, ref int failures)
    {
        report.AppendLine("[Phân giải chủ đề]");

        var theme = ThemeService.Current;
        if (theme is null)
        {
            failures++;
            report.AppendLine("✗ Dịch vụ chủ đề chưa được khởi tạo.");
            report.AppendLine();
            return;
        }

        var palette = theme.Palette;
        report.AppendLine(CultureInfo.InvariantCulture, $"Chủ đề mặc định: {theme.Appearance.Theme} (hiện tại {(theme.IsDark ? "Tối" : "Sáng")})");
        report.AppendLine(CultureInfo.InvariantCulture, $"Chế độ bảng điều khiển: {(theme.UseOpaquePanels ? "Đục" : "Kính bán trong suốt")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Màu nhấn: #{palette.Accent.R:X2}{palette.Accent.G:X2}{palette.Accent.B:X2}");
        var dockRadius = Math.Max(40, theme.Appearance.DockCornerRadius);
        var launcherRadius = Math.Max(36, theme.Appearance.LauncherCornerRadius);
        report.AppendLine(CultureInfo.InvariantCulture, $"Độ bo góc thực tế của thanh dock: {dockRadius} DIP (tối thiểu 40)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Độ bo góc thực tế của bảng khởi chạy: {launcherRadius} DIP (tối thiểu 36)");

        var dockRadiusOk = dockRadius >= 40;
        var launcherRadiusOk = launcherRadius >= 36;
        report.AppendLine(CultureInfo.InvariantCulture, $"Độ bo góc kính lỏng giữ được độ cong rõ rệt: {Yes(dockRadiusOk && launcherRadiusOk)}");

        if (!dockRadiusOk || !launcherRadiusOk)
        {
            failures++;
        }

        var accentOk = palette.Accent.R == 0x0A && palette.Accent.G == 0x84 && palette.Accent.B == 0xFF;
        if (!accentOk)
        {
            failures++;
            report.AppendLine("✗ Màu nhấn mặc định không phải #0A84FF như đã hoạch định.");
        }

        report.AppendLine();
    }

    private static void CheckSingleInstance(StringBuilder report, ref int failures)
    {
        report.AppendLine("[Đơn phiên bản]");

        const string ProbeName = "selftest-probe";

        try
        {
            using var first = SingleInstanceGuard.Acquire(ProbeName);
            report.AppendLine(CultureInfo.InvariantCulture, $"Phiên bản đầu tiên giành được quyền sở hữu: {Yes(first.IsFirstInstance)}");

            using var second = SingleInstanceGuard.Acquire(ProbeName);
            report.AppendLine(CultureInfo.InvariantCulture, $"Phiên bản thứ hai được nhận diện là trùng lặp: {Yes(!second.IsFirstInstance)}");

            if (!first.IsFirstInstance || second.IsFirstInstance)
            {
                failures++;
                report.AppendLine("✗ Phán đoán đơn phiên bản không chính xác.");
            }

            // Tên phiên bản thật tuyệt đối không được trùng với sản phẩm tham chiếu.
            var real = SingleInstanceGuard.Acquire("Phiên bản chính");
            report.AppendLine(CultureInfo.InvariantCulture, $"Định danh đơn phiên bản của sản phẩm khả dụng: {Yes(real.IsFirstInstance)}");
            real.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ Kiểm tra đơn phiên bản thất bại: {ex.Message}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// Báo cáo xem các sản phẩm tham chiếu đã được cài đặt hay chưa, đồng thời khẳng định rõ
    /// chúng ta không đọc hay ghi dữ liệu của chúng. Đây là hạng mục nghiệm thu của giai đoạn 1,
    /// nên phải kiểm tra để xác nhận chứ không chỉ khẳng định bằng lời.
    /// </summary>
    private static void CheckStorageIsolation(StringBuilder report)
    {
        report.AppendLine("[Cô lập với sản phẩm tham chiếu]");

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var macDock = Path.Combine(roaming, "MacDock");
        var ymLauncher = Path.Combine(roaming, "Ym Launcher");
        var launcherStart = Path.Combine(roaming, "Launcher\u542F\u52A8\u53F0");

        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu sản phẩm tham chiếu tồn tại (chỉ dò tìm, không đọc/ghi): MacDock={Yes(Directory.Exists(macDock))}, Ym Launcher={Yes(Directory.Exists(ymLauncher))}, Launcher Khởi chạy={Yes(Directory.Exists(launcherStart))}");

        var ours = AppPaths.ForCurrentUser().RootDirectory;
        var overlaps = new[] { macDock, ymLauncher, launcherStart }
            .Any(other => other.StartsWith(ours, StringComparison.OrdinalIgnoreCase) ||
                          ours.StartsWith(other, StringComparison.OrdinalIgnoreCase));

        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục dữ liệu của sản phẩm này: {ours}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Chồng lấn với thư mục sản phẩm tham chiếu: {Yes(overlaps)} (bắt buộc là Không)");

        report.AppendLine();
    }

    private static string Yes(bool value) => value ? "Có" : "Không";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Thư mục tạm còn sót lại không gây hại gì; kết quả tự kiểm tra mới là điều quan trọng.
        }
    }
}
