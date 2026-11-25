namespace Glassquid.App;

/// <summary>
/// Kết quả của một lần thử khởi động.
/// </summary>
/// <remarks>
/// Dùng enum thay vì kiểu boolean vì ở đây có ba kết cục thực sự, chứ không phải chỉ hai trạng thái "thành công/thất bại".
/// Đặc biệt là <see cref="NothingToDo"/>: bản thân quá trình khởi động không có vấn đề gì, chỉ là không có việc gì để làm;
/// nếu nhét nó vào false thì bên gọi sẽ không phân biệt được giữa "nhường cho tiến trình khác" và "vốn không có việc gì để làm".
/// </remarks>
internal enum StartupOutcome
{
    /// <summary>Tiến trình này sở hữu giao diện, sản phẩm đã đang chạy.</summary>
    Started = 0,

    /// <summary>Một tiến trình khác đang giữ giao diện, ý định của lần khởi động này đã được chuyển giao cho nó.</summary>
    HandedOffToRunningInstance = 1,

    /// <summary>
    /// Lần khởi động này yêu cầu "cho tiến trình đang chạy thoát", nhưng lúc đó không có tiến trình nào đang chạy.
    /// Không có đối tượng để thông báo, và cũng không nên vì thế mà kéo giao diện lên.
    /// </summary>
    NothingToDo = 2,

    /// <summary>Phát hiện giao thức tiến trình phiên bản cũ, đã chặn việc chạy đồng thời cả phiên bản cũ và mới.</summary>
    LegacyInstanceBlocked = 3,

    /// <summary>Không thể lấy khóa thuê dùng chung dữ liệu người dùng, đã chặn khởi động trước khi tải dữ liệu.</summary>
    UserDataLeaseUnavailable = 4,

    /// <summary>Không thể nhận diện màn hình chính hiện tại của Windows, đã chặn khởi động trước khi tạo cửa sổ hiển thị.</summary>
    PrimaryDisplayUnavailable = 5,
}
