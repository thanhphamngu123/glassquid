using System.Text.Json;
using Glassquid.Core.Input;
using Glassquid.Core.Serialization;

namespace Glassquid.Core.Models.Settings;

public static class SettingsPageEditor
{
    public static GlassquidSettings Clone(GlassquidSettings settings) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(settings, GlassquidJsonContext.Default.GlassquidSettings),
        GlassquidJsonContext.Default.GlassquidSettings)!;

    public static void Apply(GlassquidSettings target, GlassquidSettings source)
    {
        target.Appearance = source.Appearance;
        target.Dock = source.Dock;
        target.Launcher = source.Launcher;
        target.Shortcuts = source.Shortcuts;
        target.Display = source.Display;
        target.Performance = source.Performance;
        target.DesktopTaskbar = source.DesktopTaskbar;
    }

    public static GlassquidSettings ResetPage(GlassquidSettings settings, string pageName)
    {
        var copy = Clone(settings);
        switch (pageName)
        {
            case "Giao diện":
                copy.Appearance = new() { DockCornerRadius = settings.Appearance.DockCornerRadius };
                break;
            case "Dock":
                copy.Dock = new() { TargetDisplayId = settings.Dock.TargetDisplayId };
                copy.Appearance.DockCornerRadius = 40;
                break;
            case "Launchpad": copy.Launcher = new(); break;
            case "Thao tác nhanh": copy.Shortcuts = new(); break;
            case "Màn hình": copy.Display = new(); copy.Dock.TargetDisplayId = null; break;
            case "Hiệu suất": copy.Performance = new(); break;
            case "Màn hình nền và thanh tác vụ": copy.DesktopTaskbar = new(); break;
            default: throw new ArgumentException("Trang này không có thiết lập nào để khôi phục.", nameof(pageName));
        }
        return copy;
    }

    public static string? Validate(GlassquidSettings settings)
    {
        var dock = settings.Dock;
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if ((dock.FrameLengthDip != 0 && !Range(dock.FrameLengthDip, 64, DockSettings.MaximumFrameLengthDip))
            || !Range(dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale)
            || !Range(dock.IconSize, 32, 96) || !Range(dock.IconSpacing, 0, 24) || !Range(dock.MagnificationPeak, 1, 2)
            || !Range(dock.OffsetX, -200, 200) || !Range(dock.OffsetY, -200, 200)
            || !Range(dock.AutoHideLeaveDelayMs, 0, 3000) || !Range(dock.EdgeHotzoneSize, 1, 64)
            || !Enum.IsDefined(dock.Placement)) return "Giá trị Dock nằm ngoài phạm vi cho phép.";
        if (!Range(settings.Launcher.Rows, 2, 10) || !Range(settings.Launcher.Columns, 2, 12)
            || !Range(settings.Launcher.WindowWorkAreaRatio, 0.3, 1)) return "Số hàng, số cột hoặc tỷ lệ cửa sổ Launchpad nằm ngoài phạm vi cho phép.";
        if (!Range(settings.Appearance.DockCornerRadius, 0, 40) || !Range(settings.Appearance.LauncherCornerRadius, 0, 80)
            || !Range(settings.Appearance.GlassTintOpacity, 0, 1)
            || !Range(settings.Appearance.GlassBackgroundClarity, 0, 1)
            || !Range(settings.Appearance.DockGlassTransparency, 0, 1)
            || !Range(settings.Appearance.LauncherGlassTransparency, 0, 1)
            || !Range(settings.Appearance.DockBuiltinIconBrightness, 0, 1)
            || !Range(settings.Appearance.LauncherTextBrightness, 0, 1)
            || !Enum.IsDefined(settings.Appearance.Theme)
            || !Enum.IsDefined(settings.Appearance.GlassMaterial)) return "Giá trị giao diện nằm ngoài phạm vi cho phép.";
        var shortcuts = settings.Shortcuts;
        if (!Range(shortcuts.HotCornerDwellMs, 100, 3000) || !Enum.IsDefined(shortcuts.HotCornerTarget)
            || !Enum.IsDefined(shortcuts.MouseBackAction) || !Enum.IsDefined(shortcuts.MouseForwardAction)) return "Thời gian giữ chuột ở góc phải từ 100 đến 3000 mili giây và thao tác kích hoạt phải hợp lệ.";
        if (shortcuts.HotCornersEnabled && shortcuts.HotCornerTarget == HotCorner.None) return "Hãy chọn một góc màn hình để bật thao tác góc nóng.";
        var seen = new HashSet<HotkeyChord>();
        foreach (var (_, definition) in HotkeyText.Bindings(shortcuts))
        {
            if (!definition.Enabled) continue;
            if (!HotkeyText.TryGetChord(definition, out var chord, out var error)) return error;
            if (!seen.Add(chord)) return "Nhiều thao tác đang dùng cùng một phím tắt.";
        }
        return null;
    }
}
