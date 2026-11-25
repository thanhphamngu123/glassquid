using System.IO;
using System.Text;
using Glassquid.Platform;

namespace Glassquid.App.Startup;

/// <summary>
/// Đưa văn bản báo cáo đến nơi mà người hoặc tập lệnh có thể đọc được.
/// </summary>
/// <remarks>
/// Tiến trình chủ được biên dịch theo WinExe nên bản thân không có console. Khi tiến trình được khởi chạy từ terminal,
/// chúng ta gắn vào console của terminal đó; trong mọi trường hợp, cùng một nội dung văn bản sẽ được ghi
/// vào tệp trong thư mục nhật ký riêng của sản phẩm — đây chính là lý do giúp tự kiểm tra hoạt động được ngay cả
/// trong các tập lệnh build hoàn toàn không nhìn thấy console.
/// </remarks>
internal static class ReportWriter
{
    public static void Write(string text, string? logFilePath)
    {
        if (ConsoleAttach.TryAttachToParentConsole())
        {
            // Việc gắn mượn console của tiến trình cha, nhưng Console.Out vẫn trỏ tới luồng cũ
            // không dùng được nữa, vì vậy bắt buộc phải mở lại. Không kèm BOM: đây là đầu ra console chứ không phải
            // tệp tin, ký tự đánh dấu thứ tự byte sẽ hiển thị thành ký tự rác chen vào.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            stdout.Write(text);
            stdout.Write(Environment.NewLine);
            stdout.Flush();
        }

        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(logFilePath, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Việc ghi báo cáo tuyệt đối không được trở thành mắt xích khiến tự kiểm tra thất bại.
        }
    }
}