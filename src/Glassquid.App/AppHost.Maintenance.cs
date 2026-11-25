using System.IO;
using System.Diagnostics;
using System.Windows;
using Glassquid.AppServices;
using Glassquid.Platform.Installation;

namespace Glassquid.App;

internal sealed partial class AppHost
{
    private int _maintenanceStarted;

    private void OnSettingsUserDataDeletionRequested(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) != 0)
        {
            return;
        }

        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        UserDataMaintenanceSequence.Execute(
            closeCommandDispatch: () =>
            {
                _deferredCommandGate.Close();
                _shuttingDown = true;
            },
            prepareSettings: () => _settings?.PrepareForUserDataMaintenance(),
            stopInteractions: DisposeInteractionController,
            stopTray: () =>
            {
                _tray?.Dispose();
                _tray = null;
            },
            stopServices: () =>
            {
                _services?.Dispose();
                _services = null;
            },
            closeMessageWindow: () =>
            {
                _messageWindow?.Dispose();
                _messageWindow = null;
            },
            releaseDataLease: () =>
            {
                _userDataLease?.Dispose();
                _userDataLease = null;
            },
            deleteData: () => UserDataDeletionService.ForCurrentUser(installDirectory).DeleteAll(confirmed: true),
            reportStopFailure: ex => MessageBox.Show(
                $"Không thể dừng an toàn thao tác ghi dữ liệu của Glassquid nên chưa xóa dữ liệu. Hãy khởi động lại ứng dụng rồi thử lại.\n\n{ex.Message}",
                "Chưa xóa dữ liệu Glassquid",
                MessageBoxButton.OK,
                MessageBoxImage.Error),
            presentResult: result =>
            {
                var (message, icon) = FormatDeletionResult(result);
                MessageBox.Show(message, "Bảo trì dữ liệu Glassquid", MessageBoxButton.OK, icon);
            },
            shutdown: _application.Shutdown);
    }

    private void OnSettingsUninstallRequested(object? sender, EventArgs e)
    {
        if (_shuttingDown || Volatile.Read(ref _maintenanceStarted) != 0) return;

        var info = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
        if (info.State != InstallationState.VerifiedInstalled || string.IsNullOrWhiteSpace(info.UninstallerPath))
        {
            _settings?.AttachInstallation(info);
            return;
        }

        try
        {
            // Chỉ khởi chạy đường dẫn tệp thực thi đã được xác minh; không truyền hoặc thực thi các tham số UninstallString trong registry.
            using var process = Process.Start(new ProcessStartInfo(info.UninstallerPath) { UseShellExecute = true });
            if (process is null)
            {
                _settings?.ShowUninstallLaunchError("Windows không thể khởi chạy trình gỡ cài đặt.");
                return;
            }

            _deferredCommandGate.Close();
            _shuttingDown = true;
            _application.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _settings?.ShowUninstallLaunchError(ex.Message);
        }
    }

    private static (string Message, MessageBoxImage Icon) FormatDeletionResult(UserDataDeletionResult result) => result.Status switch
    {
        UserDataDeletionStatus.Deleted =>
            ("Đã xóa dữ liệu người dùng Glassquid. Ứng dụng sẽ thoát; lần khởi động sau sẽ tạo dữ liệu mặc định mới.", MessageBoxImage.Information),
        UserDataDeletionStatus.NotFound =>
            ("Không tìm thấy thư mục dữ liệu người dùng Glassquid. Ứng dụng sẽ thoát.", MessageBoxImage.Information),
        UserDataDeletionStatus.Busy =>
            (result.Error ?? "Dữ liệu vẫn đang được một phiên bản Glassquid khác sử dụng; chưa có dữ liệu nào bị xóa. Hãy thoát các phiên bản khác rồi thử lại.", MessageBoxImage.Warning),
        UserDataDeletionStatus.UnsafePath =>
            ("Không xác minh được đường dẫn thư mục dữ liệu nên chưa xóa dữ liệu.\n\n" + result.Error, MessageBoxImage.Error),
        UserDataDeletionStatus.Residual =>
            ($"Không thể xóa hết dữ liệu. Đã xóa {result.DeletedEntries} mục; phần còn lại đã được cách ly trong thư mục sau. Hãy kiểm tra và xử lý thủ công:\n\n{result.ResidualDirectory}\n\nGlassquid sẽ không tự động tiếp tục xóa khi khởi động lại.\n\n{result.Error}", MessageBoxImage.Warning),
        _ =>
            ("Không thể đổi tên để cách ly thư mục dữ liệu; dữ liệu gốc được giữ nguyên và thao tác xóa đã dừng.\n\n" + result.Error, MessageBoxImage.Error),
    };
}
