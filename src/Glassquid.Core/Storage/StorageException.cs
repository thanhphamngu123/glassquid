namespace Glassquid.Core.Storage;

/// <summary>
/// Được ném ra khi không thể ghi tiếp dữ liệu lưu trữ bền vững. Mang theo một thông báo hiển thị cho người dùng,
/// và cố ý không kế thừa bất kỳ kiểu nào có thể làm sập tiến trình chủ — lỗi lưu
/// phải hạ cấp thành một thông báo nhắc nhở, tuyệt đối không được dẫn đến thoát ứng dụng.
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(string message, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>Tệp bị lỗi khi ghi.</summary>
    public string Path { get; }
}