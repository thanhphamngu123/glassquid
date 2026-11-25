namespace Glassquid.Core.Models;

/// <summary>
/// Một bản ghi trong thư viện ứng dụng dùng chung.
/// </summary>
/// <remarks>
/// Cả Dock và Launchpad đều tham chiếu các bản ghi này theo <see cref="Id"/>, cả hai đều không giữ bản sao riêng.
/// Việc gỡ một mục khỏi Dock chỉ là bỏ ghim —
/// bản ghi vẫn lưu tại đây cho đến khi người dùng xóa hẳn khỏi thư viện.
///
/// Các thuộc tính cố tình không đánh dấu <c>required</c> mà có giá trị mặc định vô hại.
/// Tệp cài đặt bị thiếu một trường nào đó vẫn phải tải được để người dùng tự sửa,
/// thay vì ném ngoại lệ giải tuần tự hóa làm mất toàn bộ thư viện.
/// Dùng <see cref="IsUsable"/> để xác định một bản ghi có ý nghĩa hay không.
/// </remarks>
public sealed class AppEntry
{
    /// <summary>
    /// Định danh ổn định. Chỉ tạo một lần khi nhập và không bao giờ ghi đè sau đó, nhờ vậy khi tên hiển thị hoặc đích thay đổi, bố cục
    /// vẫn trỏ đúng tới bản ghi.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Tên hiển thị cho người dùng. Khi nhập mặc định là tên tệp.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public AppItemKind Kind { get; set; } = AppItemKind.Program;

    /// <summary>Đường dẫn tuyệt đối; là URL khi <see cref="Kind"/> là <see cref="AppItemKind.Url"/>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Tham số dòng lệnh truyền vào khi khởi chạy. Bằng null khi không có tham số.</summary>
    public string? Arguments { get; set; }

    /// <summary>Thư mục làm việc của tiến trình được khởi chạy. Bằng null nghĩa là "giao cho shell quyết định".</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Định danh ứng dụng được đặt tường minh trên lối tắt đích (<c>System.AppUserModel.ID</c>).
    /// </summary>
    /// <remarks>
    /// Được đọc kèm khi nhập <c>.lnk</c> (trình phân giải lối tắt đọc kho thuộc tính đồng thời khi phân giải đích).
    /// Cửa sổ của ứng dụng đóng gói và UWP không thuộc về tiến trình mà lối tắt trỏ tới, nên tìm cửa sổ theo đường dẫn ảnh tiến trình chắc chắn thất bại;
    /// định danh này là mối liên kết đáng tin cậy duy nhất giữa lối tắt và cửa sổ đang chạy (đo thực tế từ đầu dò giai đoạn 4,
    /// xem tài liệu đầu ra đo thực tế đầu dò giai đoạn 4 trong <c>docs/evidence/</c>). Các mục không có định danh tường minh giữ giá trị null,
    /// việc khớp sẽ quay về dùng đường dẫn ảnh tiến trình.
    /// </remarks>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// Nguồn của biểu tượng gốc, ví dụ đường dẫn tệp thực thi hoặc vị trí biểu tượng shell.
    /// Giữ lại trường này để có thể trích xuất lại biểu tượng sau khi bộ nhớ đệm bị xóa.
    /// </summary>
    public string? IconSource { get; set; }

    /// <summary>Tệp biểu tượng do người dùng tự chọn. Ưu tiên cao hơn <see cref="IconSource"/>.</summary>
    public string? CustomIconPath { get; set; }

    /// <summary>
    /// Từ khóa tìm kiếm do người dùng thêm bổ sung. Dùng để sửa các trường hợp tạo bính âm tự động đọc sai,
    /// đặc biệt là chữ đa âm.
    /// </summary>
    public List<string> SearchAliases { get; set; } = [];

    /// <summary>
    /// Bính âm đầy đủ phục vụ tìm kiếm.
    /// </summary>
    /// <remarks>
    /// Khi nhập <strong>không</strong> điền sẵn, giữ trường ở giá trị null; giai đoạn 5 khi triển khai tìm kiếm sẽ duyệt toàn bộ thư viện để bổ sung.
    /// Lý do xem mục 6 trong báo cáo bàn giao giai đoạn 3 (<c>docs/</c>): điền đúng đòi hỏi một bảng chữ Hán sang bính âm đầy đủ,
    /// trong khi độ chính xác phát âm của chữ đa âm là bài toán riêng của giai đoạn 5 (danh sách tính năng nêu rõ không cam kết tự động đọc đúng hết).
    /// Nếu hiện tại điền một bảng chưa đầy đủ thì chẳng khác nào để lại một cái bẫy "nhìn như đã hỗ trợ nhưng thực ra chưa đủ" cho giai đoạn 5.
    /// </remarks>
    public string? PinyinIndex { get; set; }

    /// <summary>Chữ cái đầu của mỗi âm tiết, dùng cho tìm kiếm viết tắt. Trước thời điểm đó giữ null.</summary>
    public string? PinyinInitials { get; set; }

    /// <summary>
    /// Được bật khi không tìm thấy đích. Mục vẫn được giữ lại và hiển thị là mất liên kết để người dùng định vị lại,
    /// thay vì bị âm thầm loại bỏ.
    /// </summary>
    public bool IsTargetMissing { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Trả về true khi nội dung mà bản ghi trỏ tới có thể mở được.
    /// </summary>
    /// <remarks>
    /// Tách riêng với <see cref="IsUsable"/> vì id do chúng ta cấp phát, còn target phải do bên gọi cung cấp.
    /// Khi nhập kiểm tra thuộc tính này; khi đọc lại từ tệp thì kiểm tra <see cref="IsUsable"/>, vì bản ghi không có id
    /// không thể được bố cục tham chiếu.
    /// </remarks>
    public bool HasLaunchTarget => !string.IsNullOrWhiteSpace(Target);

    /// <summary>Trả về true khi bản ghi có thể lưu trữ và được bố cục tham chiếu.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Id) && HasLaunchTarget;
}