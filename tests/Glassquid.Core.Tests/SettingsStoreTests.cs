using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LoadPreservesDockAutoHideAndDisablesRemovedPointerAndFullscreenFeatures()
    {
        File.WriteAllText(_temp.Paths.SettingsFile,
            """{"dock":{"autoHide":true,"edgeRevealEnabled":true},"launcher":{"fullscreenMode":true},"shortcuts":{"hotCornersEnabled":true,"hotCornerTarget":"TopLeft","mouseSideButtonsEnabled":true}}""");
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        Assert.True(store.Current.Dock.AutoHide);
        Assert.False(store.Current.Dock.EdgeRevealEnabled);
        Assert.False(store.Current.Launcher.FullscreenMode);
        Assert.False(store.Current.Shortcuts.HotCornersEnabled);
        Assert.False(store.Current.Shortcuts.MouseSideButtonsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockDisplayModeSurvivesSavingAndReloading(bool autoHide)
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Dock.AutoHide = autoHide;
        store.Save();
        var reloaded = new SettingsStore(_temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.Equal(autoHide, reloaded.Current.Dock.AutoHide);
    }

    [Fact]
    public void Load_OnEmptyDirectory_ReportsFirstRun_WithSpecifiedDefaults()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);

        var outcome = store.Load();

        Assert.Equal(LoadStatus.FirstRun, outcome.Status);
        Assert.False(outcome.NeedsUserAttention);

        // Giá trị mặc định lấy từ đặc tả giao diện kính lỏng hiện tại, nên cần cố định lại.
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
        Assert.Equal("#0A84FF", store.Current.Appearance.AccentColor);
        Assert.Equal(40, store.Current.Appearance.DockCornerRadius);
        Assert.Equal(80, store.Current.Appearance.LauncherCornerRadius);

        Assert.Equal(48, store.Current.Dock.IconSize);
        Assert.Equal(8, store.Current.Dock.IconSpacing);
        Assert.Equal(1.35, store.Current.Dock.MagnificationPeak);
        Assert.Equal(300, store.Current.Dock.AutoHideLeaveDelayMs);

        Assert.Equal(5, store.Current.Launcher.Rows);
        Assert.Equal(7, store.Current.Launcher.Columns);
        Assert.Equal(0.66, store.Current.Launcher.WindowWorkAreaRatio);
        Assert.False(store.Current.Launcher.FocusSearchOnOpen);
        Assert.True(store.Current.Shortcuts.NextPage.IsBound);
        Assert.Equal("WheelDown", store.Current.Shortcuts.NextPage.Key);
        Assert.True(store.Current.Shortcuts.PreviousPage.IsBound);
        Assert.Equal("WheelUp", store.Current.Shortcuts.PreviousPage.Key);

        // Các tương tác có rủi ro mặc định tắt, chờ người dùng chủ động bật.
        Assert.False(store.Current.Shortcuts.HotCornersEnabled);
        Assert.False(store.Current.Shortcuts.MouseSideButtonsEnabled);
        Assert.False(store.Current.DesktopTaskbar.BlockTaskbarEdgeReveal);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Current.Appearance.AccentColor = "#FF3B30";
        store.Current.Dock.Placement = DockPlacement.TopCenter;
        store.Current.Dock.TargetDisplayId = "DISPLAY2";
        store.Save();

        var reloaded = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = reloaded.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, reloaded.Current.Appearance.Theme);
        Assert.Equal("#FF3B30", reloaded.Current.Appearance.AccentColor);
        Assert.Equal(DockPlacement.TopCenter, reloaded.Current.Dock.Placement);
        Assert.Equal("DISPLAY2", reloaded.Current.Dock.TargetDisplayId);
    }

    [Fact]
    public void Load_WhenMainFileIsCorruptAndBackupIsGood_RecoversAndPreserves()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Save();

        // Lưu thêm một lần nữa để quy trình thay thế có phiên bản trước giữ làm bản sao lưu.
        store.Current.Appearance.AccentColor = "#34C759";
        store.Save();

        File.WriteAllText(_temp.Paths.SettingsFile, "{ not valid json at all");

        var damaged = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = damaged.Load();

        Assert.Equal(LoadStatus.RecoveredFromBackup, outcome.Status);
        Assert.True(outcome.NeedsUserAttention);
        Assert.NotNull(outcome.PreservedFile);
        Assert.True(File.Exists(outcome.PreservedFile));

        // Giá trị khôi phục chính là bản nằm trên đĩa trước khi xảy ra hỏng tệp.
        Assert.Equal(ThemeMode.Dark, damaged.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_WhenBothFilesAreCorrupt_UsesDefaultsAndKeepsTheEvidence()
    {
        File.WriteAllText(_temp.Paths.SettingsFile, "broken");
        File.WriteAllText(_temp.Paths.SettingsFile + AtomicFile.BackupSuffix, "also broken");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Unrecoverable, outcome.Status);
        Assert.NotNull(outcome.PreservedFile);
        Assert.True(File.Exists(outcome.PreservedFile));
        Assert.Equal("broken", File.ReadAllText(outcome.PreservedFile));

        // Vẫn dùng được: cấu hình bị hỏng không được ngăn sản phẩm khởi động.
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_TreatsEmptyFileAsCorrupt()
    {
        // Đây là trạng thái sót lại khi quá trình lưu bị mất điện ngắt quãng trước khi áp dụng thay thế nguyên tử.
        // Không được phân tích nó như một tài liệu "trống nhưng hợp lệ".
        File.WriteAllText(_temp.Paths.SettingsFile, string.Empty);

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.NotEqual(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(outcome.PreservedFile);
    }

    [Fact]
    public void Load_WithNullSections_RepairsThemInsteadOfThrowing()
    {
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """{"schemaVersion":1,"appearance":null,"dock":null,"shortcuts":null}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(store.Current.Appearance);
        Assert.NotNull(store.Current.Dock);
        Assert.NotNull(store.Current.Shortcuts);
        Assert.NotNull(store.Current.Shortcuts.ToggleLauncher);
    }

    [Fact]
    public void Load_StampsTheCurrentSchemaVersion()
    {
        File.WriteAllText(_temp.Paths.SettingsFile, """{"schemaVersion":0}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();

        Assert.Equal(GlassquidSettings.CurrentSchemaVersion, store.Current.SchemaVersion);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        // Người dùng nâng cao sẽ chỉnh sửa thủ công tệp này; từ chối tải chỉ vì lỗi cú pháp nhỏ thì quá khắt khe.
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """
            {
              // the accent colour
              "schemaVersion": 1,
              "appearance": { "theme": "Dark", },
            }
            """);

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
    }

    [Fact]
    public void Load_WrittenByNewerSchema_KeepsDataWithoutLosingIt()
    {
        File.WriteAllText(
            _temp.Paths.SettingsFile,
            """{"schemaVersion":99,"appearance":{"theme":"Dark","accentColor":"#123456"}}""");

        var store = new SettingsStore(_temp.Paths.SettingsFile);
        var outcome = store.Load();

        // Đọc tương thích xuôi: giữ lại phần hiểu được thay vì từ chối khởi động.
        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
        Assert.Equal("#123456", store.Current.Appearance.AccentColor);
    }

    [Fact]
    public void Save_ProducesReadableJson_WithCamelCaseAndNamedEnums()
    {
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.Theme = ThemeMode.Dark;
        store.Save();

        var json = File.ReadAllText(_temp.Paths.SettingsFile);

        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"Dark\"", json, StringComparison.Ordinal);
    }
}
