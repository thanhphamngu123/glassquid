using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Hỏi shell xem "có nhận diện được mục tiêu này không" — không làm thay đổi màn hình nền, không mở bất kỳ cửa sổ nào.
/// </summary>
/// <remarks>
/// Những mục tiêu như <c>shell:RecycleBinFolder</c> không thể kiểm tra bằng cách hỏi "mục tiêu có tồn tại không" (nó không phải đường dẫn tệp),
/// và để xác minh nó thực sự không cần phải mở hẳn cửa sổ Thùng rác lên: <c>SHParseDisplayName</c> để shell tự phân giải,
/// nếu nhận diện được thì trả về một PIDL, không nhận diện được thì thất bại.
///
/// <para>
/// Một trong những lý do tồn tại của đường dẫn này là <strong>vệ sinh kiểm thử</strong>: trong kiểm thử nghiệm thu, việc "mở Thùng rác rồi đóng lại"
/// sẽ làm nháy một cửa sổ trên màn hình người dùng, còn "mục tiêu <c>shell:</c> gõ sai" sẽ khiến shell bật hộp thoại báo lỗi.
/// Kiểm tra phân giải không tạo cửa sổ, không bật hộp thoại, và khẳng định cùng một điều (shell có chấp nhận mục tiêu này hay không).
/// Thao tác mở thực sự sẽ được bao phủ bởi danh sách kiểm tra thủ công (nhấp vào ô lối vào Thùng rác).
/// </para>
/// </remarks>
public static class ShellTargets
{
    /// <summary>
    /// Shell có thể phân giải mục tiêu này hay không; trả về null nếu không thể phán đoán (bên gọi nên ghi nhận là "bỏ qua" thay vì đạt).
    /// </summary>
    public static bool? CanResolve(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        nint itemIdList = 0;

        try
        {
            var hr = Shell32.SHParseDisplayName(target, 0, out itemIdList, 0, out _);
            return hr == Ole32.SoK && itemIdList != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (itemIdList != 0)
            {
                // PIDL là cấu trúc do shell cấp, bắt buộc phải giải phóng bằng ILFree, không được dùng CoTaskMemFree.
                Shell32.ILFree(itemIdList);
            }
        }
    }
}