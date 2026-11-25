using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Điểm vào kích hoạt COM trong ole32.
/// </summary>
/// <remarks>
/// Việc phân giải lối tắt cần tạo đối tượng <c>ShellLink</c>, vốn là thành phần COM và chỉ có thể lấy qua
/// <c>CoCreateInstance</c>. Ở đây không tham chiếu coclass <c>ShellLink</c>,
/// do đó không phụ thuộc vào việc COM interop tích hợp suy luận thư viện kiểu —
/// chỉ cần giao con trỏ thô nhận được cho lớp bọc giao diện được sinh mã nguồn là đủ.
/// </remarks>
internal static partial class Ole32
{
    /// <summary>Máy chủ nội tiến trình. Đối tượng lối tắt nằm ngay trong shell32.</summary>
    internal const uint ClsCtxInprocServer = 0x1;

    /// <summary>Tệp phương tiện. <c>IPersistFile::Load</c> dùng hằng số này để biểu thị việc tải từ tệp.</summary>
    internal const uint StgmRead = 0x00000000;

    internal const int SoK = 0;

    /// <summary><c>E_INSUFFICIENTBUFFER</c>: bộ đệm do bên gọi cung cấp không chứa đủ kết quả.</summary>
    internal const int EInsufficientBuffer = unchecked((int)0x8007007A);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    /// <summary>
    /// Giải phóng bộ nhớ do bộ cấp phát tác vụ COM trả về.
    /// </summary>
    /// <remarks>
    /// <c>SHGetKnownFolderPath</c> dùng <c>CoTaskMemAlloc</c> để cấp phát chuỗi kết quả,
    /// quy định phải dùng hàm này để giải phóng; giải phóng bằng <c>Marshal.FreeHGlobal</c> là sai cặp hàm,
    /// sẽ làm hỏng heap.
    /// </remarks>
    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(nint memory);
}