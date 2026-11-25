using System.Diagnostics;
using System.Security.Principal;

namespace Glassquid.Platform;

/// <summary>Giới hạn đối tượng đơn thực thể theo Windows SID người dùng và phiên làm việc cụ thể.</summary>
internal sealed class InstanceIdentity
{
    private const string MutexPrefix = "Glassquid.SingleInstance.";
    private const string PipePrefix = "Glassquid.Pipe.";

    public InstanceIdentity(string userSid, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Contains('\\') || userSid.Contains('/') || sessionId < 0)
            throw new ArgumentException("Danh tính đơn thực thể phải chứa SID hợp lệ và mã phiên không âm.");

        UserSid = userSid;
        SessionId = sessionId;
    }

    public string UserSid { get; }

    public int SessionId { get; }

    public static InstanceIdentity Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("Không thể đọc SID người dùng Windows hiện tại.");
            return new InstanceIdentity(sid, process.SessionId);
        }
    }

    public string GetMutexName(string instanceName)
    {
        ValidateInstanceName(instanceName);
        return $@"Local\{MutexPrefix}{UserSid}.{SessionId}.{instanceName}";
    }

    public string GetPipeName(string instanceName)
    {
        ValidateInstanceName(instanceName);
        return $"{PipePrefix}{UserSid}.{SessionId}.{instanceName}";
    }

    private static void ValidateInstanceName(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (instanceName.Contains('\\') || instanceName.Contains('/') || instanceName.Any(char.IsControl))
            throw new ArgumentException("Tên đơn thực thể không được chứa ký tự phân cách đường dẫn hoặc ký tự điều khiển.", nameof(instanceName));
    }
}
