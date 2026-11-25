using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>
/// Nếu tiến trình này được khởi chạy từ một console, hãy gắn vào console đó.
/// </summary>
/// <remarks>
/// WinExe không có console riêng, nên văn bản ghi bằng <c>Console.WriteLine</c> từ tiến trình cửa sổ
/// sẽ không đi đâu cả. Việc gắn vào console của tiến trình cha giúp chế độ dòng lệnh như
/// <c>--selftest</c> có thể hoạt động trong terminal hoặc script dựng.
///
/// Khởi chạy từ File Explorer nghĩa là hoàn toàn không có console cha; đây là trường hợp bình thường,
/// chỉ trả về false thông thường chứ không coi là lỗi.
/// </remarks>
public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    /// <summary>
    /// Trả về true khi đầu ra chuẩn hiện có thể đi tới console của tiến trình cha.
    /// </summary>
    /// <remarks>
    /// Kết quả được lưu đệm: gắn hai lần là vô nghĩa, và lần gọi thứ hai sẽ thất bại
    /// do tiến trình đã được gắn sẵn.
    /// </remarks>
    public static bool TryAttachToParentConsole()
    {
        if (Field is not null)
        {
            return Field.Value;
        }

        var attached = false;

        try
        {
            attached = Kernel32.AttachConsole(AttachParentProcess);

            if (attached)
            {
                // Tiến trình này ghi đầu ra theo UTF-8. Nếu console vẫn ở trang mã ANSI hệ thống,
                // đầu ra tiếng Trung sẽ bị hiển thị lỗi, nên cần chuyển console sang UTF-8.
                Kernel32.SetConsoleOutputCP(Utf8CodePage);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            attached = false;
        }

        Field = attached;
        return attached;
    }

    /// <summary>UTF-8. Trang mã khớp với bảng mã mà ứng dụng dùng để ghi.</summary>
    private const uint Utf8CodePage = 65001;

    // Không cần cân nhắc [ThreadStatic]: một tiến trình chỉ có một console, và giá trị này
    // không thay đổi sau lần gọi đầu tiên.
    private static bool? Field;
}