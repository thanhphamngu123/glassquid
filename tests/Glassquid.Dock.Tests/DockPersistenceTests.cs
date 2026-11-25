using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.UI.Settings;

namespace Glassquid.Dock.Tests;

public class DockPersistenceTests
{
    [Fact]
    public void ExternalDropPinFailure_DockShowsErrorAndLeavesNoUnsavedPin()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Glassquid-dock-drop-failure-" + Guid.NewGuid().ToString("N"));
            DockWindow? window = null;
            try
            {
                var data = new GlassquidDataStore(new AppPaths(root));
                data.LoadAll();
                var path = Path.Combine(root, "tep-keo-tha.txt");
                File.WriteAllText(path, "dock drop");
                using var host = new AppServiceHost(data);
                var settings = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window = new DockWindow();
                window.AttachServices(host, settings, () => { });

                using (var blocker = new FileStream(
                    data.Layouts.DockFilePath + AtomicFile.TempSuffix,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    var payload = new DataObject(DataFormats.FileDrop, new[] { path });
                    var drop = (DragEventArgs)Activator.CreateInstance(
                        typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                        binder: null, args: [payload, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(20, 20)],
                        culture: null)!;
                    drop.RoutedEvent = DragDrop.DropEvent;
                    window.RaiseEvent(drop);
                }

                Assert.Empty(data.Layouts.Dock.PinnedItemIds);
                Assert.Equal(0, window.TileCount);
                Assert.IsType<Button>(window.FindName("LauncherButton"));
                Assert.Contains("Không thể lưu", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void CleanupLegacyPinsWriteFailure_DockStillStarts()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Glassquid-dock-clean-failure-" + Guid.NewGuid().ToString("N"));
            DockWindow? window = null;
            try
            {
                var data = new GlassquidDataStore(new AppPaths(root));
                data.LoadAll();
                var entry = new AppEntry
                {
                    Id = AppLibraryStore.NewId(),
                    DisplayName = "Ứng dụng hợp lệ",
                    Kind = AppItemKind.Program,
                    Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
                };
                data.Library.Add(entry);
                data.Layouts.Dock.PinnedItemIds.AddRange([entry.Id, "missing-entry"]);
                data.Layouts.SaveDock();

                using var host = new AppServiceHost(data);
                var settings = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window = new DockWindow();
                using (var blocker = new FileStream(
                    data.Layouts.DockFilePath + AtomicFile.TempSuffix,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    window.AttachServices(host, settings, () => { });
                }

                Assert.Equal([entry.Id, "missing-entry"], data.Layouts.Dock.PinnedItemIds);
                Assert.Equal(1, window.TileCount);
                Assert.IsType<Button>(window.FindName("LauncherButton"));
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void UnpinWriteFailure_WindowRemainsUsableAndPinPersists()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Glassquid-dock-write-failure-" + Guid.NewGuid().ToString("N"));
            DockWindow? window = null;
            try
            {
                var data = new GlassquidDataStore(new AppPaths(root));
                data.LoadAll();
                var entry = new AppEntry
                {
                    Id = AppLibraryStore.NewId(),
                    DisplayName = "Ứng dụng thử nghiệm ghim",
                    Kind = AppItemKind.Program,
                    Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
                };
                data.Library.Add(entry);
                data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
                data.Layouts.SaveDock();

                using var host = new AppServiceHost(data);
                var settings = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window = new DockWindow();
                window.AttachServices(host, settings, () => { });

                using (var blocker = new FileStream(
                    data.Layouts.DockFilePath + AtomicFile.TempSuffix,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    var handler = typeof(DockWindow).GetMethod("OnUnpinRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    handler.Invoke(window, [window, entry.Id]);
                }

                Assert.Contains(entry.Id, data.Layouts.Dock.PinnedItemIds);
                Assert.Equal(1, window.TileCount);
                Assert.IsType<Button>(window.FindName("LauncherButton"));
                Assert.Contains("Không thể lưu", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
