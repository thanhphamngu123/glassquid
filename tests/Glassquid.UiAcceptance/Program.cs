using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.Launcher;
using Glassquid.Platform;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.UiAcceptance;

// This runner drives production WPF handlers/routed events, NOT injected hardware input.
// Keep it outside the shipping app. It never opens user documents or minimizes other apps.
internal static class Program
{
    private static readonly List<object> Results = [];
    // Evidence is read by people: the default encoder would turn every Chinese character
    // into \uXXXX.
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static int failures;
    private static string root = "";
    private static DockWindow dock = null!;
    private static Window target = null!;
    private static GlassquidDataStore data = null!;
    private static AppServiceHost host = null!;
    private static DockSettingsHost settings = null!;
    private static AppEntry sample = null!;
    private static string CopyTarget(string folder = "Mục tiêu mới Tiếng Việt")
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
        return Path.Combine(directory, "Glassquid.UiAcceptance.exe");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Glassquid.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        if (args.Length == 2 && args[0] == "--drag-source")
        {
            var sourceApp = new Application();
            var label = new TextBlock { Text = "Kéo tệp này vào Dock:\n" + Path.GetFileName(args[1]),
                FontSize = 26, Margin = new Thickness(50), Padding = new Thickness(30), Background = Brushes.LightBlue };
            var sourceWindow = new Window { Title = "Glassquid isolated file drag source", WindowState = WindowState.Maximized,
                Content = new StackPanel { Children = { label } } };
            label.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(label, new DataObject(DataFormats.FileDrop, new[] { args[1] }), DragDropEffects.Copy);
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timer.Tick += (_, _) => sourceApp.Shutdown(); timer.Start();
            return sourceApp.Run(sourceWindow);
        }
        if (args.Contains("--owned-target"))
        {
            var targetApp = new Application();
            var owned = new Window { Title = "Glassquid isolated launch target", Width = 280, Height = 130 };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (_, _) => targetApp.Shutdown(); timer.Start();
            return targetApp.Run(owned);
        }
        var reportIndex = Array.IndexOf(args, "--report");
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length ? Path.GetFullPath(args[reportIndex + 1])
            : Path.GetFullPath("phase4-ui.json");
        root = Path.Combine(Path.GetTempPath(), "Glassquid-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, failed = 1, status = "timeout",
                detail = "UI acceptance exceeded 120 seconds; no interactive input was supplied or a scenario hung.",
                dataDirectory = root
            }, ReportJson));
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(120), System.Threading.Timeout.InfiniteTimeSpan);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                // --setup-only: chỉ dựng sẵn trạng thái (cơ sở dữ liệu, Dock, mẫu), không chạy 15 ca sự kiện định tuyến đó.
                // Driver desktop cần trạng thái này khi tiếp quản bộ chọn gốc của hệ thống: các ca đó và việc
                // "bộ chọn có thể được thao tác bằng chuột thật hay không" là hai chuyện khác nhau; chạy lẫn vào nhau
                // sẽ khiến nguyên nhân thất bại của từng bước trở nên khó phân biệt.
                await Run(includeCases: !args.Contains("--setup-only"));
                if (args.Contains("--picker")) await NativePicker();
                if (args.Contains("--drag")) await NativeDrag();
            }
            catch (Exception ex) { Record("runner", false, ex.ToString()); }
            finally
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                host?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.Now, dataDirectory = root,
                    driver = "production WPF handlers and routed events; no hardware input injection",
                    passed = Results.Count - failures, failed = failures, checks = Results,
                    externalCoverage = "Native picker, Explorer drag, packaged/UWP activation, physical hotplug and global desktop action are NOT asserted by this runner."
                }, ReportJson));
                Console.WriteLine($"Report: {report}; passed={Results.Count - failures}, failed={failures}");
                app.Shutdown(failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task NativeDrag()
    {
        var path = Path.Combine(root, "Liên tiến trình Tiếng Việt có dấu cách.txt"); File.WriteAllText(path, "cross process OLE");
        using var source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, ArgumentList = { "--drag-source", path }
        })!;
        try
        {
            await Case("real cross-process CF_HDROP gesture preserves Chinese space path", async () =>
            {
                var timeout = Stopwatch.StartNew();
                while (!host.Library.Items.Any(x => x.Target == path))
                {
                    Require(timeout.Elapsed < TimeSpan.FromSeconds(80), "no external drop received before deadline");
                    await Task.Delay(100);
                }
                var imported = host.Library.Items.Single(x => x.Target == path);
                var reload = new GlassquidDataStore(data.Paths); reload.LoadAll();
                Require(reload.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "cross-process drop not persisted");
            });
        }
        finally { if (!source.HasExited) source.Kill(); }
    }

    private static async Task NativePicker()
    {
        // Ở đây cố ý [KHÔNG] sửa thuộc tính cửa sổ của Dock (hiển thị trên thanh tác vụ, tiêu đề, kích thước, vị trí).
        // Trước đây để chiều theo driver chỉ có thể đoán theo tọa độ, fixture này phóng to Dock lên 1100x750
        // và cho nó xuất hiện trên thanh tác vụ; điều đó đã làm thay đổi hình thái cửa sổ sản phẩm, nên số liệu đọc ra không còn là dữ liệu sản phẩm thật nữa.
        // Hiện tại tests/Glassquid.DesktopAcceptance đảm nhận việc này: driver đó tìm hộp thoại gốc theo tiến trình + lớp cửa sổ,
        // không phụ thuộc vào kích thước hay việc hiển thị trên thanh tác vụ của cửa sổ chủ.
        var replacement = CopyTarget("Định vị lại Mục tiêu mới");
        sample.Arguments = "--owned-target";
        data.Layouts.Dock.PinnedItemIds = [sample.Id]; data.Layouts.SaveDock(); dock.RefreshTiles();
        Console.WriteLine("PICKER_REPLACEMENT=" + replacement);
        await Case("native picker relocation and real click launch of isolated executable", async () =>
        {
            Call("RelocateFromDialog", sample);
            await Task.Delay(150);
            var entry = host.Library.Find(sample.Id)!;
            Require(entry.Target == replacement && !entry.IsTargetMissing, "picker did not relocate to known replacement");
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            Require(Descendants<Image>(tile).Any(x => x.Source is not null && x.Opacity == 1), "replacement icon missing or dimmed");
            Call("OnTileClicked", dock, sample.Id);

            // Thăm dò chờ cửa sổ xuất hiện, không dùng thời gian chờ cố định: trên máy dev 800 ms là đủ, trong máy ảo chậm thì không đủ —
            // trên máy khách hàng, khẳng định này đã thất bại ba vòng liên tiếp chỉ vì chờ quá ngắn (bản thân việc định vị lại đã thành công).
            var appeared = false;
            for (var attempt = 0; attempt < 40 && !appeared; attempt++)
            {
                await Task.Delay(250);
                appeared = Glassquid.Platform.Shell.WindowLocator.FindWindowsForImage(replacement).Count > 0;
            }

            var candidates = Glassquid.Platform.Shell.WindowLocator.FindWindowsForImage(replacement);
            Require(appeared && candidates.Count > 0, "relocated executable window did not appear");
            foreach (var candidate in candidates) WindowControl.Close(candidate.Handle);
        });
    }

    private static async Task Run(bool includeCases = true)
    {
        data = new GlassquidDataStore(new AppPaths(Path.Combine(root, "data")));
        data.LoadAll(); host = new AppServiceHost(data);
        var file = Path.Combine(root, "Tiếng Việt có dấu cách.txt"); File.WriteAllText(file, "acceptance");
        sample = host.Import.ImportItems([new ImportRequest { Source = file }]).Added.Single();
        data.Layouts.Dock.PinnedItemIds.Add(sample.Id); data.Layouts.SaveDock();
        ThemeService.Initialize(Application.Current.Dispatcher).Update(data.Settings.Current.Appearance);
        settings = DockSettingsHost.Initialize(Application.Current.Dispatcher);
        settings.Update(data.Settings.Current.Dock);
        target = new Window { Title = "Glassquid acceptance owned target", Width = 280, Height = 150, Left = 60, Top = 60 };
        target.Show();
        var launcher = new LauncherWindow();
        dock = new DockWindow();
        dock.AttachServices(host, settings, () => { launcher.Show(); launcher.Activate(); });
        dock.Show(); await Task.Delay(200);

        // --setup-only dừng tại đây: trạng thái đã được dựng xong; driver tiếp quản bộ chọn gốc hoặc thao tác kéo thả
        // cần đúng trạng thái này, chứ không phải 15 ca sự kiện định tuyến này.
        if (!includeCases) return;

        await Case("production Dock keeps noactivate and no-taskbar", () =>
        {
            Require(WindowStyles.IsNoActivate(Handle(dock)) && !dock.ShowInTaskbar, "production window styles changed");
            return Task.CompletedTask;
        });
        await Case("context menu cancellation restores previous foreground", async () =>
        {
            await ForegroundTarget();
            Call("OnTileContextMenuRequested", dock, sample.Id);
            await Task.Delay(150);
            var menu = Roots<ContextMenu>().Single(x => x.IsOpen);
            menu.IsOpen = false;
            await Task.Delay(200);
            Require(WindowControl.GetForegroundWindow() == Handle(target), "closing menu left foreground on Dock/another window");
        });
        await Case("chooser Escape closes and restores foreground", async () =>
        {
            await ForegroundTarget();
            Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
            var list = Roots<ListBox>().Single();
            RaiseKey(list, Key.Escape);

            // Khẳng định vẫn là cùng một kết quả quan sát được (lớp phủ đóng lại + tiêu điểm nền trước trả về cửa sổ gốc), chỉ là không dùng chờ cố định để
            // canh thời gian nữa: trên máy dev này 200 ms là đủ, trong máy ảo chậm thì không — đo thực tế trên máy khách hàng thì ca này
            // đã thất bại ba vòng liên tiếp vì 200 ms cố định quá ngắn. Đổi sang thăm dò chờ, môi trường chậm sẽ đợi thêm một chút,
            // còn khi sản phẩm thực sự không đóng lớp phủ thì vẫn sẽ thất bại.
            Require(await UntilOrFalse(() => !Roots<ListBox>().Any()), "Escape did not close chooser");
            Require(await UntilOrFalse(() => WindowControl.GetForegroundWindow() == Handle(target)), "Escape did not restore target");
        });
        await Case("chooser Enter activates selected real HWND", async () =>
        {
            var second = new Window { Title = "Glassquid acceptance second target", Width = 220, Height = 120 };
            try
            {
                second.Show(); await ForegroundTarget();
                Call("ShowWindowChooser", Candidates(target, second)); await Task.Delay(100);
                var list = Roots<ListBox>().Single(); list.SelectedIndex = 1;
                RaiseKey(list, Key.Enter); await Task.Delay(200);
                Require(WindowControl.GetForegroundWindow() == Handle(second), "selected second window was not foreground");
                Require(!Roots<ListBox>().Any(), "chooser remained open");
            }
            finally { second.Close(); }
        });
        await Case("chooser clicking rendered title activates its row", async () =>
        {
            await ForegroundTarget();
            Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
            var list = Roots<ListBox>().Single();
            var text = Descendants<TextBlock>(list).First(x => x.Text == target.Title);
            text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            await Task.Delay(200);
            Require(!Roots<ListBox>().Any() && WindowControl.GetForegroundWindow() == Handle(target), $"title route failed: list={Roots<ListBox>().Count()}, foreground={WindowControl.GetForegroundWindow()}, expected={Handle(target)}");
        });
        await Case("chooser cancellation does not steal foreground from outside window", async () =>
        {
            var outside = new Window { Title = "Glassquid acceptance outside", Width = 220, Height = 100 };
            try
            {
                outside.Show(); await ForegroundTarget();
                Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
                var list = Roots<ListBox>().Single();
                outside.Activate();
                RaiseKey(list, Key.Escape); await Task.Delay(200);
                Require(WindowControl.GetForegroundWindow() == Handle(outside), "cancel stole foreground from outside target");
            }
            finally { outside.Close(); }
        });
        await Case("real Dock save excludes built-ins and survives reload", () =>
        {
            Call("SaveDockOrder", (object)new List<string> { "entry:launcher", sample.Id, sample.Id, "entry:recyclebin" });
            var reload = new GlassquidDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.SequenceEqual([sample.Id]), "saved order contains non-library ids");
            return Task.CompletedTask;
        });
        await Case("old contaminated layout cleaned on actual Dock attach", () =>
        {
            data.Layouts.Dock.PinnedItemIds = ["entry:launcher", sample.Id, "entry:showdesktop"];
            data.Layouts.SaveDock(); dock.AttachServices(host, settings, () => { launcher.Show(); });
            var reload = new GlassquidDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.SequenceEqual([sample.Id]), "migration failed");
            return Task.CompletedTask;
        });
        await Case("missing target is dimmed and click presents failure without shell dialog", () =>
        {
            File.Delete(file); host.Maintenance.RefreshMissingTargets(); Call("RefreshTiles");
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            Require(Descendants<Image>(tile).Any(x => x.Opacity < 1), "missing target not visually dimmed");
            Call("OnTileClicked", dock, sample.Id);
            var status = (TextBlock)dock.FindName("StatusText");
            Require(status.Text.Contains("mục tiêu") || status.Text.Contains("vị trí"), "missing failure notice not presented");
            return Task.CompletedTask;
        });
        await Case("launcher entry opens actual launcher via callback", () =>
        {
            Call("OnTileClicked", dock, "entry:launcher");
            Require(launcher.IsVisible, "launcher did not appear"); launcher.Hide();
            return Task.CompletedTask;
        });
        await Case("icon size setting updates shown Dock without restart", async () =>
        {
            await Task.Delay(150);
            settings.Settings.IconSize = 64; settings.Refresh(); await Task.Delay(100);
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            var image = Descendants<Image>(tile).Single();
            Require(image.ActualWidth >= 64 && image.ActualWidth <= 86.5, $"rendered icon width={image.ActualWidth}, expected 64..86.4");
        });
        await Case("folder panel Escape closes and returns foreground", async () =>
        {
            var folder = host.Import.ImportItems([new ImportRequest { Source = root }]).Added.Single();
            await ForegroundTarget(); Call("ShowFolderPanel", folder); await Task.Delay(100);
            var panel = Roots<Border>().Single(x => x.Child is ScrollViewer && x.Parent is Popup);
            RaiseKey(panel, Key.Escape); await Task.Delay(150);
            Require(!Roots<Border>().Any(x => x.Child is ScrollViewer && x.Parent is Popup), "folder panel stayed open after Escape");
            Require(WindowControl.GetForegroundWindow() == Handle(target), "folder panel did not restore previous foreground");
        });
        await Case("file-drop routed event imports Chinese space path and repins duplicate", () =>
        {
            var dropped = Path.Combine(root, "Kéo vào Tiếng Việt có dấu cách.txt"); File.WriteAllText(dropped, "drop test");
            var payload = new DataObject(DataFormats.FileDrop, new[] { dropped });
            void Drop()
            {
                var e = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [payload, DragDropKeyStates.None, DragDropEffects.Copy, dock, new Point(20, 20)], null)!;
                e.RoutedEvent = DragDrop.DropEvent; dock.RaiseEvent(e);
            }
            Drop();
            var imported = host.Library.Items.Single(x => x.Target == dropped);
            Require(data.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "drop did not pin imported item");
            Call("OnUnpinRequested", dock, imported.Id); Drop();
            var reload = new GlassquidDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "duplicate drop did not repin");
            Require(reload.Library.Items.Count(x => x.Target == dropped) == 1, "duplicate drop duplicated library entry");
            return Task.CompletedTask;
        });
        await Case("real Dock launches isolated process then activates same window and updates running dot", async () =>
        {
            var executable = CopyTarget();
            var entry = host.Import.ImportItems([new ImportRequest { Source = executable }]).Added.Single();
            entry.Arguments = "--owned-target";
            data.Layouts.Dock.PinnedItemIds.Add(entry.Id); data.Layouts.SaveDock();
            host.Running.Start(); dock.RefreshTiles();
            uint ownedPid = 0;
            try
            {
                Call("OnTileClicked", dock, entry.Id);
                ownedPid = host.Launch.LastLaunchedProcessId;
                Require(ownedPid != 0, "launch did not record owned PID");
                await Until(() => Glassquid.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Count == 1);
                var window = Glassquid.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Single();
                WindowControl.Minimize(window.Handle);
                await Until(() => WindowControl.IsMinimized(window.Handle));
                Call("OnTileClicked", dock, entry.Id);
                await Until(() => WindowControl.GetForegroundWindow() == window.Handle);
                Require(Glassquid.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Count == 1, "second click launched duplicate");
                await Until(() => host.Running.IsRunning(entry));
                dock.RefreshTiles();
                var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == entry.Id);
                Require(Descendants<System.Windows.Shapes.Ellipse>(tile).Single().Visibility == Visibility.Visible, "running dot not visible");
                WindowControl.Close(window.Handle);
                await Until(() => !host.Running.IsRunning(entry));
                dock.RefreshTiles();
                tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == entry.Id);
                Require(Descendants<System.Windows.Shapes.Ellipse>(tile).Single().Visibility != Visibility.Visible, "running dot remained after target closed");
            }
            finally
            {
                if (ownedPid != 0)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)ownedPid);
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) process.Kill();
                    }
                    catch (ArgumentException) { }
                }
            }
        });
        await Case("unpin through real menu persists but retains library entry", async () =>
        {
            Call("OnTileContextMenuRequested", dock, sample.Id); await Task.Delay(50);
            var menu = Roots<ContextMenu>().Single(x => x.IsOpen);
            var remove = menu.Items.OfType<MenuItem>().SingleOrDefault(x => Equals(x.Header, "Bỏ ghim khỏi Dock"))
                ?? throw new InvalidOperationException("menu headers=" + string.Join("|", menu.Items.OfType<MenuItem>().Select(x => x.Header)));
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            var reload = new GlassquidDataStore(data.Paths); reload.LoadAll();
            Require(!reload.Layouts.Dock.PinnedItemIds.Contains(sample.Id) && reload.Library.Find(sample.Id) is not null,
                "unpin deleted library or failed persistence");
        });
    }

    private static async Task ForegroundTarget()
    {
        target.Activate(); await Task.Delay(100);
        Require(WindowControl.GetForegroundWindow() == Handle(target), "precondition: owned target cannot become foreground");
    }
    private static List<WindowCandidate> Candidates(params Window[] windows) => windows.Select(w => new WindowCandidate
        { Handle = Handle(w), Title = w.Title }).ToList();
    private static nint Handle(Window w) => new WindowInteropHelper(w).Handle;
    private static void RaiseKey(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(element), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    private static void Call(string name, params object?[] arguments) => typeof(DockWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(dock, arguments);
    private static IEnumerable<T> Roots<T>() where T : DependencyObject => PresentationSource.CurrentSources
        .OfType<HwndSource>().Where(s => s.RootVisual is not null).ToArray().SelectMany(s => Descendants<T>(s.RootVisual));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(8), "timed out waiting for window/tracker state");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Chờ đến khi điều kiện đúng; hết thời gian chờ trả về false thay vì ném ngoại lệ, để bên gọi tự quyết định báo lỗi gì.
    /// </summary>
    /// <remarks>
    /// Dùng cho các khẳng định kiểu "lớp phủ đóng lại" hay "trả lại tiêu điểm nền trước": bản thân kết quả quan sát được không đổi, chỉ là không giả định tốc độ xử lý của môi trường.
    /// Trên máy dev chỉ vài chục mili giây là đủ, trong máy ảo chậm cần vài trăm mili giây đến vài giây; chờ cố định sẽ đánh giá nhầm môi trường chậm thành lỗi sản phẩm.
    /// </remarks>
    private static async Task<bool> UntilOrFalse(Func<bool> predicate, int timeoutMs = 8000)
    {
        var timeout = Stopwatch.StartNew();

        while (timeout.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return predicate();
    }
    private static async Task Case(string name, Func<Task> action)
    {
        try { await action(); Record(name, true, "verified"); }
        catch (Exception ex)
        {
            string? capture = null;
            try
            {
                capture = Path.Combine(root, $"failure-{Results.Count + 1}.png");
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(dock.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(dock.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dock);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(capture); png.Save(stream);
            }
            catch (Exception captureError) { capture = "capture unavailable: " + captureError.Message; }
            Record(name, false, ex + Environment.NewLine + "Dock WPF render: " + capture);
        }
        finally
        {
            foreach (var menu in Roots<ContextMenu>().ToArray()) menu.IsOpen = false;
            foreach (var popup in Roots<FrameworkElement>().Select(x => x.Parent).OfType<Popup>().Distinct().ToArray()) popup.IsOpen = false;
            await Task.Delay(50);
        }
    }
    private static void Record(string name, bool pass, string detail)
    {
        if (!pass) failures++;
        Results.Add(new { name, pass, detail }); Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: {detail}");
    }
}
