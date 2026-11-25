using System.Diagnostics;
using System.Security.Cryptography;
using Glassquid.Platform.Installation;

namespace Glassquid.AppServices.Updates;

/// <summary>Prepare a verified standalone Inno installer; the host controls process launch and shutdown.</summary>
public static class UpdateInstaller
{
    public static async Task<ProcessStartInfo> PrepareAsync(UpdateRelease release, string file,
        InstallationInfo installation, CancellationToken token)
    {
        if (installation.State != InstallationState.VerifiedInstalled || string.IsNullOrWhiteSpace(installation.InstallDirectory))
            throw new InvalidOperationException("Ứng dụng hiện tại không phải bản cài đặt đã xác minh. Hãy cài đặt gói chính thức theo cách thủ công.");
        UpdateClient.ValidateInstaller(release);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.InstallDirectory));
        var installer = Path.GetFullPath(file);
        if (installer.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gói cập nhật phải nằm bên ngoài thư mục cài đặt.");
        if (!Directory.Exists(directory) || Path.GetFileName(installer) != release.FileName)
            throw new InvalidDataException("Đường dẫn thư mục cài đặt hoặc gói cập nhật không hợp lệ.");
        await using (var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            if (stream.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                    .Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Gói cài đặt đã thay đổi sau khi tải xuống. Hãy tải lại.");
        }
        var start = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
        start.ArgumentList.Add("/SP-");
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/GLASSQUIDUPDATE=1");
        start.ArgumentList.Add("/DIR=" + directory);
        start.ArgumentList.Add("/LOG=" + Path.Combine(Path.GetDirectoryName(installer)!, "install.log"));
        return start;
    }
}
