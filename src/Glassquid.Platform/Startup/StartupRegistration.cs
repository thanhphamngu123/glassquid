using System.Security;
using Microsoft.Win32;

namespace Glassquid.Platform.Startup;

/// <summary>Trạng thái mục khởi chạy khi đăng nhập của người dùng hiện tại đối với sản phẩm này.</summary>
public enum StartupRegistrationState
{
    /// <summary>Giá trị của sản phẩm không tồn tại.</summary>
    Missing,

    /// <summary>Giá trị của sản phẩm trỏ chính xác tới tệp thực thi hiện tại kèm --autostart.</summary>
    Owned,

    /// <summary>Tồn tại giá trị cùng tên nhưng không thuộc về tệp thực thi hiện tại.</summary>
    OtherTarget,

    /// <summary>Không thể đọc hoặc kiểm tra trạng thái đăng ký.</summary>
    Unavailable,
}

/// <summary>Trạng thái dễ đọc của mục khởi chạy khi đăng nhập cho người dùng hiện tại.</summary>
public sealed record StartupRegistrationStatus(
    StartupRegistrationState State,
    string? TargetPath,
    string? Error);

/// <summary>Kết quả thực tế sau khi sửa đổi mục khởi chạy khi đăng nhập của người dùng hiện tại.</summary>
public sealed record StartupChangeResult(
    bool Succeeded,
    StartupRegistrationStatus Status,
    string? Error);

/// <summary>Quản lý tác vụ khởi chạy khi đăng nhập cho người dùng hiện tại và tương thích với mục đăng ký HKCU Run cũ.</summary>
public sealed partial class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int MaximumCommandLineLength = 260;
    private const string AutostartArgument = "--autostart";

    /// <summary>Tên giá trị Run của Glassquid cho người dùng hiện tại, giữ nhất quán với bản đăng ký cài đặt ban đầu.</summary>
    public const string ValueName = "Glassquid.Independent";

    private readonly Func<bool, RegistryKey?> _openRunKey;
    private readonly string _valueName;
    private readonly bool _ownsOpenedKeys;
    private readonly ScheduledStartupRegistration? _scheduled;

    /// <summary>Tạo dịch vụ chính thức quản lý tác vụ đăng nhập người dùng hiện tại và mục Run cũ.</summary>
    public StartupRegistration()
        : this(OpenCurrentUserRunKey, ValueName, ownsOpenedKeys: true)
    {
        _scheduled = new ScheduledStartupRegistration();
    }

    /// <summary>Tiêm một khóa Registry độc lập phục vụ kiểm thử; dịch vụ sẽ không giải phóng khóa này.</summary>
    internal StartupRegistration(RegistryKey runKey, string valueName)
        : this(_ => runKey, valueName, ownsOpenedKeys: false)
    {
        ArgumentNullException.ThrowIfNull(runKey);
    }

    internal StartupRegistration(Func<bool, RegistryKey?> openRunKey, string valueName)
        : this(openRunKey, valueName, ownsOpenedKeys: true)
    {
    }

    private StartupRegistration(Func<bool, RegistryKey?> openRunKey, string valueName, bool ownsOpenedKeys)
    {
        ArgumentNullException.ThrowIfNull(openRunKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _openRunKey = openRunKey;
        _valueName = valueName;
        _ownsOpenedKeys = ownsOpenedKeys;
    }

    /// <summary>Đọc trạng thái hiện tại từ Registry; không tạo khóa Run hay ghi cài đặt.</summary>
    private StartupRegistrationStatus QueryLegacy(string executablePath)
    {
        if (!TryCreateCommand(executablePath, out var fullPath, out var command, out var error))
            return new(StartupRegistrationState.Unavailable, null, error);

        return ReadStatus(fullPath, command);
    }

    /// <summary>Đăng ký hoặc gỡ bỏ tường minh mục khởi chạy khi đăng nhập của sản phẩm cho người dùng hiện tại.</summary>
    private StartupChangeResult SetLegacyEnabled(string executablePath, bool enabled)
    {
        if (!TryCreateCommand(executablePath, out var fullPath, out var command, out var validationError))
        {
            var unavailable = new StartupRegistrationStatus(StartupRegistrationState.Unavailable, null, validationError);
            return new(false, unavailable, validationError);
        }

        var before = ReadStatus(fullPath, command);
        if (before.State == StartupRegistrationState.Unavailable)
            return new(false, before, before.Error);

        if (enabled && before.State == StartupRegistrationState.Owned)
            return new(true, before, null);
        if (!enabled && before.State == StartupRegistrationState.Missing)
            return new(true, before, null);
        if (before.State == StartupRegistrationState.OtherTarget)
        {
            var conflict = "Mục khởi chạy cùng tên đang trỏ đến tệp khác; để bảo vệ mục đó, Glassquid không thay đổi.";
            return new(false, before with { Error = conflict }, conflict);
        }

        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true);
            if (key is null)
                throw new IOException("Không thể mở mục khởi chạy cùng Windows trong Registry của người dùng hiện tại.");

            var current = ReadStatusFromKey(key, fullPath, command);
            if (enabled && current.State == StartupRegistrationState.Owned)
                return new(true, current, null);
            if (!enabled && current.State == StartupRegistrationState.Missing)
                return new(true, current, null);
            if (current.State == StartupRegistrationState.OtherTarget)
            {
                var conflict = "Mục khởi chạy cùng Windows đã bị thay đổi ở nơi khác; để bảo vệ mục đó, Glassquid không ghi đè hoặc xóa.";
                return new(false, current with { Error = conflict }, conflict);
            }
            if (current.State == StartupRegistrationState.Unavailable)
                return new(false, current, current.Error);

            if (enabled)
                key.SetValue(_valueName, command, RegistryValueKind.String);
            else
                key.DeleteValue(_valueName, throwOnMissingValue: false);

            var after = ReadStatus(fullPath, command);
            var expectedState = enabled ? StartupRegistrationState.Owned : StartupRegistrationState.Missing;
            if (after.State == expectedState)
                return new(true, after, null);

            var mismatch = after.Error ?? "Mục khởi chạy cùng Windows chưa ở trạng thái mong muốn sau khi lưu.";
            if (enabled && before.State == StartupRegistrationState.Missing)
                TryRemoveOwnedValue(command);
            var final = ReadStatus(fullPath, command);
            return new(false, final with { Error = mismatch }, mismatch);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            if (enabled && before.State == StartupRegistrationState.Missing)
                TryRemoveOwnedValue(command);

            var current = ReadStatus(fullPath, command);
            if (enabled && current.State == StartupRegistrationState.Owned)
            {
                TryRemoveOwnedValue(command);
                current = ReadStatus(fullPath, command);
            }

            var message = ToUserError(ex);
            return new(false, current with { Error = message }, message);
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private StartupRegistrationStatus ReadStatus(string fullPath, string expectedCommand)
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(false);
            return key is null
                ? new(StartupRegistrationState.Missing, fullPath, null)
                : ReadStatusFromKey(key, fullPath, expectedCommand);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return new(StartupRegistrationState.Unavailable, fullPath, ToUserError(ex));
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private StartupRegistrationStatus ReadStatusFromKey(RegistryKey key, string fullPath, string expectedCommand)
    {
        if (!key.GetValueNames().Contains(_valueName, StringComparer.OrdinalIgnoreCase))
            return new(StartupRegistrationState.Missing, fullPath, null);

        if (key.GetValueKind(_valueName) != RegistryValueKind.String)
            return new(StartupRegistrationState.OtherTarget, null, "Mục khởi chạy cùng tên không phải giá trị chuỗi thông thường; Glassquid sẽ không thay đổi mục đó.");

        var stored = key.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return string.Equals(stored, expectedCommand, StringComparison.OrdinalIgnoreCase)
            ? new(StartupRegistrationState.Owned, fullPath, null)
            : new(StartupRegistrationState.OtherTarget, null, "Mục khởi chạy cùng tên đang trỏ đến tệp khác hoặc có tham số khác.");
    }

    private void TryRemoveOwnedValue(string expectedCommand)
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true);
            if (key is null) return;
            var status = ReadStatusFromKey(key, string.Empty, expectedCommand);
            if (status.State == StartupRegistrationState.Owned)
                key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // Preserve the original write failure; the caller re-queries and reports the actual state.
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private static RegistryKey? OpenCurrentUserRunKey(bool writable) => writable
        ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
        : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

    private static bool TryCreateCommand(string executablePath, out string fullPath, out string command, out string? error)
    {
        fullPath = string.Empty;
        command = string.Empty;
        error = null;

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            if (!Path.IsPathFullyQualified(executablePath))
                throw new ArgumentException("Phải cung cấp đường dẫn đầy đủ đến Glassquid.exe.", nameof(executablePath));

            fullPath = Path.GetFullPath(executablePath);
            if (fullPath.Contains('"'))
                throw new ArgumentException("Đường dẫn ứng dụng chứa dấu ngoặc kép không hợp lệ.", nameof(executablePath));
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Ứng dụng khởi chạy cùng Windows phải là tệp .exe.", nameof(executablePath));

            command = $"\"{fullPath}\" {AutostartArgument}";
            if (command.Length > MaximumCommandLineLength)
                throw new ArgumentException($"Lệnh khởi chạy cùng Windows không được dài quá {MaximumCommandLineLength} ký tự.", nameof(executablePath));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Không tìm thấy tệp thực thi Glassquid.", fullPath);

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsRegistryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException or ObjectDisposedException;

    private static string ToUserError(Exception ex) => ex switch
    {
        UnauthorizedAccessException or SecurityException => "Không có quyền thay đổi mục khởi chạy cùng Windows của người dùng hiện tại." + Environment.NewLine + ex.Message,
        _ => "Không thể đọc hoặc thay đổi mục khởi chạy cùng Windows của người dùng hiện tại: " + ex.Message,
    };
}
