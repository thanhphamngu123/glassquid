using System.Windows.Interop;
using Glassquid.Core.Models.Settings;
using Glassquid.Dock;
using Glassquid.Platform;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Kiểm thử khói tích hợp cho kiểu cửa sổ Dock: tạo cửa sổ và tay cầm thật, rồi đọc lại kiểu thật.
/// </summary>
/// <remarks>
/// Đây là kiểm thử tích hợp trên Windows, chỉ chứng minh rằng các bit kiểu đã được gắn.
/// Hành vi tiêu điểm thực tế "cửa sổ nền trước không đổi sau khi nhấp" thuộc mục thủ công/SendInput trong danh sách nghiệm thu,
/// không thể thay thế bằng kiểm thử này.
/// </remarks>
public class DockWindowStyleTests
{
    [Fact]
    public void AfterShow_HasWsExNoActivate()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                window.Show();

                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.True(
                    WindowStyles.IsNoActivate(handle),
                    "Khi nhấp vào Dock không được đẩy ứng dụng người dùng đang dùng xuống nền (lỗ hổng được ghi nhận ở giai đoạn 1)");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WithoutHostAttached_SettingsUseDefaults()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                Assert.Equal(DockSettings.FixedIconSize, window.Settings.IconSize);
                Assert.Equal(DockSettings.FixedIconSpacing, window.Settings.IconSpacing);
                Assert.Equal(1.35, window.Settings.MagnificationPeak);
                Assert.Equal(40, window.PanelCornerRadius.TopLeft);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
