using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Áp dụng hiệu ứng làm mờ nền Acrylic cho cửa sổ.
/// </summary>
/// <remarks>
/// <b>Tại sao dùng API không công khai.</b> Thử nghiệm thực tế ở Giai đoạn 2 xác nhận: chất liệu nền DWM có tài liệu chính thức
/// (<c>DWMWA_SYSTEMBACKDROP_TYPE</c>) <strong>hoàn toàn không hiển thị</strong> trên cửa sổ WPF —
/// lệnh gọi thuộc tính trả về thành công, nhưng màn hình lại đen kịt.
///
/// Nguyên nhân là cửa sổ WPF mặc định không phải cửa sổ phân lớp (layered window), vùng khách (client area) kết xuất vào một bề mặt chuyển hướng,
/// và các điểm ảnh trong suốt trên bề mặt đó biến thành màu đen đục, che lấp toàn bộ chất liệu do DWM vẽ.
/// Đo thực tế bốn cấu hình (Mica / mở rộng viền / Acrylic / có tô nền) đều cho điểm ảnh trung tâm là <c>#000000</c>,
/// trong khi cửa sổ phân lớp kết hợp API này thu được hiệu ứng làm mờ nền thực sự (đo thực tế <c>#222222</c>,
/// khớp chính xác với giá trị tính toán của "màu tối 60% phủ lên màn hình nền đã làm mờ").
///
/// Vì vậy muốn có hiệu ứng kính thật trên WPF thì chỉ có con đường này. Đây là API không công khai,
/// được dùng rộng rãi và ổn định từ Windows 10 trở đi, nhưng đã là không công khai thì bắt buộc phải:
/// <list type="bullet">
/// <item>Khi gọi thất bại thì lặng lẽ lùi về tự tô nền, tuyệt đối không giả định luôn khả dụng;</item>
/// <item>Ghi lại kết quả kiểm chứng theo số bản dựng (build) Windows, không tuyên bố hỗ trợ các bản dựng chưa kiểm chứng.</item>
/// </list>
/// </remarks>
public static class WindowAcrylic
{
    private const int WcaAccentPolicy = 19;

    /// <summary>Làm mờ Acrylic: làm mờ thực sự nội dung phía sau cửa sổ.</summary>
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>Chỉ làm mờ, không pha trộn tông màu.</summary>
    private const int AccentEnableBlurBehind = 3;

    /// <summary>
    /// Áp dụng hiệu ứng Acrylic cho cửa sổ.
    /// </summary>
    /// <param name="window">Handle của cửa sổ đích.</param>
    /// <param name="tintAlpha">Độ mờ đục của lớp phủ tông màu, 0 là trong suốt hoàn toàn.</param>
    /// <param name="red">Thành phần màu đỏ của tông màu.</param>
    /// <param name="green">Thành phần màu xanh lục của tông màu.</param>
    /// <param name="blue">Thành phần màu xanh dương của tông màu.</param>
    /// <returns>Hệ thống có chấp nhận thiết lập này hay không.</returns>
    /// <remarks>
    /// Cố ý chỉ nhận ba byte R/G/B, không nhận <c>Color</c> của WPF: assembly này không tham chiếu WPF,
    /// nhờ vậy tầng tương tác (interop) có thể được sử dụng và kiểm thử ngay cả khi không có framework giao diện.
    /// </remarks>
    public static bool TryApply(nint window, byte tintAlpha, byte red, byte green, byte blue)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            // 2 nghĩa là mở rộng vùng làm mờ ra bốn phía, tránh xuất hiện viền sáng chưa được làm mờ ở các cạnh.
            AccentFlags = 2,
            // GradientColor có thứ tự **ABGR**, không phải ARGB. Viết ngược sẽ làm đỏ và xanh dương bị tráo đổi.
            GradientColor = (tintAlpha << 24) | (blue << 16) | (green << 8) | red,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // API không công khai có thể biến mất trong các bản dựng tương lai, đây là lỗi nằm trong dự kiến.
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Gỡ bỏ hiệu ứng Acrylic khỏi cửa sổ.</summary>
    public static bool TryRemove(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = 0, // ACCENT_DISABLED
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Các số bản dựng Windows đã được <strong>kiểm chứng thực tế</strong> là dùng được Acrylic.
    /// </summary>
    /// <remarks>
    /// Đây là một danh sách cho phép (allowlist), không phải phép kiểm tra "lớn hơn hoặc bằng một phiên bản nào đó". Sự khác biệt giữa hai cách này rất quan trọng:
    ///
    /// <list type="bullet">
    /// <item>
    /// Giao diện này không công khai, Microsoft không cam kết nó vẫn tồn tại hay giữ nguyên hành vi ở các bản dựng tương lai. Dùng kiểu kiểm tra ">= 22621"
    /// đồng nghĩa với việc đưa ra cam kết chưa được kiểm chứng cho <strong>mọi bản dựng tương lai</strong>.
    /// </item>
    /// <item>
    /// Danh sách cho phép thì ngược lại: các bản dựng chưa có tên sẽ đi theo nhánh hạ cấp tự vẽ — giao diện vẫn dùng bình thường, chỉ là không có làm mờ nền.
    /// Điều này nhất quán với yêu cầu của bản kế hoạch đối với tính năng thử nghiệm ("chỉ mở trên các bản dựng đã kiểm chứng thực tế; bản dựng chưa rõ thì báo không hỗ trợ").
    /// </item>
    /// </list>
    ///
    /// <b>Chỉ liệt kê các bản dựng có bằng chứng.</b> Thêm một số bản dựng chỉ vì "nó là một phiên bản nào đó của Windows 11"
    /// chẳng khác nào đổi cách viết của ">= 22621" — vẫn là cam kết cho bản dựng chưa kiểm chứng. Trước khi thêm một số bản dựng,
    /// bắt buộc phải thực hiện thử nghiệm đối chứng trên <strong>chính máy đó</strong> và lưu lại kết quả:
    ///
    /// <list type="number">
    /// <item>Tạo một cửa sổ phân lớp, áp dụng Acrylic, chụp màn hình lấy điểm ảnh ở tâm cửa sổ;</item>
    /// <item>Cùng cửa sổ đó, cùng vị trí đó, không áp dụng Acrylic rồi lấy lại một lần nữa;</item>
    /// <item>Giá trị điểm ảnh và cảm quan giữa hai lần phải khác biệt rõ rệt (dữ liệu đối chứng trên máy này với bản dựng 26100 xem tại
    /// <c>docs/GiaiDoan2-BaoCaoKhaNangHeThong.md</c> mục 3);</item>
    /// <item>Ghi số bản dựng, ngày tháng và kết quả đối chứng vào báo cáo đó rồi mới thêm vào danh sách.</item>
    /// </list>
    /// </remarks>
    private static readonly int[] VerifiedBuilds =
    [
        // 26100 = Windows 11 24H2 —— Đã kiểm chứng thực tế trên máy này (2026-09-18, Giai đoạn 2).
        // Dữ liệu đối chứng: cửa sổ phân lớp + Acrylic cho ra #222222, khớp chính xác với giá trị tính toán
        // "màu tối 60% phủ lên màn hình nền đã làm mờ"; khi không áp dụng Acrylic, cùng cửa sổ đó nhìn thấy chi tiết hình nền chưa làm mờ.
        26100,
    ];

    /// <summary>
    /// Máy hiện tại có nằm trong danh sách bản dựng đã kiểm chứng hay không.
    /// </summary>
    /// <remarks>
    /// Trả về false không phải lỗi, chỉ là "chưa được kiểm chứng", bên gọi dựa vào đó để chuyển sang hạ cấp tự vẽ.
    /// </remarks>
    public static bool IsSupportedOnThisBuild =>
        Array.IndexOf(VerifiedBuilds, WindowsCapabilities.BuildNumber) >= 0;

    /// <summary>
    /// Lý do bản dựng hiện tại được hoặc không được Acrylic hỗ trợ, dùng cho đầu ra chẩn đoán.
    /// </summary>
    public static string DescribeSupport()
    {
        var build = WindowsCapabilities.BuildNumber;

        return IsSupportedOnThisBuild
            ? $"Build {build} nằm trong danh sách đã kiểm chứng, sử dụng làm mờ nền Acrylic"
            : $"Build {build} chưa được kiểm chứng, chuyển sang bảng tự vẽ hạ cấp (đã kiểm chứng: {string.Join(", ", VerifiedBuilds)})";
    }

    /// <summary>
    /// Biến thể chỉ làm mờ và không phủ tông màu, dùng cho các trường hợp cần tự kiểm soát hoàn toàn tông màu.
    /// </summary>
    public static bool TryApplyBlurOnly(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var policy = new AccentPolicy
        {
            AccentState = AccentEnableBlurBehind,
            AccentFlags = 2,
            GradientColor = 0,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return Composition32.SetWindowCompositionAttribute(window, ref data) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    internal int Attribute;
    internal nint Data;
    internal int SizeOfData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    internal int AccentState;
    internal int AccentFlags;
    internal int GradientColor;
    internal int AnimationId;
}

internal static partial class Composition32
{
    [LibraryImport("user32.dll")]
    internal static partial int SetWindowCompositionAttribute(nint window, ref WindowCompositionAttributeData data);
}