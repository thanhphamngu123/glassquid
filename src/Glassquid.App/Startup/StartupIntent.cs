using Glassquid.Platform;

namespace Glassquid.App.Startup;

/// <summary>Chuyển đổi ý định khởi chạy từ dòng lệnh thành hành vi cửa sổ phiên bản đầu tiên và IPC cho phiên bản trùng lặp.</summary>
internal static class StartupIntent
{
    public static string? GetDuplicateInstanceCommand(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Autostart)
            return null;

        return options switch
        {
            { Exit: true } => InstanceCommands.Exit,
            { ShowSettings: true } => InstanceCommands.ShowSettings,
            { ShowLauncher: true } => InstanceCommands.ShowLauncher,
            _ => InstanceCommands.ShowSettings,
        };
    }

    public static bool ShouldOpenLauncher(CommandLineOptions options) =>
        !options.Autostart && options.ShowLauncher && !options.ShowSettings;

    public static bool ShouldOpenSettings(CommandLineOptions options) =>
        !options.Autostart && (options.ShowSettings || !options.ShowLauncher);

    public static bool ShouldExitWithoutExistingInstance(CommandLineOptions options) =>
        options.Exit;

    public static string GetLegacyInstanceBlockMessage(LegacyInstanceStatus status) => status switch
    {
        LegacyInstanceStatus.Running => "Phát hiện phiên bản Glassquid cũ vẫn đang chạy. Để tránh hai phiên bản cùng sửa dữ liệu, hãy thoát phiên bản cũ từ menu khay hệ thống rồi thử lại.",
        LegacyInstanceStatus.Unknown => "Không thể xác định phiên bản Glassquid cũ còn chạy hay không. Để bảo vệ dữ liệu, ứng dụng đã chặn khởi chạy. Hãy xác nhận phiên bản cũ đã thoát hoàn toàn rồi thử lại.",
        _ => string.Empty,
    };
}
