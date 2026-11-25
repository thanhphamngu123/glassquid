using Glassquid.Core.Contracts;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>
/// Điểm đầu vào cho bộ dịch vụ ứng dụng của Giai đoạn 3.
/// </summary>
/// <remarks>
/// Tồn tại để host và hai giao diện không phải tự ghép lại phần lưu trữ, bộ nhớ đệm biểu tượng, nhập và khởi chạy —
/// có rất nhiều cách ghép sai (dùng các thực thể <see cref="AppLibraryStore"/> khác nhau,
/// quên gọi <c>EnsureCreated</c> trước, trỏ thư mục bộ nhớ đệm sang nơi khác), và chúng đều không báo lỗi,
/// mà chỉ biểu hiện thành "đồ vừa nhập sau khi khởi động lại thì biến mất".
///
/// <para>
/// Nó <strong>không</strong> nắm giữ bất kỳ cửa sổ nào và không tham chiếu WPF. Cả Dock lẫn Launchpad đều thao tác thông qua nó,
/// nhờ vậy quy tắc kiến trúc "giao diện không chạm trực tiếp vào hệ thống" vẫn giữ nguyên hiệu lực sau Giai đoạn 3.
/// </para>
/// </remarks>
public sealed class AppServiceHost : IDisposable, IImportObserver
{
    private readonly GlassquidDataStore _data;
    private readonly IImportObserver? _externalObserver;

    public AppServiceHost(GlassquidDataStore data, IImportObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        _externalObserver = observer;

        // Thư mục bộ nhớ đệm biểu tượng phải tồn tại trước, nếu không lần trích xuất biểu tượng đầu tiên sẽ ghi thất bại,
        // mà thất bại được thiết kế thành "âm thầm bỏ qua", dẫn đến biểu hiện "mọi mục đều không có biểu tượng".
        data.Paths.EnsureCreated();

        Icons = new IconCache(data.Paths);
        Import = new ImportService(data.Library, Icons, this, data.Layouts,
            () => Math.Clamp(data.Settings.Current.Launcher.Rows, 2, 10)
                * Math.Clamp(data.Settings.Current.Launcher.Columns, 2, 12));
        Launch = new LaunchService(data.Library);
        Maintenance = new LibraryMaintenance(data.Library, Icons, this);
        Running = new RunningAppTracker(data.Library);
        Search = new SearchService(() => data.Library.Items, () => data.Layouts.Launcher);
        Editor = new AppEntryEditor(data.Library, data.Paths, OnLibraryChanged);
    }

    public IconCache Icons { get; }

    public ImportService Import { get; }

    public LaunchService Launch { get; }

    public LibraryMaintenance Maintenance { get; }

    public SearchService Search { get; }

    public AppEntryEditor Editor { get; }

    public event EventHandler? LibraryChanged;

    public void OnLibraryChanged()
    {
        _externalObserver?.OnLibraryChanged();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Theo dõi trạng thái đang chạy (D-07). Chỉ sau khi gọi <see cref="RunningAppTracker.Start"/>
    /// mới bắt đầu lắng nghe sự kiện cửa sổ; trước đó có thể gọi <see cref="RunningAppTracker.Reconcile"/> khi cần.
    /// </summary>
    public RunningAppTracker Running { get; }

    /// <summary>Bố cục riêng của Dock và Launchpad (thứ tự ghim, v.v.).</summary>
    public LayoutStore Layouts => _data.Layouts;

    /// <summary>
    /// Thư viện ứng dụng dùng chung trong phiên làm việc này.
    /// </summary>
    /// <remarks>
    /// Mọi lệnh đều đọc từ thực thể duy nhất này, thay vì mỗi nơi tải riêng một bản. Nếu mỗi nơi tự tải sẽ xảy ra lỗi
    /// "sau khi nhập xong liệt kê ngay nhưng không thấy mục mới" — vì danh sách đó đọc bản chụp được tải
    /// từ trước khi nhập. Loại lỗi này không ném ngoại lệ, chỉ là dữ liệu trông sai lệch.
    /// </remarks>
    public AppLibraryStore Library => _data.Library;

    public AppPaths Paths => _data.Paths;

    /// <summary>
    /// Phơi bày khả năng nhập theo hình dạng ổn định <see cref="IImportService"/>.
    /// </summary>
    /// <remarks>
    /// Giao diện nhận hợp đồng này thay vì <see cref="ImportService"/>, nhờ vậy mã giao diện phụ thuộc vào hợp đồng,
    /// chứ không phụ thuộc vào phần cài đặt cụ thể; sau này phần cài đặt có đổi thế nào cũng không ảnh hưởng tới nó.
    /// </remarks>
    public IImportService ImportContract => Import;

    /// <summary>Phơi bày khả năng khởi chạy theo hình dạng ổn định <see cref="ILaunchService"/>.</summary>
    public ILaunchService LaunchContract => Launch;

    /// <summary>Quét menu Bắt đầu và báo cáo quy mô quan sát được trong lần này.</summary>
    public static StartMenuScanResult ScanStartMenu() => StartMenuScanner.Scan();

    /// <summary>
    /// Nhập các mục đã chọn trong kết quả quét vào thư viện ứng dụng.
    /// </summary>
    /// <remarks>
    /// Tên dùng tên hiển thị của Shell trên lối tắt, thay vì tên tệp mục tiêu đã phân giải: người dùng nhìn thấy trên menu Bắt đầu
    /// là "Paint", trong khi tệp mục tiêu lại tên là <c>mspaint.exe</c>. Tên hiển thị Shell cũng giữ nguyên chữ đã bản địa hóa.
    /// </remarks>
    public ImportResult ImportStartMenuShortcuts(IEnumerable<StartMenuShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        var requests = shortcuts
            .Select(shortcut => new ImportRequest
            {
                Source = shortcut.Path,
                DisplayName = shortcut.Name,
            })
            .ToList();

        return Import.ImportItems(requests);
    }

    /// <summary>
    /// Tự thêm các lối tắt menu Bắt đầu chưa có trong ứng dụng thư viện.
    /// Các mục đã biết không bị đưa trở lại Launchpad nếu người dùng đã gỡ chúng khỏi bố cục.
    /// </summary>
    public ImportResult ImportNewStartMenuShortcuts(IEnumerable<StartMenuShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        var requests = shortcuts
            .Select(shortcut => new ImportRequest
            {
                Source = shortcut.Path,
                DisplayName = shortcut.Name,
            })
            .ToList();

        return Import.ImportItems(requests, addExistingEntriesToLauncher: false);
    }

    public void Dispose()
    {
        // Hiện chưa có tài nguyên không được quản lý nào cần giải phóng tường minh: các đối tượng COM đều đã được giải phóng theo cặp trong từng lời gọi,
        // handle biểu tượng cũng vậy. Luồng sự kiện theo dõi trạng thái đang chạy được dọn dẹp tại đây.
        Running.Dispose();
    }
}
