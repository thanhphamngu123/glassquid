using System.Runtime.InteropServices;

namespace Glassquid.Platform;

/// <summary>
/// Bản cài đặt Windows này hỗ trợ những khả năng nào.
/// </summary>
/// <remarks>
/// Chỉ thăm dò một lần và lưu vào bộ nhớ đệm. Mọi tính năng phụ thuộc vào phiên bản Windows cụ thể
/// đều hỏi tại đây, thay vì tự kiểm tra phiên bản cục bộ,
/// nhờ đó các tổ hợp không được hỗ trợ sẽ báo cáo trạng thái nhất quán,
/// và quy tắc "phiên bản không hỗ trợ thì nêu rõ sự thật thay vì đoán" chỉ cần thực thi ở một nơi.
///
/// Phiên bản được lấy từ <c>RtlGetVersion</c> thay vì <see cref="Environment.OSVersion"/>,
/// vì giá trị sau có thể bị shim tương thích ứng dụng ghi đè,
/// trong khi ý nghĩa của các kiểm tra này chính là xác nhận đúng số hiệu phiên bản.
/// </remarks>
public static partial class WindowsCapabilities
{
    /// <summary>Phiên bản đầu tiên của Windows 11. Bắt đầu hỗ trợ bo góc cửa sổ.</summary>
    private const int Windows11Build = 22000;

    /// <summary>Windows 11 22H2. Bắt đầu hỗ trợ <c>DWMWA_SYSTEMBACKDROP_TYPE</c>.</summary>
    private const int SystemBackdropBuild = 22621;

    /// <summary>Phiên bản bắt đầu hỗ trợ chính thức thuộc tính cửa sổ chế độ tối.</summary>
    private const int ImmersiveDarkModeBuild = 18985;

    /// <summary>Nhánh phát hành này yêu cầu tối thiểu Windows 11 21H2.</summary>
    private const int MinimumSupportedBuild = 22000;

    static WindowsCapabilities()
    {
        var (major, minor, build) = ReadVersion();
        OsVersion = new Version(major, minor, build);
        BuildNumber = build;
    }

    /// <summary>Phiên bản thực tế của hệ điều hành đang chạy, ví dụ 10.0.26100.</summary>
    public static Version OsVersion { get; }

    public static int BuildNumber { get; }

    /// <summary>Ngưỡng hệ thống tối thiểu của sản phẩm; không đặt giới hạn trên cho các Build tương lai.</summary>
    public static bool IsSupported => IsProductBuildSupported(BuildNumber);
    public static bool IsProductBuildSupported(int build) => build >= MinimumSupportedBuild;

    public static bool IsWindows11OrGreater => BuildNumber >= Windows11Build;

    /// <summary>
    /// DWM có phản hồi yêu cầu chất liệu nền hệ thống hay không.
    /// Khi là false, cửa sổ phải tự vẽ bảng nền bán trong suốt.
    /// </summary>
    public static bool SupportsSystemBackdrop => BuildNumber >= SystemBackdropBuild;

    /// <summary>Có thể yêu cầu DWM bo tròn góc cửa sổ hay không.</summary>
    public static bool SupportsWindowCornerPreference => BuildNumber >= Windows11Build;

    /// <summary>Vùng không thuộc khách (non-client area) có thể theo chủ đề ứng dụng hay không.</summary>
    public static bool SupportsImmersiveDarkMode => BuildNumber >= ImmersiveDarkModeBuild;

    /// <summary>
    /// Tóm tắt dễ đọc dùng cho trang "Giới thiệu" của cửa sổ Cài đặt và mục chẩn đoán.
    /// </summary>
    public static string Describe()
    {
        var backdrop = SupportsSystemBackdrop ? "Có hỗ trợ chất liệu nền hệ thống" : "Không hỗ trợ chất liệu nền hệ thống (đang dùng bảng nền tự vẽ)";
        return $"Windows {OsVersion} (Build {BuildNumber}) — {backdrop}";
    }

    private static (int Major, int Minor, int Build) ReadVersion()
    {
        try
        {
            var info = new RtlOsVersionInfo
            {
                Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>(),
            };

            // Trả về STATUS_SUCCESS (0) trên mọi phiên bản Windows được hỗ trợ.
            // Giá trị trả về khác 0 nghĩa là bản thân lệnh gọi đã thất bại,
            // khi đó giá trị dự phòng từ managed vẫn tốt hơn việc báo một số phiên bản vô lý.
            if (NtDll.RtlGetVersion(ref info) == 0)
            {
                return ((int)info.MajorVersion, (int)info.MinorVersion, (int)info.BuildNumber);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Tiếp tục dùng giá trị dự phòng từ managed.
        }

        var fallback = Environment.OSVersion.Version;
        return (fallback.Major, fallback.Minor, fallback.Build);
    }

    /// <summary>
    /// Tương ứng với <c>RTL_OSVERSIONINFOW</c>. Bộ đệm ký tự có độ dài cố định giúp struct giữ được khả năng marshal trực tiếp (blittable),
    /// đó cũng là lý do bên dưới có thể dùng source-generated marshalling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct RtlOsVersionInfo
    {
        public uint Size;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char ServicePack[128];
    }

    private static partial class NtDll
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int RtlGetVersion(ref RtlOsVersionInfo versionInfo);
    }
}
