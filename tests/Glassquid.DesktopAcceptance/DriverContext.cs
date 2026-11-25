using System.IO;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Ngữ cảnh dùng chung cho một lần chạy trình điều khiển: đường dẫn tạo phẩm, thư mục làm việc và báo cáo.
/// </summary>
/// <remarks>
/// Tất cả tệp tạm đều nằm trong <c>%TEMP%\Glassquid-trình-điều-khiển-desktop-&lt;duy-nhất-lần-này&gt;</c>,
/// không đặt bất cứ thứ gì vào Desktop, Documents hay thư mục dữ liệu sản phẩm.
/// </remarks>
internal sealed class DriverContext
{
    internal DriverContext(string productPath, string fixturePath, string workRoot, Report report)
    {
        ProductPath = productPath;
        FixturePath = fixturePath;
        WorkRoot = workRoot;
        Report = report;
    }

    internal string ProductPath { get; }

    internal string FixturePath { get; }

    internal string WorkRoot { get; }

    internal Report Report { get; }

    /// <summary>Thư mục làm việc riêng cho lượt chạy này. Mỗi lượt đều đổi sang thư mục mới để tránh phần sót lại của lượt trước ảnh hưởng đến lượt sau.</summary>
    internal string NewWorkDirectory(string label)
    {
        var directory = Path.Combine(WorkRoot, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string DefaultWorkRoot() =>
        Path.Combine(Path.GetTempPath(), WorkRootPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Tiền tố chung của tất cả thư mục làm việc thuộc trình điều khiển này.
    /// </summary>
    /// <remarks>
    /// Hàng rào bảo vệ của thư viện ứng dụng dùng nó để phân biệt "tệp do trình điều khiển tự tạo" với "dữ liệu thật của người dùng".
    /// Lưu ý đây là tiền tố chứ không phải tên thư mục của riêng lần chạy này: các mục do lượt trước để lại cũng nằm dưới cùng tiền tố này, thuộc phần lịch sử có thể chấp nhận.
    /// </remarks>
    internal const string WorkRootPrefix = "Glassquid-trình-điều-khiển-desktop-";

    /// <summary>
    /// Tiền tố <strong>tuyệt đối</strong> cho thư mục làm việc của trình điều khiển này, dùng cho hàng rào thư viện ứng dụng xác định quyền sở hữu.
    /// </summary>
    /// <remarks>
    /// Bắt buộc phải là đường dẫn tuyệt đối: mục tiêu trong thư viện ghi đường dẫn đầy đủ, dùng tiền tố tương đối gọi StartsWith sẽ không bao giờ khớp,
    /// dẫn đến việc các tệp do chính trình điều khiển tạo ở lượt trước bị coi là dữ liệu thật của người dùng (đã từng gặp lỗi này khi kiểm thử thực tế).
    /// </remarks>
    internal static string WorkRootFamily => Path.Combine(Path.GetTempPath(), WorkRootPrefix);
}