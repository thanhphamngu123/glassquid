using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Kết quả của một lần khởi chạy qua Shell.</summary>
/// <param name="Started">Shell đã chấp nhận yêu cầu này.</param>
/// <param name="ErrorCode">Mã lỗi do Shell trả về; 0 nếu thành công.</param>
/// <param name="ProcessId">ID của tiến trình được khởi chạy; 0 nếu Shell không trả về handle tiến trình.</param>
public sealed record ShellLaunchOutcome(bool Started, int ErrorCode, uint ProcessId)
{
    /// <summary>Tệp mục tiêu không tồn tại.</summary>
    public bool IsFileNotFound => ErrorCode == Shell32.SeErrFileNotFound;

    /// <summary>Không có chương trình nào được liên kết với loại tệp này.</summary>
    public bool IsNoAssociation => ErrorCode == Shell32.SeErrNoAssociation;

    /// <summary>Bị từ chối truy cập (bao gồm trường hợp mục tiêu cần nâng quyền nhưng người dùng đã hủy hộp thoại).</summary>
    public bool IsAccessDenied => ErrorCode is Shell32.SeErrAccessDenied or Shell32.SeErrCancelled;
}

/// <summary>
/// Mở đối tượng bằng Shell.
/// </summary>
/// <remarks>
/// Bốn loại mục (chương trình, lối tắt, thư mục, tệp) và trang web <strong>dùng chung đường dẫn này</strong>,
/// thay vì "chương trình đi qua <c>CreateProcess</c>, phần còn lại đi qua Shell". Có ba lý do:
/// <list type="bullet">
/// <item>Liên kết tệp, thư mục làm việc và nâng quyền UAC đều giao cho Shell, nhất quán với việc người dùng nhấp đúp trong Explorer;</item>
/// <item><c>CreateProcess</c> không thể khởi chạy chương trình cần nâng quyền, cũng không thể mở thư mục hay trang web;</item>
/// <item>Bớt một nhánh là bớt một bề mặt lỗi kiểu "một trong bốn loại có hành vi khác biệt".</item>
/// </list>
///
/// Đánh đổi là tham số phải do <strong>chúng ta</strong> ghép rồi truyền cho Shell. Tham số luôn được truyền nguyên dạng ban đầu,
/// không escape hay sắp xếp lại — bản thân nó vốn lấy từ <c>.lnk</c>, đã là chuỗi chuẩn bị sẵn cho dòng lệnh.
/// </remarks>
public static class ShellExecutor
{
    /// <summary>Mở một tệp, thư mục hoặc địa chỉ web.</summary>
    public static ShellLaunchOutcome Open(string target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ShellLaunchOutcome(false, Shell32.SeErrFileNotFound, 0);
        }

        var filePointer = Marshal.StringToHGlobalUni(target);
        var argumentsPointer = string.IsNullOrEmpty(arguments) ? 0 : Marshal.StringToHGlobalUni(arguments);
        var directoryPointer = string.IsNullOrWhiteSpace(workingDirectory) ? 0 : Marshal.StringToHGlobalUni(workingDirectory);

        try
        {
            var info = new Shell32.ShellExecuteInfo
            {
                Size = Marshal.SizeOf<Shell32.ShellExecuteInfo>(),

                // Cần handle tiến trình để lấy PID, phục vụ logic "đang chạy thì đưa lên trước";
                // không dùng hộp thoại lỗi của Shell, thông báo sẽ do chúng ta thống nhất câu chữ.
                //
                // Cố ý không thêm SEE_MASK_NOASYNC. Tài liệu khuyên "bên gọi không có vòng lặp thông điệp" nên thêm cờ này,
                // và lệnh thư viện ứng dụng ở dòng lệnh thực sự không có vòng lặp thông điệp nên nó từng được thêm vào —
                // nhưng nguyên nhân thực sự của lần "khởi chạy bị Shell từ chối" khi đó là số trường trong struct bên dưới bị sai
                // (xem mô tả của ShellExecuteInfo). Sau khi sửa đúng struct,
                // kiểm tra thực tế có hay không có bit này đều thành công, nên không giữ một công tắc chưa được kiểm chứng.
                Mask = Shell32.SeeMaskNoCloseProcess | Shell32.SeeMaskFlagNoUi,

                // Để trống Verb = để Shell tự quyết định hành động theo liên kết tệp.
                File = filePointer,
                Parameters = argumentsPointer,
                Directory = directoryPointer,
                Show = 1,
            };

            if (!Shell32.ShellExecuteEx(ref info))
            {
                return new ShellLaunchOutcome(false, Marshal.GetLastWin32Error(), 0);
            }

            var processId = info.Process == 0 ? 0 : Kernel32.GetProcessId(info.Process);

            if (info.Process != 0)
            {
                _ = Kernel32.CloseHandle(info.Process);
            }

            // Thư mục và địa chỉ web không tạo tiến trình mới nên handle rỗng là bình thường, vẫn tính là thành công.
            return new ShellLaunchOutcome(true, Shell32.SeErrSuccess, processId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ShellLaunchOutcome(false, -1, 0);
        }
        finally
        {
            FreeIfAllocated(filePointer);
            FreeIfAllocated(argumentsPointer);
            FreeIfAllocated(directoryPointer);
        }
    }

    private static void FreeIfAllocated(nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}