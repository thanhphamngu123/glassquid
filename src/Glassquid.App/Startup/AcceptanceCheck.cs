namespace Glassquid.App.Startup;

/// <summary>Kết luận của một bước kiểm tra nghiệm thu.</summary>
/// <remarks>
/// <strong>"Bỏ qua" và "Đạt" là hai việc khác nhau, bắt buộc phải thống kê riêng.</strong>
/// Bỏ qua nghĩa là máy này thiếu điều kiện đó (không có màn hình phụ, desktop người dùng đã chiếm cửa sổ đích,
/// thiếu ứng dụng đã đóng gói...), nó không chứng minh điều gì và cũng không được tính vào số lượng đạt —
/// ghi nhận nó là đạt chẳng khác nào lấy việc thiếu môi trường để giả làm kết quả xác minh.
/// </remarks>
internal enum CheckStatus
{
    /// <summary>Khẳng định đã chạy và đạt.</summary>
    Passed = 0,

    /// <summary>Khẳng định đã chạy nhưng không đạt.</summary>
    Failed = 1,

    /// <summary>Không đủ điều kiện nên chưa chạy. Không tính là đạt cũng không tính là thất bại.</summary>
    Skipped = 2,
}