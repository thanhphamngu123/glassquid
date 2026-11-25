using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.Dock;
using Glassquid.UI.Settings;
using Xunit;

namespace Glassquid.Dock.Tests;

public class SideDocksTopPlacementTests
{
    [Fact]
    public void SearchDockWindow_IsTopPlacement_FollowsDockSettings()
    {
        StaTest.Run(() =>
        {
            var searchDock = new SearchDockWindow();
            try
            {
                var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
                host.Update(new DockSettings { Placement = DockPlacement.BottomCenter });
                searchDock.AttachDockSettings(host);

                Assert.False(searchDock.IsTopPlacement);

                host.Update(new DockSettings { Placement = DockPlacement.TopCenter });
                Assert.True(searchDock.IsTopPlacement);

                host.Update(new DockSettings { Placement = DockPlacement.BottomRight });
                Assert.False(searchDock.IsTopPlacement);
            }
            finally
            {
                searchDock.Close();
            }
        });
    }

    [Fact]
    public void SystemTrayBarWindow_IsTopPlacement_FollowsDockSettings()
    {
        StaTest.Run(() =>
        {
            var trayBar = new SystemTrayBarWindow();
            try
            {
                var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
                host.Update(new DockSettings { Placement = DockPlacement.BottomCenter });
                trayBar.AttachDockSettings(host);

                Assert.False(trayBar.IsTopPlacement);

                host.Update(new DockSettings { Placement = DockPlacement.TopCenter });
                Assert.True(trayBar.IsTopPlacement);

                host.Update(new DockSettings { Placement = DockPlacement.BottomLeft });
                Assert.False(trayBar.IsTopPlacement);
            }
            finally
            {
                trayBar.Close();
            }
        });
    }

    [Fact]
    public void SystemTrayBarWindow_IsLeftPlacement_FollowsDockSettings()
    {
        StaTest.Run(() =>
        {
            var trayBar = new SystemTrayBarWindow();
            try
            {
                var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
                host.Update(new DockSettings { SystemTrayOnLeft = false });
                trayBar.AttachDockSettings(host);

                Assert.False(trayBar.IsLeftPlacement);

                host.Update(new DockSettings { SystemTrayOnLeft = true });
                Assert.True(trayBar.IsLeftPlacement);

                host.Update(new DockSettings { SystemTrayOnLeft = false });
                Assert.False(trayBar.IsLeftPlacement);
            }
            finally
            {
                trayBar.Close();
            }
        });
    }
}
