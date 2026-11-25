using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

/// <summary>
/// Thư mục dữ liệu dùng một lần, sẽ bị xóa khi kết thúc kiểm thử.
/// </summary>
/// <remarks>
/// Mỗi bài kiểm thử liên quan đến lưu trữ sẽ có thư mục riêng.
/// Nếu dùng chung một thư mục, bài kiểm thử cố tình làm hỏng một tệp sẽ ảnh hưởng đến các ca kiểm thử lân cận,
/// và chính sự phụ thuộc chéo đó khiến bộ kiểm thử tầng dữ liệu trở nên thiếu tin cậy.
/// </remarks>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        var name = $"Glassquid-tests-{Guid.NewGuid():n}";
        Root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public GlassquidDataStore CreateStore() => new(Paths);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Thư mục tạm còn sót lại không đáng để làm cả lượt kiểm thử thất bại.
        }
        catch (UnauthorizedAccessException)
        {
            // Tương tự như trên.
        }
    }
}