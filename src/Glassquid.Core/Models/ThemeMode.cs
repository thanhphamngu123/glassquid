namespace Glassquid.Core.Models;

/// <summary>
/// Bảng phối màu mà UI nên sử dụng.
/// </summary>
/// <remarks>
/// Ba trạng thái, tương ứng với yêu cầu trong bản kế hoạch. Vì được lưu trong cài đặt, giá trị phải
/// giữ ổn định: <see cref="System"/> biểu thị "theo thiết lập Sáng/Tối của hệ thống".
/// </remarks>
public enum ThemeMode
{
    /// <summary>Theo chủ đề ứng dụng Windows và tự động điều chỉnh khi chủ đề thay đổi.</summary>
    System = 0,

    /// <summary>Luôn sáng.</summary>
    Light = 1,

    /// <summary>Luôn tối.</summary>
    Dark = 2,
}