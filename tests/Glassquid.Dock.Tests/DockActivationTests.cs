using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.Platform;
using Glassquid.Platform.Shell;
using Glassquid.UI.Settings;

namespace Glassquid.Dock.Tests;

public class DockActivationTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void ClickForegroundAppMinimizes_ClickAgainRestoresOriginalWindowState(WindowState initialState)
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            target.WindowState = initialState;
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());

            ClickTile(dock, entry.Id);

            Assert.True(WindowControl.IsMinimized(handle), "Nhấn lại biểu tượng Dock của ứng dụng đang ở tiền cảnh phải thu nhỏ cửa sổ");
            ClickTile(dock, entry.Id);

            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
            Assert.Equal(initialState, target.WindowState);
        }));
    }

    [Fact]
    public void ClickBackgroundAppBringsToFrontOnly_DoesNotMinimize()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var other = new Window { Title = "Owned foreground", Owner = target, Width = 160, Height = 100 };
            try
            {
                other.Show();
                var handle = new WindowInteropHelper(target).Handle;
                Assert.True(WindowLocator.TryBringToForeground(new WindowInteropHelper(other).Handle));
                Assert.NotEqual(handle, WindowControl.GetForegroundWindow());

                ClickTile(dock, entry.Id);

                Assert.False(WindowControl.IsMinimized(handle));
                Assert.Equal(handle, WindowControl.GetForegroundWindow());
            }
            finally { other.Close(); }
        }));
    }

    [Fact]
    public void GenericLaunchEntryReactivatingForegroundWindow_DoesNotMinimize()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));

            Assert.Equal(ActivationOutcome.Activated, host.Launch.ActivateOrLaunch(entry.Id).Outcome);

            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
        }));
    }

    [Fact]
    public void ContextMenuOpeningForegroundApp_DoesNotMinimize()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));
            typeof(DockWindow).GetMethod("OpenEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dock, [entry.Id, false]);
            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
        }));
    }

    [Fact]
    public void MultipleWindowsOfSameAppStillShowSelectionList()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var second = new Window { Title = "Second app window", Width = 160, Height = 100 };
            try
            {
                second.Show();
                ClickTile(dock, entry.Id);

                Assert.False(WindowControl.IsMinimized(new WindowInteropHelper(target).Handle));
                Assert.False(WindowControl.IsMinimized(new WindowInteropHelper(second).Handle));
                Assert.Equal(ActivationOutcome.MultipleWindowsFound, host.Launch.ActivateOrLaunch(entry.Id).Outcome);
            }
            finally { second.Close(); }
        }));
    }

    private static void ClickTile(DockWindow dock, string entryId)
    {
        typeof(DockWindow).GetMethod("OnTileClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dock, [dock, entryId]);
        // Native focus changes complete through the UI message loop, as they do after a real click.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void WithWindows(Action<DockWindow, AppServiceHost, AppEntry, Window> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glassquid-dock-activation-" + Guid.NewGuid().ToString("N"));
        DockWindow? dock = null;
        Window? target = null;
        try
        {
            var data = new GlassquidDataStore(new AppPaths(root));
            data.LoadAll();
            var entry = new AppEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Activation fixture",
                Kind = AppItemKind.Program,
                Target = Environment.ProcessPath!,
            };
            data.Library.Add(entry);
            using var host = new AppServiceHost(data);
            dock = new DockWindow();
            dock.AttachServices(host, DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher), () => { });
            dock.Show();
            target = new Window { Title = "Activation fixture", Width = 240, Height = 160 };
            target.Show();
            Assert.Single(WindowMatcher.FindWindows(entry));
            body(dock, host, entry, target);
        }
        finally
        {
            target?.Close();
            dock?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
