using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Đọc đường dẫn thực tế của các thư mục hệ thống đã biết.
/// </summary>
/// <remarks>
/// Dùng <c>SHGetKnownFolderPath</c> thay vì ghép biến môi trường kiểu <c>%APPDATA%</c>:
/// các thư mục này có thể bị chuyển hướng (chính sách doanh nghiệp, OneDrive tiếp quản, hoặc người dùng tự di chuyển),
/// ghép chuỗi trên máy đã bị chuyển hướng sẽ trỏ tới một nơi không tồn tại.
/// </remarks>
public static class KnownFolders
{
    /// <summary>Thư mục chương trình trong menu Start cho tất cả người dùng.</summary>
    public static string? CommonPrograms => Resolve(Shell32.FolderIdCommonPrograms);

    /// <summary>Thư mục chương trình trong menu Start cho người dùng hiện tại.</summary>
    public static string? Programs => Resolve(Shell32.FolderIdPrograms);

    /// <summary>Trả về null khi không lấy được, để bên gọi tự quyết định đây có phải là vấn đề hay không.</summary>
    private static string? Resolve(Guid folderId)
    {
        nint pointer = 0;

        try
        {
            var hr = Shell32.SHGetKnownFolderPath(in folderId, 0, 0, out pointer);
            if (hr != Ole32.SoK || pointer == 0)
            {
                return null;
            }

            // Giá trị trả về là chuỗi UTF-16 kết thúc bằng ký tự null.
            var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // Vùng nhớ này được cấp phát bằng CoTaskMemAlloc, bắt buộc phải giải phóng bằng hàm tương ứng,
            // nếu không sẽ gây lỗi giải phóng sai heap. Cũng chính vì bắt buộc phải giải phóng nên không thể return trực tiếp chuỗi vừa đọc.
            if (pointer != 0)
            {
                Ole32.CoTaskMemFree(pointer);
            }
        }
    }
}