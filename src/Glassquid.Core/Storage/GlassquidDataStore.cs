using Glassquid.Core.Models;

namespace Glassquid.Core.Storage;

/// <summary>Kết quả tải toàn bộ tài liệu, câu chữ hướng tới người dùng.</summary>
public sealed class DataLoadReport
{
    public LoadStatus LibraryStatus { get; internal set; }
    /// <summary>Các thông báo đáng hiển thị. Rỗng nghĩa là mọi tệp đều được tải suôn sẻ.</summary>
    public List<string> Notices { get; } = [];

    /// <summary>Vị trí lưu giữ tệp bị hỏng (nếu có).</summary>
    public List<string> PreservedFiles { get; } = [];

    public bool HasNotices => Notices.Count > 0;
}

/// <summary>
/// Tầng dữ liệu của sản phẩm: cài đặt, thư viện ứng dụng dùng chung, hai bố cục, cùng
/// chức năng sao lưu và khôi phục bao phủ toàn bộ các nội dung trên.
/// </summary>
/// <remarks>
/// Mọi thứ bên dưới kiểu dữ liệu này đều thao tác trực tiếp với tệp; mọi thứ bên trên nó (Dock, Launchpad, trung tâm cài đặt)
/// chỉ tương tác qua các bộ lưu trữ này thay vì thao tác trực tiếp với đường dẫn. Chính điều này giúp quy tắc trong bản kế hoạch
/// "lời gọi registry, tiến trình và handle cửa sổ không nằm rải rác trong trình xử lý sự kiện giao diện"
/// được thực thi nghiêm ngặt.
/// </remarks>
public sealed class GlassquidDataStore
{
    public GlassquidDataStore(AppPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Paths = paths;
        Settings = new SettingsStore(paths.SettingsFile, timeProvider);
        Library = new AppLibraryStore(paths.AppLibraryFile, timeProvider);
        Layouts = new LayoutStore(paths.DockLayoutFile, paths.LauncherLayoutFile, timeProvider);
        Backup = new BackupService(paths, timeProvider);
    }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public AppLibraryStore Library { get; }

    public LayoutStore Layouts { get; }

    public BackupService Backup { get; }

    /// <summary>
    /// Tạo thư mục dữ liệu và tải toàn bộ nội dung.
    /// </summary>
    /// <remarks>
    /// Tuyệt đối không ném ngoại lệ khi gặp tệp bị hỏng hoặc bị thiếu: mỗi bộ lưu trữ tự lùi về bản sao lưu hoặc giá trị mặc định của riêng mình,
    /// và giải thích chuyện gì đã xảy ra thông qua báo cáo trả về.
    /// Tệp cài đặt bị hỏng tuyệt đối không được ngăn ứng dụng khởi động.
    /// </remarks>
    public DataLoadReport LoadAll()
    {
        Paths.EnsureCreated();

        var report = new DataLoadReport();

        Describe(report, Settings.Load(), "cài đặt");
        var libraryOutcome = Library.Load();
        report.LibraryStatus = libraryOutcome.Status;
        Describe(report, libraryOutcome, "thư viện ứng dụng");
        Describe(report, Layouts.LoadDock(), "bố cục Dock");
        Describe(report, Layouts.LoadLauncher(), "bố cục Launchpad");

        // Mục bất thường không chặn quá trình khởi động, nhưng phải thông báo cho người dùng biết trong tệp có phần bị hỏng.
        if (Library.AnomalousEntryCount > 0)
        {
            report.Notices.Add(
                $"Đã bỏ qua {Library.AnomalousEntryCount} mục không hợp lệ trong thư viện ứng dụng (có thể trống hoặc thiếu trường); các mục này vẫn được giữ trong tệp.");
        }

        // Cơ chế tự sửa đã thay đổi dữ liệu người dùng nên phải hiển thị rõ. Nguyên nhân điển hình là thư mục dữ liệu bị di chuyển toàn bộ,
        // hoặc bản sao lưu được khôi phục sang thư mục khác: tham chiếu vẫn đang trỏ về vị trí cũ.
        if (Library.RepairedIconReferenceCount > 0)
        {
            report.Notices.Add(
                $"Đã cập nhật đường dẫn biểu tượng tùy chỉnh của {Library.RepairedIconReferenceCount} mục từ thư mục dữ liệu cũ về thư mục hiện tại.");
        }

        return report;
    }

    /// <summary>
    /// Xóa một bản ghi khỏi thư viện ứng dụng và xóa bỏ mọi tham chiếu tới nó trong các bố cục,
    /// sau đó lưu bền vững cả ba tài liệu bị ảnh hưởng.
    /// </summary>
    /// <remarks>
    /// Đây là hành động "Xóa khỏi thư viện ứng dụng". Phương thức này chủ đích không phải là đích gọi của lệnh gỡ khỏi Dock —
    /// lệnh kia chỉ bỏ ghim và phải giữ nguyên bản ghi.
    /// </remarks>
    public bool DeleteFromLibrary(string? itemId)
    {
        if (!Library.Remove(itemId))
        {
            return false;
        }

        Layouts.PurgeItem(itemId);

        Library.Save();
        Layouts.SaveDock();
        Layouts.SaveLauncher();
        return true;
    }

    private static void Describe<T>(DataLoadReport report, LoadOutcome<T> outcome, string label)
        where T : class
    {
        if (outcome.PreservedFile is not null)
        {
            report.PreservedFiles.Add(outcome.PreservedFile);
        }

        switch (outcome.Status)
        {
            case LoadStatus.Loaded:
            case LoadStatus.FirstRun:
                break;

            case LoadStatus.RecoveredFromBackup:
                report.Notices.Add($"Tệp {label} bị hỏng; đã khôi phục từ bản sao lưu.");
                break;

            case LoadStatus.Unrecoverable:
                report.Notices.Add($"Không thể đọc tệp {label} hoặc bản sao lưu; đã dùng giá trị mặc định.");
                break;

            default:
                report.Notices.Add($"Không xác định được trạng thái của tệp {label}.");
                break;
        }

        if (outcome.PreservedFile is not null)
        {
            report.Notices.Add($"Đã giữ lại tệp {label} bị hỏng để tiện kiểm tra.");
        }
    }
}
