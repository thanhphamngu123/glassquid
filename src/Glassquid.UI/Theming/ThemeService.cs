using System.Windows;
using System.Windows.Threading;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Platform;
using Microsoft.Win32;

// WPF có sẵn System.Windows.ThemeMode, nên tại những nơi vừa import System.Windows vừa import mô hình cài đặt
// sẽ bị trùng tên với kiểu dữ liệu của chúng ta.
using ThemeMode = Glassquid.Core.Models.ThemeMode;

namespace Glassquid.UI.Theming;

/// <summary>
/// Nguồn chân lý duy nhất cho giao diện của sản phẩm.
/// </summary>
/// <remarks>
/// Được thiết kế có chủ đích ở cấp tiến trình chứ không tiêm theo từng cửa sổ.
/// Tại bất kỳ thời điểm nào cũng chỉ có một chủ đề đang có hiệu lực; Dock, Launchpad và Cài đặt
/// đều phải phân giải ra cùng một bảng màu; biến nó thành trạng thái tĩnh
/// sẽ tránh được tình trạng sau khi đổi giao diện, hai cửa sổ lại hiển thị khác nhau.
///
/// Khi ở chế độ theo hệ thống, dịch vụ này đăng ký nhận thông báo tùy chọn của hệ điều hành và phân giải lại,
/// nhờ đó khi chuyển đổi giữa Sáng và Tối trong Windows,
/// các cửa sổ của chúng ta sẽ tự cập nhật mà không cần khởi động lại.
/// </remarks>
public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ThemeService(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;

        Appearance = new AppearanceSettings();
        Palette = ResolvePalette(Appearance);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Dịch vụ chủ đề duy nhất của tiến trình này.</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>Tạo và cài đặt thực thể cấp tiến trình.</summary>
    public static ThemeService Initialize(Dispatcher dispatcher)
    {
        Current?.Dispose();
        Current = new ThemeService(dispatcher);
        return Current;
    }

    public AppearanceSettings Appearance { get; private set; }

    /// <summary>Chủ đề thực tế sau khi đã phân giải theo hệ thống.</summary>
    public bool IsDark { get; private set; }

    public GlassPalette Palette { get; private set; }

    /// <summary>
    /// Trả về true khi nên tránh hiệu ứng bán trong suốt: hoặc do người dùng yêu cầu,
    /// hoặc do hiệu ứng trong suốt của Windows đã tắt,
    /// khiến bảng bán trong suốt trông lạc lõng khi đặt cạnh phần còn lại của màn hình nền.
    /// </summary>
    public bool UseOpaquePanels { get; private set; }

    /// <summary>Kích hoạt trên luồng UI sau khi bất kỳ giá trị nào thay đổi.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Áp dụng thiết lập giao diện mới và thông báo cho từng bên lắng nghe.
    /// Dù thực tế có thay đổi hay không, việc gọi hàm này luôn an toàn.
    /// </summary>
    public void Update(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        Appearance = appearance;
        Refresh();
    }

    /// <summary>Phân giải lại bảng màu và kích hoạt <see cref="Changed"/>.</summary>
    public void Refresh()
    {
        IsDark = Appearance.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,

            // Theo hệ điều hành. Nếu giá trị registry bị thiếu hoặc không đọc được thì xử lý như Sáng.
            _ => !SystemThemeReader.IsLightTheme(),
        };

        UseOpaquePanels = Appearance.ReduceTransparency || !SystemThemeReader.IsTransparencyEnabled();
        Palette = GlassPalette.Create(IsDark, GlassPalette.ParseAccent(Appearance.AccentColor), UseOpaquePanels);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Tắt hoạt ảnh khi người dùng yêu cầu,
    /// và cũng tắt khi Windows báo tùy chọn giảm chuyển động thông qua cùng khối thiết lập đó.
    /// </summary>
    public bool AnimationsEnabled => !Appearance.ReduceMotion;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _disposed = true;

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Kích hoạt trên luồng hệ thống. Chỉ các chế độ theo hệ thống và hiệu ứng trong suốt do hệ điều hành điều khiển mới cần làm mới,
        // và mỗi lần đọc các thuộc tính này đều thuộc quyền sở hữu của luồng UI.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                // Shell icon visibility also sends General notifications. Broadcast
                // only a changed resolved palette; otherwise every glass surface
                // unnecessarily discards its completed desktop material.
                var next = ResolvePalette(Appearance);
                if (Palette.IsDark == next.IsDark && Palette.Accent == next.Accent
                    && Palette.FallbackPanelBackground == next.FallbackPanelBackground) return;
                Refresh();
            }
        });
    }

    private static GlassPalette ResolvePalette(AppearanceSettings appearance)
    {
        var isDark = appearance.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemThemeReader.IsLightTheme(),
        };

        var opaque = appearance.ReduceTransparency || !SystemThemeReader.IsTransparencyEnabled();
        return GlassPalette.Create(isDark, GlassPalette.ParseAccent(appearance.AccentColor), opaque);
    }

    /// <summary>Phương thức tiện lợi cho bên gọi chỉ muốn biết hệ điều hành đang cấu hình thế nào.</summary>
    public static bool SystemPrefersDark() => !SystemThemeReader.IsLightTheme();

    /// <summary>Trả về một dòng trạng thái đã phân tích để phục vụ chẩn đoán.</summary>
    public string Describe() =>
        $"Chủ đề: {Appearance.Theme} (đang dùng {(IsDark ? "Tối" : "Sáng")})" +
        $", bảng nền đục: {(UseOpaquePanels ? "Có" : "Không")}" +
        $", hoạt ảnh: {(AnimationsEnabled ? "Bật" : "Tắt")}" +
        $"; {WindowsCapabilities.Describe()}";
}
