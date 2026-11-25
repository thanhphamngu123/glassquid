using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Glassquid.Platform.Storage;

/// <summary>Điều phối quyền truy cập khi chạy và bảo trì độc quyền trên cùng một thư mục dữ liệu bằng ngữ nghĩa chia sẻ tệp của Windows.</summary>
/// <remarks>
/// Tệp khóa thuê nằm ở thư mục ngang cấp với gốc dữ liệu, nên khóa thuê độc quyền vẫn có thể được giữ trong suốt quá trình đổi tên và xóa gốc dữ liệu.
/// Khóa thuê chia sẻ không chia sẻ quyền xóa; nhiều phiên bản đang chạy có thể mở cùng lúc, còn thao tác bảo trì thì bắt buộc phải mở độc quyền.
/// </remarks>
public sealed class UserDataLease : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private UserDataLease(FileStream stream) => _stream = stream;

    /// <summary>Thử lấy khóa thuê chia sẻ khi chạy; trả về false nếu có tranh chấp hoặc hệ thống tệp từ chối.</summary>
    public static bool TryAcquireShared(string dataRoot, out UserDataLease? lease) =>
        TryAcquire(dataRoot, FileShare.ReadWrite, out lease);

    /// <summary>Thử lấy khóa thuê độc quyền cần thiết cho việc xóa/bảo trì dữ liệu.</summary>
    public static bool TryAcquireExclusive(string dataRoot, out UserDataLease? lease) =>
        TryAcquire(dataRoot, FileShare.None, out lease);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stream.Dispose();
    }

    private static bool TryAcquire(string dataRoot, FileShare sharing, out UserDataLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        lease = null;

        try
        {
            var leasePath = GetLeasePath(dataRoot);
            var parentDirectory = Path.GetDirectoryName(leasePath);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return false;
            }

            Directory.CreateDirectory(parentDirectory);
            var stream = new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                sharing);
            lease = new UserDataLease(stream);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException)
        {
            // Khi quyền bảo trì và quyền chạy không rõ ràng thì luôn từ chối; host sẽ dừng trước khi tải hoặc xóa dữ liệu.
            return false;
        }
    }

    private static string GetLeasePath(string dataRoot)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullRoot) ?? string.Empty);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullRoot, volumeRoot, comparison))
        {
            throw new ArgumentException("Thư mục dữ liệu không thể là thư mục gốc ổ đĩa.", nameof(dataRoot));
        }

        // Đường dẫn Windows không phân biệt chữ hoa chữ thường; cùng một đường dẫn khác chữ hoa/thường phải trỏ về cùng một tệp khóa.
        var leaseIdentity = OperatingSystem.IsWindows() ? fullRoot.ToUpperInvariant() : fullRoot;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaseIdentity)));
        var parentDirectory = Directory.GetParent(fullRoot)?.FullName;
        if (parentDirectory is null)
        {
            throw new ArgumentException("Không thể xác định vị trí khóa sử dụng ngang cấp với thư mục dữ liệu.", nameof(dataRoot));
        }

        return Path.Combine(parentDirectory, $".glassquid-data-{digest}.lease");
    }
}
