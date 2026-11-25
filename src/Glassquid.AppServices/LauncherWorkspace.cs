using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices;

/// <summary>Điều phối bố cục Launchpad cùng việc chỉnh sửa thư viện ứng dụng dùng chung và tệp bố cục.</summary>
public sealed class LauncherWorkspace
{
    private static readonly HashSet<string> WindowsToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "control.exe", "SystemSettings.exe", "regedit.exe", "regedt32.exe", "mmc.exe",
        "LiveCaptions.exe", "magnify.exe", "narrator.exe", "osk.exe", "voiceaccess.exe",
        "mstsc.exe", "psr.exe", "charmap.exe", "dfrgui.exe", "cleanmgr.exe",
        "iscsicpl.exe", "odbcad32.exe", "perfmon.exe", "resmon.exe", "RecoveryDrive.exe",
        "msconfig.exe", "msinfo32.exe", "taskmgr.exe", "MdSched.exe", "eventvwr.exe",
        "SystemPropertiesAdvanced.exe", "SystemPropertiesComputerName.exe",
        "SystemPropertiesHardware.exe", "SystemPropertiesPerformance.exe",
        "SystemPropertiesProtection.exe", "SystemPropertiesRemote.exe", "optionalfeatures.exe",
        "UserAccountControlSettings.exe", "utilman.exe", "DisplaySwitch.exe", "sdclt.exe",
    };

    private readonly AppServiceHost _host;
    private readonly bool _libraryAvailable;

    public LauncherWorkspace(AppServiceHost host, bool libraryAvailable = true)
    {
        _host = host;
        _libraryAvailable = libraryAvailable;
    }

    public LauncherLayout Layout => _host.Layouts.Launcher;

    public bool Reconcile(int capacity)
    {
        if (!_libraryAvailable) return false;
        var draft = LauncherLayoutRules.Reconcile(Layout,
            _host.Library.Items.Where(item => item is { IsUsable: true } && !IsWindowsTool(item))
                .Select(item => item.Id), capacity);
        if (LauncherLayoutRules.Equivalent(Layout, draft)) return false;
        return _host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion);
    }

    // Filter only Launchpad membership. Keep shared library entries and Dock pins intact.
    // Use launch identities, never translated display names or a blanket Windows-folder rule.
    private static bool IsWindowsTool(AppEntry item)
    {
        var target = Environment.ExpandEnvironmentVariables(item.Target).Trim().Trim('"');
        if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)) return true;

        if (item.AppUserModelId is { } appId
            && (appId.Equals("Microsoft.Windows.AdministrativeTools", StringComparison.OrdinalIgnoreCase)
                || appId.Equals("Microsoft.Windows.ControlPanel", StringComparison.OrdinalIgnoreCase)
                || appId.Equals("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                    StringComparison.OrdinalIgnoreCase)
                || appId.Equals("Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI", StringComparison.OrdinalIgnoreCase)
                || appId.Equals("Microsoft.Windows.SecHealthUI_cw5n1h2txyewy!SecHealthUI", StringComparison.OrdinalIgnoreCase)))
            return true;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows) || !Path.IsPathFullyQualified(target)) return false;
        try { target = Path.GetFullPath(target); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }

        var root = Path.TrimEndingDirectorySeparator(windows) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = target[root.Length..];
        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        var systemDirectory = directory.Equals("System32", StringComparison.OrdinalIgnoreCase)
            || directory.Equals("SysWOW64", StringComparison.OrdinalIgnoreCase)
            || directory.Equals("Sysnative", StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(relative);

        if (systemDirectory && (Path.GetExtension(name).Equals(".msc", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(name).Equals(".cpl", StringComparison.OrdinalIgnoreCase))) return true;
        if ((systemDirectory || directory.Length == 0
            || directory.Equals("ImmersiveControlPanel", StringComparison.OrdinalIgnoreCase))
            && WindowsToolNames.Contains(name)) return true;

        // Keep installed applications that use a Windows command host to run their own scripts.
        if (!string.IsNullOrWhiteSpace(item.Arguments)) return false;
        if (systemDirectory && name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return (directory.Equals(@"System32\WindowsPowerShell\v1.0", StringComparison.OrdinalIgnoreCase)
                || directory.Equals(@"SysWOW64\WindowsPowerShell\v1.0", StringComparison.OrdinalIgnoreCase)
                || directory.Equals(@"Sysnative\WindowsPowerShell\v1.0", StringComparison.OrdinalIgnoreCase))
            && (name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("powershell_ise.exe", StringComparison.OrdinalIgnoreCase));
    }

    public LauncherEditSession BeginEdit()
        => new(_host.Layouts.LauncherVersion, _host.Library.Version, LauncherLayoutRules.DeepClone(Layout));

    public bool Commit(LauncherEditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_host.Library.Version != session.LibraryVersion) return false;
        return _host.Layouts.CommitLauncher(session.Draft, session.LayoutVersion);
    }
}

/// <summary>Bản nháp tách biệt khỏi bố cục dùng chung; hủy bỏ bản nháp sẽ không làm thay đổi bộ nhớ hay đĩa.</summary>
public sealed class LauncherEditSession(long layoutVersion, long libraryVersion, LauncherLayout draft)
{
    public long LayoutVersion { get; } = layoutVersion;
    public long LibraryVersion { get; } = libraryVersion;
    public LauncherLayout Draft { get; set; } = draft;
}
