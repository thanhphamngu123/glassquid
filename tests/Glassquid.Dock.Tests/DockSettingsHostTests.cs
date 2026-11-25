using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Settings;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Hợp đồng giao diện thông báo cài đặt thanh Dock: khi đổi snapshot phải đổi tham chiếu và phải gửi thông báo,
/// sau khi sửa tại chỗ thì thông báo bổ sung không được đổi tham chiếu.
/// </summary>
public class DockSettingsHostTests
{
    [Fact]
    public void Update_SwapsSnapshotAndRaisesChangedOnSameThread()
    {
        var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);

        var fired = 0;
        object? notifiedBy = null;
        host.Changed += (sender, _) =>
        {
            fired++;
            notifiedBy = sender;
        };

        var next = new DockSettings { IconSize = 56, MagnificationPeak = 1.5 };
        host.Update(next);

        Assert.Equal(1, fired);
        Assert.Same(host, notifiedBy);
        Assert.Same(next, host.Settings);
        Assert.Equal(56, host.Settings.IconSize);
    }

    [Fact]
    public void Refresh_PreservesReferenceAndRaisesChanged()
    {
        var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
        var original = host.Settings;

        var fired = 0;
        host.Changed += (_, _) => fired++;

        // Cách dùng khi màn hình cài đặt sửa giá trị tại chỗ: sửa xong gọi Refresh, tham chiếu snapshot giữ nguyên.
        host.Settings.IconSize = 64;
        host.Refresh();

        Assert.Equal(1, fired);
        Assert.Same(original, host.Settings);
        Assert.Equal(64, host.Settings.IconSize);
    }

    [Fact]
    public void Initialize_SetsProcessLevelInstance()
    {
        try
        {
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            Assert.Same(host, DockSettingsHost.Current);
        }
        finally
        {
            // Thực thể tĩnh không được rò rỉ sang các test case khác. Setter của Current là private,
            // khi dùng reflection phải lấy rõ phương thức set không công khai, PropertyInfo.SetValue sẽ từ chối.
            var current = typeof(DockSettingsHost).GetProperty(nameof(DockSettingsHost.Current))!;
            current.GetSetMethod(true)!.Invoke(null, new DockSettingsHost?[] { null });
        }
    }
}
