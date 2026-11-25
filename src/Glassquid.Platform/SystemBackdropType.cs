namespace Glassquid.Platform;

/// <summary>
/// Các chất liệu nền DWM mà sản phẩm có thể yêu cầu.
/// </summary>
/// <remarks>
/// Giá trị khớp với enum <c>DWM_SYSTEMBACKDROP_TYPE</c>. Chất liệu chỉ là một yêu cầu,
/// không phải sự đảm bảo: kết quả kết xuất cuối cùng do trình tổng hợp quyết định, nên lớp nền tự vẽ bên dưới
/// vẫn phải tự đứng vững khi hiển thị độc lập.
/// </remarks>
public enum SystemBackdropType
{
    /// <summary>Để hệ thống tự chọn. Trên Windows 11 tương đương với <see cref="MainWindow"/>.</summary>
    Auto = 0,

    /// <summary>Không dùng chất liệu; cửa sổ tự vẽ toàn bộ.</summary>
    None = 1,

    /// <summary>Mica — lấy sắc thái từ hình nền, dịu nhẹ. Chất liệu bảng mặc định.</summary>
    MainWindow = 2,

    /// <summary>Acrylic — làm mờ nội dung phía sau cửa sổ. Dùng cho bề mặt tạm thời.</summary>
    TransientWindow = 3,

    /// <summary>Mica Alt — biến thể có độ tương phản cao hơn, phù hợp cho bề mặt có thẻ tab.</summary>
    TabbedWindow = 4,
}

/// <summary>
/// Thiết lập bo góc viền cửa sổ, tương ứng với <c>DWM_WINDOW_CORNER_PREFERENCE</c>.
/// </summary>
public enum WindowCornerPreference
{
    /// <summary>Do Windows quyết định dựa trên kiểu cửa sổ.</summary>
    Default = 0,

    /// <summary>Ép góc vuông.</summary>
    DoNotRound = 1,

    /// <summary>Ép sử dụng bán kính bo góc tiêu chuẩn.</summary>
    Round = 2,

    /// <summary>Ép sử dụng bán kính bo góc nhỏ hơn.</summary>
    RoundSmall = 3,
}