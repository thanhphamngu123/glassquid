using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Các điểm vào của console và luồng trong kernel32.
/// </summary>
/// <remarks>
/// Ban đầu được khai báo ở cuối tệp <c>ConsoleAttach</c>, vì lúc đó chỉ có phần gắn console sử dụng.
/// Hook sự kiện toàn màn hình lại cần điểm vào liên quan đến luồng, nên được gom về thư mục interop —
/// đặt cùng các khai báo Win32 còn lại theo quy ước của thư mục này.
/// </remarks>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleOutputCP")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleOutputCP(uint codePage);

    /// <summary>
    /// ID cấp hệ thống của luồng hiện tại.
    /// </summary>
    /// <remarks>
    /// Vòng lặp thông điệp chứa hook sự kiện cần giá trị này: chỉ có thể kết thúc vòng lặp bằng cách gửi <c>WM_QUIT</c> tới luồng đó,
    /// mà đích nhận là ID luồng cấp hệ thống chứ không phải ID luồng được quản lý.
    /// </remarks>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    // ---- Tiến trình (xác định quyền sở hữu cửa sổ của giai đoạn 3) ----

    /// <summary>Chỉ đủ để truy vấn đường dẫn ảnh thực thi và mã thoát, yêu cầu quyền thấp nhất.</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Mở tiến trình.
    /// </summary>
    /// <remarks>
    /// Dùng <see cref="ProcessQueryLimitedInformation"/> thay vì <c>PROCESS_QUERY_INFORMATION</c>:
    /// giá trị trước vẫn lấy được thông tin đối với tiến trình **đã nâng quyền**, còn giá trị sau sẽ bị từ chối truy cập.
    /// Việc xác định "cửa sổ này có thuộc về một mục trong thư viện ứng dụng hay không" đúng lúc cần xem tiến trình xuyên qua các cấp quyền.
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>Truy vấn đường dẫn đầy đủ của ảnh tiến trình. <paramref name="size"/> nhận dung lượng bộ đệm đầu vào và trả về độ dài thực tế.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);

    /// <summary>Kiểm tra tiến trình có còn đang chạy hay không.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    internal const uint WaitTimeout = 0x00000102;

    /// <summary>Truy vấn ID tiến trình từ handle tiến trình.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);

    /// <summary>
    /// Truy vấn định danh ứng dụng (AppUserModelID) của tiến trình đã đóng gói.
    /// </summary>
    /// <remarks>
    /// Chỉ có hiệu lực với tiến trình có định danh gói; tiến trình Win32 thông thường trả về mã lỗi
    /// <c>APPMODEL_ERROR_NO_APPLICATION</c> (0x80270112), đây chính là ranh giới cần đầu dò đo kiểm thực tế để xác nhận.
    /// <paramref name="length"/> truyền vào dung lượng bộ đệm (số ký tự), trả ra độ dài thực tế (bao gồm ký tự null kết thúc);
    /// khi dung lượng không đủ sẽ trả về <c>ERROR_INSUFFICIENT_BUFFER</c> và ghi lại độ dài cần thiết.
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "GetApplicationUserModelId", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetApplicationUserModelId(nint process, ref uint length, [Out] char[] buffer);

    /// <summary><c>GetApplicationUserModelId</c>: tiến trình đích không có định danh ứng dụng (không phải ứng dụng đóng gói).</summary>
    internal const int AppModelErrorNoApplication = unchecked((int)0x80270112);
}