namespace Glassquid.Core.Storage;

/// <summary>
/// Toàn bộ vị trí mà sản phẩm này đọc và ghi trong thư mục dữ liệu người dùng của riêng mình.
/// </summary>
/// <remarks>
/// Tên thư mục này chỉ thuộc về chúng tôi. Tại đây không có bất kỳ đường dẫn nào trỏ tới vùng lưu trữ của sản phẩm tham chiếu
/// (<c>%APPDATA%\MacDock</c> hoặc dữ liệu người dùng Electron của Launchpad), do đó việc cài đặt
/// Glassquid sẽ không gây ảnh hưởng tới bản cài đặt MacDock hoặc Ym Launcher đã có.
/// Sự tách biệt này là yêu cầu bắt buộc trong bản kế hoạch,
/// đồng thời cũng là nguyên nhân từ một sự cố có thật trong bản cơ sở:
/// phiên bản phát triển lúc đó đã ghi đè mục tự khởi động của trình khởi chạy đã cài sẵn.
///
/// Tệp bố cục và thư viện ứng dụng được tách riêng có chủ ý: hỏng bố cục không nên
/// khiến người dùng mất danh sách ứng dụng của mình.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>Tên thư mục nằm trong thư mục dữ liệu ứng dụng chuyển vùng (Roaming AppData).</summary>
    public const string ProductFolderName = "Glassquid";

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Đường dẫn tuyệt đối tới thư mục dữ liệu sản phẩm.</summary>
    public string RootDirectory { get; }

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string AppLibraryFile => Path.Combine(RootDirectory, "apps.json");

    public string DockLayoutFile => Path.Combine(RootDirectory, "dock-layout.json");

    public string LauncherLayoutFile => Path.Combine(RootDirectory, "launcher-layout.json");

    /// <summary>Các tệp biểu tượng đã trích xuất và các tệp biểu tượng do người dùng tự chuẩn bị.</summary>
    public string IconCacheDirectory => Path.Combine(RootDirectory, IconCacheSubdirectory);

    /// <summary>
    /// Vị trí của thư mục bộ đệm biểu tượng tương đối so với <see cref="RootDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Kho lưu trữ sao lưu dùng chung một tầng thư mục con này để khi khôi phục có thể đưa về đúng vị trí cũ. Gom lại thành một hằng số,
    /// tránh việc tầng lưu trữ tự ghép ra một đường dẫn viết khác đi.
    /// </remarks>
    public const string IconCacheSubdirectory = "icons";

    /// <summary>Tiền tố chung của tên tệp biểu tượng tùy chỉnh do sản phẩm này quản lý.</summary>
    /// <remarks>
    /// Được <c>IconCache.ImportCustomIcon</c> sử dụng khi ghi ra đĩa;
    /// việc xác định "đường dẫn này có phải do chúng tôi quản lý hay không" cũng dựa vào nó, hai nơi phải thống nhất.
    /// </remarks>
    public const string ManagedCustomIconPrefix = "custom-";

    /// <summary>
    /// Xác định một đường dẫn có phải là biểu tượng tùy chỉnh do sản phẩm này quản lý (bản được IconCache sao chép vào icons/) hay không.
    /// </summary>
    /// <remarks>
    /// Chỉ kiểm tra tiền tố tên tệp. Đường dẫn lưu trên mục <strong>luôn luôn</strong> là bản do chúng tôi tự sao chép ra
    /// (tệp gốc do người dùng chọn không bao giờ được tham chiếu trực tiếp), nên khớp tiền tố là đủ để xác định.
    /// Đường dẫn trỏ đi nơi khác không thuộc quyền quản lý của chúng tôi, sửa đổi nó đồng nghĩa với phá hoại dữ liệu người dùng.
    /// </remarks>
    public static bool IsManagedCustomIconPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.GetFileName(path).StartsWith(ManagedCustomIconPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vị trí được quản lý của một tên tệp biểu tượng dưới thư mục dữ liệu chỉ định.</summary>
    public static string ManagedCustomIconPath(string dataRoot, string fileName)
        => Path.Combine(dataRoot, IconCacheSubdirectory, fileName);

    /// <summary>Bản sao lưu do người dùng chủ động tạo từ trung tâm cài đặt.</summary>
    public string BackupDirectory => Path.Combine(RootDirectory, "backup");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>
    /// Trạng thái được ghi trước khi thực hiện thay đổi hệ thống mang tính thử nghiệm (hiện chỉ có chặn thanh tác vụ),
    /// để có thể hoàn tác ở lần khởi động tiếp theo nếu gặp sự cố sập ứng dụng.
    /// </summary>
    public string RecoveryDirectory => Path.Combine(RootDirectory, "recovery");

    /// <summary>Thư mục dữ liệu của người dùng hiện tại: <c>%APPDATA%\Glassquid</c>.</summary>
    public static AppPaths ForCurrentUser()
    {
        // Acceptance data is portable and never points at the installed profile.
        return new AppPaths(Path.Combine(AppContext.BaseDirectory, "Data"));
    }

    /// <summary>Tạo cây thư mục. Có thể gọi lặp lại nhiều lần.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }

    /// <summary>
    /// Các <strong>tài liệu có thể phân tích cú pháp</strong> cấu thành bản sao lưu đầy đủ, được biểu diễn theo đường dẫn tương đối so với thư mục dữ liệu.
    /// </summary>
    /// <remarks>
    /// Cùng một đường dẫn tương đối vừa dùng làm vị trí bên trong tệp lưu trữ, vừa dùng làm vị trí đích khi khôi phục.
    /// Bốn tệp này là các tài liệu có tên cố định, có thể kiểm tra từng tệp trước khi khôi phục; biểu tượng tùy chỉnh thuộc một loại khác
    /// (<see cref="CustomIconRelativePaths"/>), là tệp nhị phân theo byte, không có cấu trúc tài liệu để kiểm tra.
    /// </remarks>
    public static IReadOnlyList<string> BackupDocumentRelativePaths() =>
    [
        "settings.json",
        "apps.json",
        "dock-layout.json",
        "launcher-layout.json",
    ];

    /// <summary>
    /// Các biểu tượng tùy chỉnh <strong>hiện có</strong> trong thư mục dữ liệu, được biểu diễn theo đường dẫn tương đối so với <see cref="RootDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Biểu tượng tùy chỉnh bắt buộc phải được đưa vào bản sao lưu.</b> Chúng không phải là bộ đệm có thể tái tạo mà được sao chép từ tệp người dùng đã chọn,
    /// tệp nguồn có thể đã không còn tồn tại từ lâu; nếu làm mất chúng, sau khi khôi phục các mục đó sẽ thực sự bị mất biểu tượng,
    /// trong khi lúc sao lưu lại vẫn báo "thành công".
    /// </para>
    /// <para>
    /// Cố ý <strong>chỉ</strong> mang theo nhóm <c>custom-*</c> này. Các tệp PNG đặt tên theo mã băm nguồn biểu tượng trong icons/
    /// có thể trích xuất lại bất cứ lúc nào từ nguồn, không tính là dữ liệu người dùng; các tệp khác trong thư mục lại càng không phải của chúng tôi,
    /// không nên bị sao chép kèm theo.
    /// </para>
    /// <para>
    /// Danh sách này mô tả <strong>những gì hiện đang có trên đĩa</strong>, nên chỉ có ý nghĩa với việc sao lưu và ảnh chụp nhanh trước khi khôi phục.
    /// Khi khôi phục cần lấy <strong>những gì có trong tệp lưu trữ</strong> làm chuẩn, xem <see cref="BackupService"/> —
    /// trước khi khôi phục bộ đệm thường đã bị dọn sạch, liệt kê theo hiện trạng sẽ không tìm thấy biểu tượng nào.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CustomIconRelativePaths()
    {
        var iconsDirectory = IconCacheDirectory;

        if (!Directory.Exists(iconsDirectory))
        {
            return [];
        }

        // Tên phải khớp với tên mà IconCache.ImportCustomIcon dùng khi ghi ra đĩa.
        return [.. Directory.GetFiles(iconsDirectory, "custom-*")
            .Select(file => Path.GetRelativePath(RootDirectory, file))];
    }
}