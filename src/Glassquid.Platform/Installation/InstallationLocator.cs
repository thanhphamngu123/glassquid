using System.Buffers.Binary;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Glassquid.Platform.Installation;

public enum InstallationState
{
    Portable,
    VerifiedInstalled,
    RegistrationMismatch,
}

public sealed record InstallationInfo(
    InstallationState State,
    string? InstallDirectory = null,
    string? UninstallerPath = null,
    string? Error = null);

/// <summary>Kiểm tra chỉ đọc xem chương trình hiện tại có khớp với thông tin đăng ký cài đặt Glassquid Inno Setup cố định hay không.</summary>
/// <remarks>Chỉ đọc UninstallString để trích xuất đường dẫn ứng viên; lớp này không bao giờ thực thi chuỗi đăng ký hoặc tham số của nó.</remarks>
public sealed class InstallationLocator
{
    public const string AppId = "Glassquid.Independent";
    public const string UninstallRegistrySubKey =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Glassquid.Independent_is1";

    private readonly string _registrySubKey;

    public InstallationLocator() : this(UninstallRegistrySubKey)
    {
    }

    internal InstallationLocator(string registrySubKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrySubKey);
        _registrySubKey = registrySubKey;
    }

    public InstallationInfo Inspect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Mismatch("Đường dẫn tệp thực thi Glassquid hiện tại không khả dụng.");
        }

        RegistryKey? key;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return Mismatch("Không thể đọc thông tin cài đặt Glassquid của người dùng hiện tại: " + ex.Message);
        }

        if (key is null)
        {
            return new InstallationInfo(InstallationState.Portable);
        }

        using (key)
        {
            try
            {
                var displayName = ReadString(key, "DisplayName");
                var installValue = ReadString(key, "InstallLocation");
                var uninstallValue = ReadString(key, "UninstallString");
                if (!string.Equals(displayName, "Glassquid", StringComparison.Ordinal))
                {
                    return Mismatch("Tên hiển thị trong thông tin sản phẩm đã ghim không khớp với Glassquid.");
                }

                if (string.IsNullOrWhiteSpace(installValue) || string.IsNullOrWhiteSpace(uninstallValue))
                {
                    return Mismatch("Thông tin sản phẩm đã ghim thiếu đường dẫn cài đặt hoặc gỡ cài đặt.");
                }

                var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installValue));
                var executable = Path.GetFullPath(executablePath);
                if (!PathsEqual(Path.GetDirectoryName(executable) ?? string.Empty, installDirectory)
                    || !string.Equals(Path.GetFileName(executable), "Glassquid.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return Mismatch("Tiến trình hiện tại không nằm trong thư mục cài đặt Glassquid đã đăng ký.");
                }

                if (!Directory.Exists(installDirectory) || !File.Exists(executable)
                    || !HasNoReparsePointInPath(executable))
                {
                    return Mismatch("Thiếu đường dẫn cài đặt hiện tại hoặc đường dẫn có chứa liên kết thư mục/liên kết tượng trưng.");
                }

                var uninstaller = ParseExecutablePath(uninstallValue);
                if (uninstaller is null)
                {
                    return Mismatch("Đường dẫn gỡ cài đặt đã đăng ký không phải tệp thực thi có thể xác minh.");
                }

                uninstaller = Path.GetFullPath(uninstaller);
                if (!PathsEqual(Path.GetDirectoryName(uninstaller) ?? string.Empty, installDirectory)
                    || !IsInnoUninstallerName(Path.GetFileName(uninstaller))
                    || !File.Exists(uninstaller)
                    || !HasNoReparsePointInPath(uninstaller)
                    || !IsPortableExecutable(uninstaller))
                {
                    return Mismatch("Không tìm thấy trình gỡ cài đặt hoặc trình này không thuộc thư mục cài đặt Glassquid hiện tại.");
                }

                return new InstallationInfo(InstallationState.VerifiedInstalled, installDirectory, uninstaller);
            }
            catch (Exception ex) when (IsRegistryFailure(ex) || ex is IOException or ArgumentException or NotSupportedException)
            {
                return Mismatch("Không thể xác minh an toàn thông tin cài đặt Glassquid: " + ex.Message);
            }
        }
    }

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private static string? ParseExecutablePath(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0) return null;
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote <= 1 || trimmed[(closingQuote + 1)..].Trim().Length != 0) return null;
            return trimmed[1..closingQuote];
        }

        var separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : null;
    }

    private static bool IsInnoUninstallerName(string fileName)
    {
        if (fileName.Length != 12 || !fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return char.IsAsciiDigit(fileName[5]) && char.IsAsciiDigit(fileName[6]) && char.IsAsciiDigit(fileName[7]);
    }

    private static bool HasNoReparsePointInPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current)) break;
            current = parent;
        }

        return true;
    }

    private static bool IsPortableExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> dosHeader = stackalloc byte[64];
        try
        {
            stream.ReadExactly(dosHeader);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..0x40]);
        if (peOffset < dosHeader.Length || peOffset > stream.Length - 4) return false;
        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        try
        {
            stream.ReadExactly(signature);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return signature[0] == (byte)'P' && signature[1] == (byte)'E'
            && signature[2] == 0 && signature[3] == 0;
    }

    private static bool IsRegistryFailure(Exception ex) => ex is SecurityException or UnauthorizedAccessException
        or IOException or SystemException;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static InstallationInfo Mismatch(string message) =>
        new(InstallationState.RegistrationMismatch, Error: message);
}
