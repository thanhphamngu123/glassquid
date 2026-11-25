using Glassquid.Core.Models;

namespace Glassquid.Core.Contracts;

/// <summary>
/// Giao diện nội bộ ổn định mà mã giao diện người dùng (UI) phụ thuộc vào khi lập trình.
/// </summary>
/// <remarks>
/// Đây đều là các giao diện nội bộ trong tiến trình; sản phẩm này không công khai bất kỳ dịch vụ mạng nào ra bên ngoài. Ý nghĩa tồn tại của chúng là để
/// mã cửa sổ gửi yêu cầu tới một dịch vụ thực hiện công việc, thay vì tự thao tác trực tiếp với registry, tiến trình hoặc
/// handle cửa sổ tại chỗ — đây là một quy tắc kiến trúc trong bản kế hoạch.
///
/// Sao lưu và khôi phục đã được
/// <see cref="Storage.BackupService"/> triển khai thực tế. Các giao diện còn lại được khai báo tại đây theo đúng cấu trúc đã thỏa thuận,
/// từng thành viên đều ghi chú rõ giai đoạn triển khai; trong khung xương giai đoạn 1 vẫn chưa có gì
/// giả vờ như đã đáp ứng chúng.
/// </remarks>
public static class ContractPhases
{
    public const string Import = "Giai đoạn 3";
    public const string Launch = "Giai đoạn 3";
    public const string Search = "Giai đoạn 5";
    public const string Layout = "Giai đoạn 4 / 5";
    public const string Appearance = "Giai đoạn 2 / 6";
}

/// <summary>Một mục mà người dùng yêu cầu đưa vào thư viện ứng dụng.</summary>
public sealed class ImportRequest
{
    /// <summary>Đường dẫn tuyệt đối; khi <see cref="IsUrl"/> được bật thì đây là một URL.</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsUrl { get; set; }

    /// <summary>Nếu người dùng đổi tên khi nhập, sử dụng giá trị này để ghi đè tên được suy luận.</summary>
    public string? DisplayName { get; set; }
}

public enum ImportFailureReason
{
    NotFound = 0,
    UnsupportedType = 1,
    AccessDenied = 2,
    ShortcutUnreadable = 3,
    Other = 4,
}

public sealed class ImportFailure
{
    public string Source { get; init; } = string.Empty;

    public ImportFailureReason Reason { get; init; }

    /// <summary>Thông báo có thể hiển thị an toàn bên cạnh mục này.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>Kết quả gồm các mục đã thêm mới, trùng lặp và thất bại trong một yêu cầu nhập.</summary>
public sealed class ImportResult
{
    public IReadOnlyList<AppEntry> Added { get; init; } = [];

    public IReadOnlyList<AppEntry> Duplicates { get; init; } = [];

    public IReadOnlyList<ImportFailure> Failures { get; init; } = [];
}

public enum LaunchFailureReason
{
    UnknownItem = 0,
    TargetMissing = 1,
    AccessDenied = 2,
    ShellRejected = 3,

    /// <summary>Định dạng của mục tiêu không rõ cách mở.</summary>
    Unsupported = 4,
}

public sealed class LaunchResult
{
    public bool Succeeded { get; init; }

    public LaunchFailureReason? Failure { get; init; }

    /// <summary>Giải thích dành cho người dùng xem. Tuyệt đối không được là thông báo ngoại lệ thô.</summary>
    public string? Message { get; init; }
}

/// <summary>Kết quả của việc ưu tiên tái sử dụng cửa sổ hiện có thay vì khởi chạy thêm một thực thể mới.</summary>
public enum ActivationOutcome
{
    /// <summary>Cửa sổ hiện có đã được đưa lên tiền cảnh.</summary>
    Activated = 0,

    /// <summary>Không có cửa sổ khớp, nên đã khởi chạy ứng dụng.</summary>
    Launched = 1,

    /// <summary>Có nhiều cửa sổ khớp, người dùng bắt buộc phải chọn.</summary>
    MultipleWindowsFound = 2,

    Failed = 3,

    /// <summary>Nhấp lại vào biểu tượng Dock của ứng dụng đang ở tiền cảnh, cửa sổ đã được thu nhỏ.</summary>
    Minimized = 4,
}

public sealed class ActivationResult
{
    public ActivationOutcome Outcome { get; init; }

    /// <summary>Các cửa sổ ứng cử khi xảy ra <see cref="ActivationOutcome.MultipleWindowsFound"/>.</summary>
    public IReadOnlyList<WindowCandidate> Candidates { get; init; } = [];

    public string? Message { get; init; }
}

/// <summary>Cửa sổ có thể thuộc về một mục nào đó trong thư viện ứng dụng.</summary>
public sealed class WindowCandidate
{
    public nint Handle { get; init; }

    public string Title { get; init; } = string.Empty;
}

public enum SearchMatchKind
{
    /// <summary>Từ truy vấn trùng khớp hoàn toàn với tên.</summary>
    ExactName = 0,

    /// <summary>Tên bắt đầu bằng từ truy vấn.</summary>
    NamePrefix = 1,

    /// <summary>Khớp toàn bộ bính âm (Pinyin).</summary>
    Pinyin = 2,

    /// <summary>Khớp chữ cái đầu bính âm (Pinyin).</summary>
    PinyinInitials = 3,

    /// <summary>Tên có chứa từ truy vấn ở một vị trí nào đó.</summary>
    NameContains = 4,

    /// <summary>Khớp bí danh hoặc đường dẫn mục tiêu.</summary>
    Alias = 5,
}

/// <summary>Một kết quả tìm kiếm đã được xếp hạng. Dịch vụ chịu trách nhiệm sắp xếp.</summary>
public sealed class SearchHit
{
    public AppEntry Item { get; init; } = null!;

    public SearchMatchKind MatchKind { get; init; }

    /// <summary>
    /// Điểm số tương đối. Chỉ có ý nghĩa khi sắp xếp trong phạm vi một tập kết quả;
    /// thang điểm không thuộc một phần của hợp đồng giao diện.
    /// </summary>
    public int Score { get; init; }
}

/// <summary>Nhập ứng dụng, lối tắt, thư mục, tệp và URL.</summary>
public interface IImportService
{
    /// <summary>Quá trình nhập tuyệt đối không làm hỏng thư viện ứng dụng hiện có; lỗi được báo cáo riêng cho từng mục.</summary>
    ImportResult ImportItems(IReadOnlyList<ImportRequest> requests);
}

/// <summary>Mở mục trong thư viện ứng dụng và ưu tiên tái sử dụng cửa sổ hiện có.</summary>
public interface ILaunchService
{
    /// <summary>Mở mục tiêu theo loại của nó.</summary>
    LaunchResult LaunchItem(string itemId);

    /// <summary>Đưa cửa sổ hiện có lên phía trước; nếu không có thì khởi chạy ứng dụng.</summary>
    ActivationResult ActivateOrLaunch(string itemId);
}

/// <summary>Tìm kiếm trong thư viện ứng dụng dùng chung. Chạy hoàn toàn cục bộ; không thu thập dữ liệu đo từ xa (telemetry), không kết nối mạng.</summary>
public interface ISearchService
{
    IReadOnlyList<SearchHit> SearchApps(string query, int limit);
}

/// <summary>
/// Áp dụng toàn bộ thay đổi bố cục chỉ trong một lần gọi. Bên gọi gửi lên cách sắp xếp cuối cùng,
/// thay vì một chuỗi các chỉnh sửa tăng dần, nhờ vậy thao tác bị hủy giữa chừng khi đang kéo
/// sẽ không để lại bố cục bị lưu dở dang.
/// </summary>
public interface ILayoutService
{
    void UpdateLayout(DockLayout dock, LauncherLayout launcher);
}

/// <summary>Đẩy các thay đổi giao diện tới mọi cửa sổ đang mở chỉ trong một lần.</summary>
public interface IAppearanceService
{
    void ApplyAppearance(Models.Settings.AppearanceSettings appearance);
}
