using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>Giao diện mà bên gọi mong muốn cửa sổ hiển thị.</summary>
public sealed class WindowAppearanceRequest
{
    /// <summary>Chất liệu muốn yêu cầu; chọn <see cref="SystemBackdropType.None"/> để tự vẽ.</summary>
    public SystemBackdropType Backdrop { get; set; } = SystemBackdropType.None;

    public WindowCornerPreference Corners { get; set; } = WindowCornerPreference.Round;

    /// <summary>Vùng ngoài vùng khách (non-client area) tối màu.</summary>
    public bool DarkMode { get; set; }

    /// <summary>
    /// Khi bản thân cửa sổ dùng 32 bit mỗi pixel, việc mở rộng khung viền vào vùng khách
    /// giúp DWM kết hợp chất liệu nền xuống bên dưới nội dung của chúng ta.
    /// </summary>
    public bool ExtendFrameIntoClientArea { get; set; }
}

/// <summary>Ghi nhận trung thực những gì hệ thống thực sự đã chấp nhận.</summary>
/// <remarks>
/// Yêu cầu chất liệu nền có thể bị từ chối — do phiên bản không phù hợp, hiệu ứng trong suốt bị tắt, phiên làm việc từ xa,
/// hoặc chế độ kết hợp không hỗ trợ chất liệu. Giai đoạn 2 phải báo cáo trên từng máy
/// cuối cùng đã xảy ra những mức hạ cấp nào, vì vậy kết quả cần được trả về thay vì nuốt mất.
/// </remarks>
public readonly record struct WindowAppearanceResult(
    bool BackdropApplied,
    bool CornersApplied,
    bool DarkModeApplied)
{
    /// <summary>Là true khi chất liệu được yêu cầu thực sự hiển thị trên màn hình.</summary>
    public bool IsFullyApplied => BackdropApplied && CornersApplied && DarkModeApplied;
}

/// <summary>
/// Áp dụng yêu cầu giao diện lên handle cửa sổ thông qua các tính năng DWM đã được ghi tài liệu.
/// </summary>
public static class WindowAppearance
{
    /// <summary>
    /// Áp dụng yêu cầu, bỏ qua các phần mà phiên bản Windows này không hỗ trợ.
    /// Không bao giờ ném ngoại lệ: gặp thuộc tính không được hỗ trợ trên phiên bản cũ là kết quả nằm trong dự kiến.
    /// </summary>
    public static WindowAppearanceResult Apply(nint hwnd, WindowAppearanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (hwnd == 0)
        {
            return default;
        }

        var backdropApplied = false;

        if (request.Backdrop != SystemBackdropType.None && WindowsCapabilities.SupportsSystemBackdrop)
        {
            backdropApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaSystemBackdropType,
                (int)request.Backdrop);
        }

        if (backdropApplied && request.ExtendFrameIntoClientArea)
        {
            // Chất liệu được kết hợp vào khung viền, nên vùng khách phải là
            // một phần của khung viền thì chất liệu mới xuyên qua được.
            DwmApi.TryExtendFrameIntoClientArea(hwnd, -1, -1, -1, -1);
        }

        var cornersApplied = false;
        if (WindowsCapabilities.SupportsWindowCornerPreference)
        {
            cornersApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaWindowCornerPreference,
                (int)request.Corners);
        }

        var darkApplied = false;
        if (WindowsCapabilities.SupportsImmersiveDarkMode)
        {
            darkApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaUseImmersiveDarkMode,
                request.DarkMode ? 1 : 0);
        }

        return new WindowAppearanceResult(backdropApplied, cornersApplied, darkApplied);
    }
}