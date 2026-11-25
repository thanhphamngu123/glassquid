using System.IO.Pipes;
using System.Text;

namespace Glassquid.Platform;

/// <summary>Các lệnh mà lần khởi chạy thứ hai có thể chuyển cho thực thể đang chạy.</summary>
public static class InstanceCommands
{
    /// <summary>Hiển thị Launchpad.</summary>
    public const string ShowLauncher = "show-launcher";

    /// <summary>Hiển thị trung tâm cài đặt.</summary>
    public const string ShowSettings = "show-settings";

    /// <summary>Ẩn Launchpad khi đang mở.</summary>
    public const string HideLauncher = "hide-launcher";

    /// <summary>Đóng thực thể đang chạy.</summary>
    public const string Exit = "exit";
}

/// <summary>Kết quả khi xin cấp quyền giữ chỗ tương thích giao thức đơn thực thể phiên bản cũ trước giai đoạn 7.</summary>
public enum LegacyInstanceStatus
{
    /// <summary>Vị trí thực thể phiên bản cũ không tồn tại.</summary>
    NotRunning,

    /// <summary>Vị trí thực thể phiên bản cũ vẫn tồn tại.</summary>
    Running,

    /// <summary>Không thể xác nhận trạng thái vị trí thực thể phiên bản cũ; bên gọi nên xử lý như vẫn đang chạy.</summary>
    Unknown,
}

/// <summary>
/// Đảm bảo mỗi phiên người dùng chỉ có một thực thể và cho phép các lần khởi chạy sau liên lạc với nó.
/// </summary>
/// <remarks>
/// Danh tính được suy ra từ tên sản phẩm và được tách biệt có chủ đích khỏi mutex của sản phẩm tham chiếu,
/// nhờ đó Glassquid có thể tồn tại song song với MacDock hoặc Ym Launcher đã cài đặt
/// mà không cản trở lẫn nhau.
///
/// Chỉ dùng mutex là chưa đủ: khi khởi chạy lại chương trình cần đưa thực thể đang chạy lên tiền cảnh
/// thay vì im lặng không phản hồi, vì vậy thực thể đầu tiên còn lắng nghe một đường ống đặt tên (named pipe)
/// để các lần khởi chạy tiếp theo gửi lệnh tới.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string LegacyMutexPrefix = @"Local\Glassquid.SingleInstance.";
    private readonly string _instanceName;
    private readonly InstanceIdentity _identity;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    private Mutex? _legacyMutex;
    private Task? _listener;
    private bool _disposed;

    private SingleInstanceGuard(string instanceName, InstanceIdentity identity, Mutex mutex, bool isFirstInstance)
    {
        _instanceName = instanceName;
        _identity = identity;
        _mutex = mutex;
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>Trả về true khi tiến trình này là thực thể sở hữu UI.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Kích hoạt trên luồng nền khi thực thể khác gửi lệnh tới.</summary>
    public event EventHandler<string>? CommandReceived;

    /// <summary>
    /// Chiếm giữ vị trí thực thể cho <paramref name="instanceName"/>.
    /// </summary>
    public static SingleInstanceGuard Acquire(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        // Local\ giới hạn không gian tên đối tượng, kết hợp SID và mã phiên để khóa và đường ống dùng cùng một danh tính thực thể sản phẩm.
        var identity = InstanceIdentity.Current;
        var mutex = new Mutex(initiallyOwned: false, identity.GetMutexName(instanceName), out var createdNew);
        return new SingleInstanceGuard(instanceName, identity, mutex, createdNew);
    }

    /// <summary>Lấy tên thực thể phiên bản cũ và giữ quyền giữ chỗ tương thích cho đến khi thực thể này thoát.</summary>
    /// <remarks>
    /// Thực thể phiên bản mới đầu tiên phải giữ tên giao thức cũ trong suốt vòng đời của nó. Tiến trình phiên bản cũ dùng
    /// <c>createdNew</c> để xác định danh tính thực thể; việc mở tạm rồi giải phóng chỉ kiểm tra được trường hợp "bản cũ khởi chạy trước",
    /// chứ không ngăn được bản cũ tạo lại mutex cùng tên sau khi bản mới đã khởi chạy.
    /// </remarks>
    public bool TryAcquireLegacyProtocolLease(out LegacyInstanceStatus status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsFirstInstance)
        {
            throw new InvalidOperationException("Chỉ thực thể phiên bản mới đầu tiên mới có thể lấy quyền giữ chỗ tương thích giao thức cũ.");
        }

        if (_legacyMutex is not null)
        {
            status = LegacyInstanceStatus.NotRunning;
            return true;
        }

        try
        {
            var mutex = new Mutex(
                initiallyOwned: false,
                LegacyMutexPrefix + _instanceName,
                out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                status = LegacyInstanceStatus.Running;
                return false;
            }

            _legacyMutex = mutex;
            status = LegacyInstanceStatus.NotRunning;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Khi không thể xác nhận tên phiên bản cũ đã bị chiếm hay chưa, tiến trình chủ phải xử lý như vẫn đang chạy.
            status = LegacyInstanceStatus.Unknown;
            return false;
        }
    }

    /// <summary>
    /// Bắt đầu tiếp nhận lệnh từ các lần khởi chạy tiếp theo.
    /// Không làm gì nếu không phải thực thể đầu tiên.
    /// </summary>
    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsFirstInstance || _listener is not null)
        {
            return;
        }

        _listener = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    /// <summary>
    /// Gửi lệnh tới thực thể đang chạy.
    /// </summary>
    /// <returns>Trả về true khi thực thể đang chạy đã chấp nhận lệnh.</returns>
    /// <remarks>
    /// Sẽ thử lại ngắn gọn vì khi lần khởi chạy thứ hai tới, thực thể đầu tiên có thể vẫn đang thiết lập trình lắng nghe —
    /// người dùng khởi chạy liên tiếp hai lần nhanh là hành vi bình thường.
    /// </remarks>
    public static async Task<bool> TrySendCommandAsync(string instanceName, string command, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var pipeName = InstanceIdentity.Current.GetPipeName(instanceName);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                await client.ConnectAsync(250).ConfigureAwait(false);

                var payload = Encoding.UTF8.GetBytes(command + "\n");
                await client.WriteAsync(payload).ConfigureAwait(false);
                await client.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Trình lắng nghe chưa sẵn sàng hoặc đã biến mất. Thử lại trước khi hết thời hạn.
                await Task.Delay(60).ConfigureAwait(false);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Cancel();

        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Điều kiện tranh chấp khi đóng là điều đã dự liệu trước, không có gì cần báo cáo.
        }

        _cancellation.Dispose();
        _legacyMutex?.Dispose();
        _legacyMutex = null;
        _mutex.Dispose();
        _disposed = true;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        // Mỗi lệnh đến chiếm một thực thể đường ống, nên mỗi vòng lặp tạo lại phía máy chủ
        // thay vì cố tái sử dụng một luồng đã bị tiêu thụ.
        var server = new NamedPipeServerStream(
            _identity.GetPipeName(_instanceName),
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(command))
                {
                    CommandReceived?.Invoke(this, command.Trim());
                }

                // Ngắt kết nối để thực thể này phục vụ lần khởi chạy tiếp theo.
                server.Disconnect();

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Đóng bình thường.
        }
        catch (IOException)
        {
            // Đường ống đã bị tháo dỡ; tiến trình chủ đang thoát và không có đối tượng nào cần thông báo.
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
