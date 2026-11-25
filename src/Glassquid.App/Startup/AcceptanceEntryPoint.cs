using System.IO;
using System.Text;
using Glassquid.Core.Storage;
using Glassquid.Platform.Storage;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Glassquid.AppServices.Tests")]

namespace Glassquid.App.Startup;

/// <summary>Điểm vào cô lập duy nhất cho tác vụ nghiệm thu: lấy ranh giới bảo vệ thư mục gốc dữ liệu thật trước, rồi mới tạo không gian làm việc độc quyền.</summary>
internal static class AcceptanceEntryPoint
{
    public static bool IsValidIsolatedRun(CommandLineOptions options) =>
        (options.SelfTest ^ options.Diagnose) && options.AcceptanceDataRoot is { Length: > 0 }
        && options.AcceptanceReport is { Length: > 0 }
        && options.Verb == AppServiceVerb.None && !options.Exit
        && !options.ShowLauncher && !options.ShowSettings && options.Sample == AppearanceSample.None
        && !options.Help && options.Unknown.Count == 0;

    public static int Run(AppPaths realPaths, string dataRoot, IReadOnlyList<AcceptanceOutput> outputs,
        Func<AcceptanceWorkspace, int> action)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(action);

        var workspace = Prepare(realPaths, dataRoot, outputs);
        return workspace is null ? 2 : Run(workspace, action);
    }

    /// <summary>Chẩn đoán bất đồng bộ cũng phải lấy được không gian làm việc trước khi đưa vào hàng đợi giao diện; nếu bị từ chối thì không chạy chẩn đoán.</summary>
    public static AcceptanceWorkspace? Prepare(AppPaths realPaths, string dataRoot, IReadOnlyList<AcceptanceOutput> outputs)
    {
        try
        {
            return AcceptanceWorkspace.Create(new AcceptanceRequest(realPaths.RootDirectory, dataRoot, outputs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Khởi tạo cô lập nghiệm thu thất bại (đường dẫn báo cáo hoặc thư mục dữ liệu bị từ chối): {ex.Message}");
            return null;
        }
    }

    public static int Run(AcceptanceWorkspace workspace, Func<AcceptanceWorkspace, int> action)
    {
        try
        {
            using (workspace) return action(workspace);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tác vụ nghiệm thu hoặc việc ghi/đóng đầu ra bắt buộc thất bại: {ex.Message}");
            return 1;
        }
    }

    public static void WriteText(Stream output, string text)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
    }
}
