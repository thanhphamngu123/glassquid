using Glassquid.Core.Storage;

namespace Glassquid.AppServices.Tests;

/// <summary>
/// Thư mục mẫu dùng một lần, sẽ bị xóa khi kết thúc kiểm thử.
/// </summary>
/// <remarks>
/// Mỗi ca kiểm thử nhận một thư mục gốc riêng, nhờ vậy các thao tác như "xóa mục tiêu để tạo lỗi"
/// sẽ không ảnh hưởng đến các ca kiểm thử lân cận.
///
/// <para>
/// Tên thư mục cố tình có ký tự Unicode và dấu cách: một trong các yêu cầu nghiệm thu của giai đoạn 3 là đường dẫn có ký tự Unicode và dấu cách
/// phải được nhập và khởi chạy đúng; đặt mẫu trong thư mục thuần ASCII sẽ vô tình bỏ qua đúng phần dễ phát sinh lỗi nhất.
/// </para>
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"Glassquid-Kiểm thử mẫu-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Root);

        DataPaths = new AppPaths(Path.Combine(Root, "Thư mục dữ liệu"));
        DataPaths.EnsureCreated();
    }

    /// <summary>Thư mục gốc chứa tệp mẫu.</summary>
    public string Root { get; }

    /// <summary>Một thư mục dữ liệu có cấu trúc giống sản phẩm, được cô lập bên dưới thư mục mẫu.</summary>
    public AppPaths DataPaths { get; }

    public GlassquidDataStore CreateStore() => new(DataPaths);

    /// <summary>Tạo một tập lệnh thực thi có nội dung vô hại.</summary>
    public string CreateProgram(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "@echo off" + Environment.NewLine);
        return path;
    }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string name, string content = "Nội dung")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Thư mục tạm còn sót lại không đáng để làm hỏng cả lượt chạy kiểm thử.
        }
    }
}