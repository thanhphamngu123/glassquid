using System.Windows.Interop;
using Glassquid.Dock;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Các mục tích hợp sẵn độc lập với ô ứng dụng được ghim; chuỗi thu nhỏ ở tầng thấp cho chức năng hiện màn hình nền vẫn được bao phủ bởi kiểm chứng nền tảng.
/// </summary>
public class DockEntryTileTests
{
    [Fact]
    public void BuiltinEntriesAreIndependentFromPinnedTilesWhenNoItemsArePinned()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                window.Show();

                Assert.Equal(0, window.TileCount);
                Assert.IsType<System.Windows.Controls.Button>(window.FindName("LauncherButton"));
                Assert.IsType<System.Windows.Controls.Button>(window.FindName("ThisPcButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MinimizeAndRestorePipelineWorks()
    {
        // "Hiện màn hình nền" (D-13) được triển khai bằng cách gọi SW_MINIMIZE cho từng cửa sổ;
        // ca kiểm thử này khóa chặt hành vi thu nhỏ/khôi phục của WindowControl trên cửa sổ thực.
        StaTest.Run(() =>
        {
            var target = new System.Windows.Window
            {
                Width = 200,
                Height = 100,
                WindowStyle = System.Windows.WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Content = new System.Windows.Controls.Border(),
            };

            try
            {
                target.Show();
                var handle = new WindowInteropHelper(target).Handle;

                Assert.False(WindowControl.IsMinimized(handle));

                _ = WindowControl.Minimize(handle);
                Assert.True(WindowControl.IsMinimized(handle), "Sau SW_MINIMIZE cửa sổ phải ở trạng thái thu nhỏ");

                _ = WindowControl.Restore(handle);
                Assert.False(WindowControl.IsMinimized(handle));
            }
            finally
            {
                target.Close();
            }
        });
    }
}
