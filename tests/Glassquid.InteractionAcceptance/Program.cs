using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.App;
using Glassquid.App.Startup;
using Glassquid.AppServices;
using Glassquid.Core.Input;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Dock;
using Glassquid.Launcher;
using Glassquid.Platform.Displays;
using Glassquid.Settings;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

internal static class Program
{
    private const int WmHotkey = 0x0312;
    private static readonly List<object> Cases = [];
    private static readonly List<string> Screenshots = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Glassquid.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        Cases.Clear();
        Screenshots.Clear();
        _failed = 0;
        if (!TryParse(args, out var report, out var suppliedData, out var argumentError))
        {
            Console.Error.WriteLine(argumentError);
            return 2;
        }
        var root = suppliedData is null ? Path.Combine(Path.GetTempPath(), "Glassquid-P6-Wpf-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(suppliedData);
        var userRoot = AppPaths.ForCurrentUser().RootDirectory;
        if (DataDirectoryGuard.Overlaps(root, userRoot))
        {
            Console.Error.WriteLine("Refusing a data directory that overlaps the user's Glassquid directory.");
            return 2;
        }
        if (DataDirectoryGuard.Overlaps(report, userRoot))
        {
            report = Path.Combine(Path.GetTempPath(), "Glassquid-P6-refused-" + Guid.NewGuid().ToString("N") + ".json");
            Check("Đường dẫn báo cáo không ghi vào thư mục người dùng thật", false, "Đường dẫn báo cáo được cô lập khỏi dữ liệu người dùng", "Đường dẫn báo cáo trùng với dữ liệu người dùng, chuyển sang ghi vào thư mục tạm");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Glassquid.UI;component/Theming/LiquidGlassTheme.xaml"),
        });
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        Directory.CreateDirectory(evidenceDirectory);
        Directory.CreateDirectory(root);
        try
        {
            RunWpfAcceptance(app, root, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "phase6-settings.png"));
        }
        catch (Exception ex)
        {
            Check("Trình chạy nghiệm thu WPF không có ngoại lệ chưa xử lý", false, "Không có ngoại lệ", ex.ToString());
        }
        finally
        {
            ThemeService.Current?.Dispose();
            app.Shutdown();
            WriteReport(report, root);
            if (suppliedData is null && Directory.Exists(root))
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        Console.WriteLine($"Phase 6 WPF: {Cases.Count - _failed} passed, {_failed} failed. Report: {Path.GetFullPath(report)}");
        return Cases.Count > 0 && _failed == 0 ? 0 : 1;
    }

    private static void RunWpfAcceptance(Application app, string root, string settingsScreenshot)
    {
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var data = new GlassquidDataStore(paths);
        data.LoadAll();
        VerifyPhase6ReportPathProtection(root);
        data.Settings.Current.Launcher.Rows = 2;
        data.Settings.Current.Launcher.Columns = 2;
        data.Settings.Current.Display.DockDisplayId = null;
        data.Settings.Current.Display.SuppressTriggersWhenFullscreen = false;
        data.Settings.Save();
        for (var i = 0; i < 8; i++)
            data.Library.Add(new AppEntry { Id = $"p6-{i}", DisplayName = $"Mẫu tương tác {i}", Target = Path.Combine(root, $"missing-{i}"), IsTargetMissing = true });
        data.Library.Save();

        ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
        using var services = new AppServiceHost(data);
        var primarySession = CapturePrimaryDisplaySession();
        var launcher = new LauncherWindow();
        launcher.BindPrimaryDisplay(primarySession);
        launcher.AttachServices(services, true);
        launcher.ApplySettings(data.Settings.Current.Launcher);
        SettingsWindow? settings = null;
        HwndSource? messageSource = null;
        InteractionController? controller = null;
        var actions = new List<InputAction>();
        var dispatchedIds = new List<int>();
        try
        {
            var sourceParameters = new HwndSourceParameters("Glassquid.Phase6Acceptance.MessageWindow")
            {
                Width = 0, Height = 0, ParentWindow = new nint(-3), WindowStyle = 0,
            };
            messageSource = new HwndSource(sourceParameters);
            messageSource.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == WmHotkey && controller is not null)
                {
                    dispatchedIds.Add(wParam.ToInt32());
                    controller.HandleHotkey(wParam.ToInt32());
                    handled = true;
                }
                return 0;
            });
            controller = new InteractionController(data, primarySession, messageSource.Handle, app.Dispatcher,
                action => action is InputAction.ToggleLauncher || launcher.IsVisible,
                action =>
                {
                    actions.Add(action);
                    switch (action)
                    {
                        case InputAction.ToggleLauncher:
                            if (launcher.IsVisible) launcher.Hide(); else { launcher.PrepareToShow(); launcher.Show(); }
                            break;
                        case InputAction.HideLauncher: launcher.Hide(); break;
                        case InputAction.NextPage: launcher.NavigatePage(1); break;
                        case InputAction.PreviousPage: launcher.NavigatePage(-1); break;
                        // Deliberately do not toggle Explorer: session-level Shell mutation is outside this WPF runner.
                    }
                    controller?.SetLauncherAvailability(launcher.IsVisible, launcher.IsVisible);
                }, message => { });
            Check("Khởi tạo bộ điều phối", controller.Initialize() is null, "Khởi tạo thành công", "Đã gọi Initialize");
            controller.SetLauncherAvailability(false, false);

            var desktopWasReadable = controller.ReadDesktopIcons().HasValue;
            var shortcuts = new ShortcutSettings
            {
                ToggleLauncher = Hotkey(13), HideLauncher = Hotkey(14), NextPage = Hotkey(15), PreviousPage = Hotkey(16),
                ToggleDesktopIcons = desktopWasReadable ? Hotkey(17) : new HotkeyDefinition(),
            };
            var applyError = controller.Apply(shortcuts, data.Settings.Current.Display);
            Check("Cấu hình năm loại thao tác thông qua dịch vụ cài đặt bản phát hành", applyError is null
                && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F13"
                && data.Settings.Current.Shortcuts.HideLauncher.Key == "F14"
                && data.Settings.Current.Shortcuts.NextPage.Key == "F15"
                && data.Settings.Current.Shortcuts.PreviousPage.Key == "F16"
                && data.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled == desktopWasReadable,
                "Đăng ký bốn thao tác cửa sổ; thao tác màn hình nền chỉ đăng ký khi đọc được khả năng", applyError ?? $"Khả năng biểu tượng màn hình nền đọc được={desktopWasReadable}");
            if (!desktopWasReadable)
            {
                var before = File.ReadAllBytes(paths.SettingsFile);
                var requested = SettingsPageEditor.Clone(data.Settings.Current).Shortcuts;
                requested.ToggleDesktopIcons = Hotkey(17);
                var unsupported = controller.Apply(requested, data.Settings.Current.Display);
                Check("Từ chối phím tắt liên quan khi chưa rõ khả năng biểu tượng màn hình nền", unsupported is not null
                    && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                    "Hàm Apply trả về lỗi và cài đặt trên đĩa không đổi", unsupported ?? "Không trả về lỗi");
            }
            var initialReload = new GlassquidDataStore(paths);
            initialReload.LoadAll();
            Check("Cấu hình năm loại thao tác được nạp lại qua thực thể dữ liệu mới", initialReload.Settings.Current.Shortcuts.ToggleLauncher.Key == "F13"
                && initialReload.Settings.Current.Shortcuts.PreviousPage.Key == "F16"
                && initialReload.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled == desktopWasReadable,
                "Cài đặt phím tắt nạp lại khớp với giá trị đã áp dụng", $"Toggle={initialReload.Settings.Current.Shortcuts.ToggleLauncher.Key}; DesktopEnabled={initialReload.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled}");

            launcher.Show();
            Pump(TimeSpan.FromMilliseconds(120));
            controller.SetLauncherAvailability(true, true);
            var pageBefore = PageText(launcher);
            PostHotkeys(messageSource.Handle, [0x1000]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("WM_HOTKEY thật kích hoạt thao tác cửa sổ Toggle bản phát hành", !launcher.IsVisible && actions.LastOrDefault() == InputAction.ToggleLauncher,
                "Launchpad chuyển sang ẩn", $"Visible={launcher.IsVisible}; action={actions.LastOrDefault()}");

            PostHotkeys(messageSource.Handle, [0x1001]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("Thao tác Hide không bị tiêu thụ khi Launchpad đang ẩn", !launcher.IsVisible && actions.Count == 1,
                "Không có thao tác mới được thực thi", $"Visible={launcher.IsVisible}; actions={actions.Count}");
            PostHotkeys(messageSource.Handle, [0x1002]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("Thao tác NextPage không bị tiêu thụ khi Launchpad đang ẩn", !launcher.IsVisible && actions.Count == 1 && PageText(launcher) == pageBefore,
                "Danh sách thao tác và số trang không đổi", $"actions={actions.Count}; page={PageText(launcher)}");

            launcher.PrepareToShow();
            launcher.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            controller.SetLauncherAvailability(true, true);
            var beforeNext = PageText(launcher);
            PostHotkeys(messageSource.Handle, [0x1002]);
            Pump(TimeSpan.FromMilliseconds(80));
            var afterNext = PageText(launcher);
            Check("WM_HOTKEY thật thực thi chuyển trang NextPage bản phát hành", actions.LastOrDefault() == InputAction.NextPage
                && afterNext != beforeNext && afterNext.EndsWith("/ 2", StringComparison.Ordinal),
                "Logic nút điều hướng bản phát hành chuyển tới trang 2", $"{beforeNext}→{afterNext}");
            PostHotkeys(messageSource.Handle, [0x1003]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("WM_HOTKEY thật thực thi chuyển trang PreviousPage bản phát hành", actions.LastOrDefault() == InputAction.PreviousPage
                && PageText(launcher).StartsWith("1 / ", StringComparison.Ordinal),
                "Logic nút điều hướng bản phát hành quay về trang 1", PageText(launcher));

            if (desktopWasReadable)
            {
                PostHotkeys(messageSource.Handle, [0x1004]);
                Pump(TimeSpan.FromMilliseconds(80));
                Check("WM_HOTKEY thật đi tới định tuyến thao tác bản phát hành của biểu tượng màn hình nền", actions.LastOrDefault() == InputAction.ToggleDesktopIcons
                    && dispatchedIds.Contains(0x1004),
                    "WM_HOTKEY đi vào ánh xạ thao tác ToggleDesktopIcons", $"action={actions.LastOrDefault()}; chưa thực thi chuyển đổi Shell");
            }

            VerifyHotkeyConflict(app, launcher, paths);

            settings = new SettingsWindow();
            settings.BindPrimaryDisplay(primarySession);
            settings.AttachDataStore(data, null);
            settings.InteractionSettingsApply = (shortcutsToApply, display) =>
            {
                return controller.Apply(shortcutsToApply, display);
            };
            settings.PositionOnPrimary();
            settings.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            VerifySettingsNavigation(settings, data, paths);
            VerifyGlassTransparency(settings, data, paths, Path.Combine(Path.GetDirectoryName(settingsScreenshot)!, "glass-transparency.png"));
            var previousPointerSetting = data.Settings.Current.Display.LauncherFollowsPointer;
            RecordShortcut(settings, "ToggleLauncherHotkeyBox", Key.F13, ModifierKeys.Control);
            Check("Ghi phím tắt thao tác nhanh tự động lưu và giữ nguyên cài đặt màn hình cũ", data.Settings.Current.Display.LauncherFollowsPointer == previousPointerSetting
                && Reload(paths).Settings.Current.Display.LauncherFollowsPointer == previousPointerSetting
                && !Reload(paths).Settings.Current.Display.SuppressTriggersWhenFullscreen,
                "Phím tắt tự động lưu, các trường tương thích theo con trỏ và kích hoạt toàn màn hình giữ nguyên giá trị cũ",
                $"pointer={Reload(paths).Settings.Current.Display.LauncherFollowsPointer}; suppress={Reload(paths).Settings.Current.Display.SuppressTriggersWhenFullscreen}");

            var rows = data.Settings.Current.Launcher.Rows;
            data.Settings.Current.Shortcuts.HotCornerDwellMs = 777;
            data.Settings.Save();
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 3;
            ((Button)settings.FindName("ResetPageButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Khôi phục mặc định một trang trong Settings bản phát hành giữ nguyên các trang khác", data.Settings.Current.Launcher.Rows == rows
                && data.Settings.Current.Shortcuts.HotCornerDwellMs == 300,
                $"Số hàng Launchpad giữ nguyên {rows}, thời gian dừng góc nóng đặt lại về 300", $"rows={data.Settings.Current.Launcher.Rows}; dwell={data.Settings.Current.Shortcuts.HotCornerDwellMs}");

            Pump(TimeSpan.FromMilliseconds(80));
            Capture(settings, settingsScreenshot);
            Screenshots.Add(Path.GetFullPath(settingsScreenshot));

            var bytesBeforeInvalid = File.ReadAllBytes(paths.SettingsFile);
            ((ShortcutRecorder)settings.FindName("ToggleLauncherHotkeyBox")!).Text = "invalid";
            RecordShortcut(settings, "NextPageHotkeyBox", Key.F15, ModifierKeys.Control);
            Check("Định tuyến bản phát hành của Settings từ chối giá trị không hợp lệ và không ghi đĩa", bytesBeforeInvalid.SequenceEqual(File.ReadAllBytes(paths.SettingsFile))
                && !string.IsNullOrEmpty(((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text),
                "Byte của settings.json không đổi và hiển thị lỗi phạm vi", ((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text);

            VerifySaveFailure(settings, data, paths, controller);

            var bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(launcher).Handle);
            var workArea = primarySession.Display.WorkArea;
            var contained = bounds is { } b && workArea.Contains(b.X, b.Y)
                && b.Right <= workArea.Right && b.Bottom <= workArea.Bottom;
            Check("Hình chữ nhật cửa sổ vật lý của Launchpad thật nằm trong vùng làm việc màn hình chính lúc khởi động", contained,
                "Toàn bộ ranh giới cửa sổ nằm gọn trong vùng làm việc Primary Monitor", bounds?.ToString() ?? "Không thể đọc ranh giới vật lý");

            launcher.Hide();
            VerifyDockLauncherClickToggles(services, launcher, primarySession, Path.GetDirectoryName(settingsScreenshot)!);
        }
        finally
        {
            controller?.Dispose();
            settings?.Close();
            launcher.Close();
            messageSource?.Dispose();
        }
    }

    private static void VerifyDockLauncherClickToggles(AppServiceHost services, LauncherWindow launcher,
        PrimaryDisplaySession primarySession, string evidenceDirectory)
    {
        var settings = DockSettingsHost.Initialize(Application.Current.Dispatcher);
        var dock = new DockWindow();
        dock.BindPrimaryDisplay(primarySession);
        PixelRect? lastSourceBounds = null;
        var openRequests = 0;
        void OpenLauncher(PixelRect? sourceBounds)
        {
            openRequests++;
            lastSourceBounds = sourceBounds;
            launcher.RefreshDiagnostics();
            dock.SetLauncherContainerState(true);
            launcher.ShowWithTransition(sourceBounds ?? dock.GetLauncherButtonPixelBounds());
            launcher.Activate();
        }

        try
        {
            launcher.ContainerAnchorProvider = dock.GetLauncherButtonPixelBounds;
            launcher.IsVisibleChanged += OnVisibility;
            dock.AttachServices(services, settings,
                sourceBounds => AppHost.ToggleLauncherFromDock(launcher, sourceBounds, OpenLauncher));
            dock.Show();
            Pump(TimeSpan.FromMilliseconds(120));
            var button = (Button)dock.FindName("LauncherButton")!;
            var liveBounds = dock.GetLauncherButtonPixelBounds();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("Nút Launchpad trên Dock mở theo vị trí thời gian thực", launcher.IsVisible && launcher.CanNavigatePages
                && lastSourceBounds == liveBounds,
                "Launchpad mở ra và điểm xuất phát hoạt ảnh bằng hình chữ nhật hiện tại của nút Dock", $"Visible={launcher.IsVisible}; source={lastSourceBounds}; dock={liveBounds}");

            var iconImage = new RenderTargetBitmap(48, 64, 96, 96, PixelFormats.Pbgra32);
            iconImage.Render(button);
            var iconPixels = new byte[48 * 64 * 4];
            iconImage.CopyPixels(iconPixels, 48 * 4, 0);
            var visibleIconPixels = Enumerable.Range(0, 48 * 64).Count(index => iconPixels[index * 4 + 3] >= 128);
            Check("Biểu tượng nút Dock vẫn hiển thị và có thể nhấp sau khi Launchpad mở rộng", visibleIconPixels > 100 && button.IsEnabled && button.IsHitTestVisible,
                "Vẫn vẽ biểu tượng Launchpad sau khi mở rộng và nhận thao tác nhấp", $"Pixel biểu tượng hiển thị={visibleIconPixels}; Enabled={button.IsEnabled}; HitTest={button.IsHitTestVisible}");
            var dockScreenshot = Path.Combine(evidenceDirectory, "dock-launcher-button-expanded.png");
            Capture(dock, dockScreenshot);
            Screenshots.Add(Path.GetFullPath(dockScreenshot));

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("Nhấp lại biểu tượng Dock khi Launchpad đã mở sẽ thu gọn ngược lại", !launcher.IsVisible,
                "Launchpad thu về nút Dock theo chuyển tiếp gốc và ẩn đi", $"Visible={launcher.IsVisible}");

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            var requestsBeforeClose = openRequests;
            // Deactivation may start closing before the Dock's mouse-up Click.
            // That second click must complete the close, rather than reopen it.
            launcher.CloseWithTransition();
            var wasClosing = launcher.IsClosingContainer;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("Nhấp Dock khi đã bắt đầu thu gọn do mất tiêu điểm sẽ không mở lại ngược chiều", wasClosing && !launcher.IsVisible && openRequests == requestsBeforeClose,
                "Tiếp tục thu gọn cho tới khi ẩn, không yêu cầu mở lại", $"Thu gọn trước đó={wasClosing}; Visible={launcher.IsVisible}; Yêu cầu mở={requestsBeforeClose}->{openRequests}");
        }
        finally
        {
            launcher.Hide();
            launcher.IsVisibleChanged -= OnVisibility;
            launcher.ContainerAnchorProvider = null;
            dock.Close();
        }

        void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) => dock.SetLauncherContainerState(e.NewValue is true);
    }

    private static void VerifyHotkeyConflict(Application app, LauncherWindow launcher, AppPaths mainPaths)
    {
        var paths = new AppPaths(Path.Combine(mainPaths.RootDirectory, "native-transaction"));
        var data = new GlassquidDataStore(paths);
        data.LoadAll();
        using var source = new HwndSource(new HwndSourceParameters("Glassquid.Phase6.Conflict")
        { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        InteractionController? controller = null;
        using var competitor = new HwndSource(new HwndSourceParameters("Glassquid.Phase6.Competitor")
        { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        controller = new InteractionController(data, CapturePrimaryDisplaySession(), source.Handle, app.Dispatcher,
            action => action == InputAction.ToggleLauncher || launcher.IsVisible,
            action =>
            {
                if (action != InputAction.ToggleLauncher) return;
                if (launcher.IsVisible) launcher.Hide(); else launcher.Show();
            }, _ => { });
        var received = 0;
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == WmHotkey && controller is not null)
            {
                received++;
                controller.HandleHotkey(wParam.ToInt32());
                handled = true;
            }
            return 0;
        });
        _ = controller.Initialize();
        var baseline = new ShortcutSettings { ToggleLauncher = Hotkey(18) };
        var initialError = controller.Apply(baseline, data.Settings.Current.Display);
        var baselineBytes = File.ReadAllBytes(paths.SettingsFile);
        using var registry = new NativeHotkeyRegistry(competitor.Handle);
        var contested = Hotkey(19);
        _ = HotkeyText.TryGetChord(contested, out var chord, out _);
        var acquired = registry.TryRegister(88, chord, out var osError);
        var candidate = new ShortcutSettings { ToggleLauncher = contested };
        var rejected = acquired ? controller.Apply(candidate, data.Settings.Current.Display) : "Máy này không thể tạo đăng ký cạnh tranh, lỗi Win32=" + osError;
        Check("Xung đột RegisterHotKey thật vẫn giữ nguyên đăng ký, cài đặt và thao tác bản phát hành cũ", initialError is null && acquired && rejected is not null
            && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F18"
            && baselineBytes.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
            "HWND thứ hai đăng ký xung đột; cài đặt trong bộ nhớ, trên đĩa và phím tắt gốc vẫn giữ Ctrl+F18", $"Áp dụng lần đầu={initialError ?? "Thành công"}; Kết quả xung đột={rejected ?? "Không có lỗi"}");
        launcher.Hide();
        if (acquired) registry.Unregister(88);
        PostHotkeys(source.Handle, [0x1000]);
        Pump(TimeSpan.FromMilliseconds(80));
        Check("Sau xung đột, đăng ký cũ vẫn điều khiển thao tác cửa sổ bản phát hành", received == 1 && launcher.IsVisible,
            "Thông điệp Ctrl+F18 cũ đến nơi và hiển thị Launchpad", $"Số thông điệp={received}; Cửa sổ hiển thị={launcher.IsVisible}");
        var afterRelease = controller.Apply(candidate, data.Settings.Current.Display);
        Check("Sau khi giải phóng xung đột đăng ký thật, tổ hợp tương tự có thể đăng ký", afterRelease is null
            && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F19",
            "Ctrl+F19 đã được áp dụng và lưu bền vững", afterRelease ?? "Dịch vụ bản phát hành đăng ký thành công sau khi HWND thứ hai giải phóng");
        controller.Dispose();
        controller = null;
        using var afterClose = new NativeHotkeyRegistry(source.Handle);
        _ = HotkeyText.TryGetChord(contested, out chord, out _);
        var canRegisterAfterClose = afterClose.TryRegister(90, chord, out var closeError);
        Check("Sau khi đóng bộ điều phối, giải phóng đăng ký phím tắt tương ứng", canRegisterAfterClose,
            "Cùng một HWND có thể đăng ký lại Ctrl+F19", canRegisterAfterClose ? "Đăng ký thành công" : $"Lỗi Win32={closeError}");
        if (canRegisterAfterClose) afterClose.Unregister(90);
        controller?.Dispose();
    }

    private static void VerifySaveFailure(SettingsWindow settings, GlassquidDataStore data, AppPaths paths, InteractionController controller)
    {
        var oldKey = data.Settings.Current.Shortcuts.ToggleLauncher.Key;
        var before = File.ReadAllBytes(paths.SettingsFile);
        settings.InteractionSettingsApply = (shortcuts, display) =>
        {
            using var locked = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return controller.Apply(shortcuts, display);
        };
        RecordShortcut(settings, "ToggleLauncherHotkeyBox", Key.F23, ModifierKeys.Control | ModifierKeys.Alt);
        var status = ((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text;
        Check("Ứng dụng bản phát hành của Settings hoàn tác khi gặp khóa độc quyền tệp thật", data.Settings.Current.Shortcuts.ToggleLauncher.Key == oldKey
            && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile))
            && status.Contains("Lưu thất bại, đã khôi phục cài đặt và phím tắt gốc:", StringComparison.Ordinal),
            "Cài đặt dùng chung và byte trên đĩa quay về trạng thái trước khi lưu", $"Theo con trỏ={data.Settings.Current.Display.LauncherFollowsPointer}; status={((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text}");
    }

    private static void VerifyPhase6ReportPathProtection(string root)
    {
        var protectedRoot = Path.Combine(root, "protected-user-data");
        var protectedPaths = new AppPaths(protectedRoot);
        protectedPaths.EnsureCreated();
        const string sentinel = "preserve-settings-sentinel";
        File.WriteAllText(protectedPaths.SettingsFile, sentinel);
        var error = new StringWriter();
        var previous = Console.Error;
        int exitCode;
        try
        {
            Console.SetError(error);
            exitCode = Phase6Acceptance.Run(protectedPaths,
                [protectedPaths.SettingsFile, Path.Combine(root, "safe-acceptance-data")]);
        }
        finally
        {
            Console.SetError(previous);
        }

        Check("Giai đoạn 6 CLI từ chối ghi báo cáo vào thư mục dữ liệu người dùng", exitCode == 2
            && File.ReadAllText(protectedPaths.SettingsFile) == sentinel
            && error.ToString().Contains("Đường dẫn báo cáo", StringComparison.Ordinal),
            "Trả về 2, byte tệp cài đặt không đổi và báo cáo lý do từ chối",
            $"exit={exitCode}; sentinelPreserved={File.ReadAllText(protectedPaths.SettingsFile) == sentinel}; stderr={error}");
    }

    private static GlassquidDataStore Reload(AppPaths paths)
    {
        var result = new GlassquidDataStore(paths);
        result.LoadAll();
        return result;
    }

    private static HotkeyDefinition Hotkey(int functionKey) => new()
    { Enabled = true, Control = true, Key = "F" + functionKey.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private static void PostHotkeys(nint hwnd, IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (!PostMessage(hwnd, WmHotkey, new nint(id), 0))
                throw new InvalidOperationException($"PostMessage(WM_HOTKEY,{id}) failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    private static void VerifyGlassTransparency(SettingsWindow settings, GlassquidDataStore data, AppPaths paths, string screenshot)
    {
        var sampling = Glassquid.UI.Controls.LiquidGlassDebugState.SamplingParameters;
        var weights = Glassquid.UI.Controls.LiquidGlassDebugState.WeightParameters;
        var brdf = Glassquid.UI.Controls.LiquidGlassDebugState.BrdfParameters;
        var dispersion = Glassquid.UI.Controls.LiquidGlassDebugState.DispersionParameters;
        foreach (var (controlName, launcher) in new[] { ("DockTransparencySlider", false), ("LauncherTransparencySlider", true) })
        {
            double Read(Glassquid.Core.Models.Settings.AppearanceSettings appearance) => launcher
                ? appearance.LauncherGlassTransparency : appearance.DockGlassTransparency;
            double ReadOther(Glassquid.Core.Models.Settings.AppearanceSettings appearance) => launcher
                ? appearance.DockGlassTransparency : appearance.LauncherGlassTransparency;
            var otherBefore = ReadOther(data.Settings.Current.Appearance);
            var slider = (Slider)settings.FindName(controlName)!;
            foreach (var transparency in new[] { 1d, 0d, .5 })
            {
                slider.Value = transparency;
                Pump(TimeSpan.FromMilliseconds(100));
                var expected = transparency;
                Check($"Độ trong suốt kính {transparency:P0} được áp dụng và lưu theo thời gian thực",
                    Math.Abs(Read(data.Settings.Current.Appearance) - expected) < 1e-9
                    && Math.Abs(Read(ThemeService.Current!.Appearance) - expected) < 1e-9
                    && Math.Abs(Read(Reload(paths).Settings.Current.Appearance) - expected) < 1e-9
                    && ReadOther(data.Settings.Current.Appearance) == otherBefore
                    && settings.Opacity == 1,
                    "Độ trong suốt hai cửa sổ được lưu độc lập, Opacity cửa sổ giữ ở 1", $"control={controlName}; transparency={Read(data.Settings.Current.Appearance)}; opacity={settings.Opacity}");
            }
            Check("Thanh trượt độ trong suốt giữ nguyên Face Inner Outer và các tham số quang học",
                sampling == Glassquid.UI.Controls.LiquidGlassDebugState.SamplingParameters
                && weights == Glassquid.UI.Controls.LiquidGlassDebugState.WeightParameters
                && brdf == Glassquid.UI.Controls.LiquidGlassDebugState.BrdfParameters
                && dispersion == Glassquid.UI.Controls.LiquidGlassDebugState.DispersionParameters,
                "Lấy mẫu, trọng số, phản xạ bóng và tán sắc giữ nguyên giá trị gốc", "Đã so sánh các tham số trước và sau khi chỉnh thanh trượt");
            var before = File.ReadAllBytes(paths.SettingsFile);
            var previousClarity = Read(data.Settings.Current.Appearance);
            using (var locked = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                slider.Value = .75;
            Check("Khôi phục thanh trượt và bản xem trước thời gian thực khi lưu độ trong suốt thất bại",
                Read(data.Settings.Current.Appearance) == previousClarity
                && Read(ThemeService.Current!.Appearance) == previousClarity
                && Math.Abs(slider.Value - previousClarity) < 1e-9
                && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                "Cài đặt, bản xem trước, thanh trượt và dữ liệu trên đĩa đều khôi phục giá trị gốc", $"stored={Read(data.Settings.Current.Appearance)}; live={Read(ThemeService.Current!.Appearance)}");
        }
        ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 0;
        Pump(TimeSpan.FromMilliseconds(200));
        Capture(settings, screenshot);
        Screenshots.Add(Path.GetFullPath(screenshot));
    }

    private static void RecordShortcut(SettingsWindow settings, string name, Key key, ModifierKeys modifiers)
    {
        var recorder = (ShortcutRecorder)settings.FindName(name)!;
        recorder.BeginRecording();
        recorder.CaptureKey(key, modifiers);
    }

    private static void VerifySettingsNavigation(SettingsWindow settings, GlassquidDataStore data, AppPaths paths)
    {
        var list = (ListBox)settings.FindName("SettingsPageList")!;
        var scroll = (ScrollViewer)settings.FindName("SettingsScroll")!;
        var reset = (Button)settings.FindName("ResetPageButton")!;
        var sections = new (string page, string heading)[]
        {
            ("Giao diện", "AppearanceSectionHeading"), ("Dock", "DockSectionHeading"),
            ("Launchpad", "LauncherSectionHeading"), ("Thao tác nhanh", "ShortcutsSectionHeading"),
            ("Màn hình nền", "DesktopSectionHeading"), ("Sao lưu và khôi phục", "BackupSectionHeading"),
            ("Chuyển khoản", "SponsorshipSectionHeading"),
            ("Giới thiệu", "AboutSectionHeading"),
        };
        foreach (var (page, headingName) in sections.Concat(sections.Reverse()))
        {
            var heading = (FrameworkElement)settings.FindName(headingName)!;
            var target = scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y - 12;
            scroll.ScrollToVerticalOffset(Math.Max(0, target));
            Pump(TimeSpan.FromMilliseconds(70));
            var offset = scroll.VerticalOffset;
            Check($"Cuộn Settings đồng bộ danh mục: {page}, offset={offset:0}", Equals(list.SelectedItem, page),
                page, list.SelectedItem?.ToString() ?? "Chưa chọn");
            Pump(TimeSpan.FromMilliseconds(40));
            Check($"Đồng bộ tô sáng trong Settings không nhảy ngược: {page}, offset={offset:0}", Math.Abs(scroll.VerticalOffset - offset) < .5,
                "Vị trí cuộn được giữ nguyên", $"{offset:0.0}→{scroll.VerticalOffset:0.0}");
        }
        scroll.ScrollToBottom();
        Pump(TimeSpan.FromMilliseconds(70));
        Check("Cuộn Settings xuống cuối sẽ chọn Giới thiệu và vô hiệu hóa khôi phục mặc định", Equals(list.SelectedItem, "Giới thiệu") && !reset.IsEnabled,
            "Giới thiệu, vô hiệu hóa khôi phục mặc định", $"{list.SelectedItem}; reset={reset.IsEnabled}");

        foreach (var page in new[] { "Dock", "Launchpad", "Thao tác nhanh", "Màn hình nền", "Sao lưu và khôi phục", "Chuyển khoản", "Giới thiệu", "Giao diện" })
        {
            list.SelectedItem = page;
            Pump(TimeSpan.FromMilliseconds(90));
            var heading = (FrameworkElement)settings.FindName(sections.Single(s => s.page == page).heading)!;
            var y = heading.TranslatePoint(new Point(), scroll).Y;
            Check($"Nhấp danh mục trong Settings định vị đúng và tô sáng ổn định: {page}", Equals(list.SelectedItem, page)
                && (Math.Abs(y - 12) < 1 || (page == "Giao diện" && scroll.VerticalOffset < .5)
                    || Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < .5),
                "Tiêu đề danh mục đã chọn định vị lên đầu hoặc ranh giới cuộn", $"{list.SelectedItem}; headingY={y:0.0}; offset={scroll.VerticalOffset:0.0}");
        }
        var dockHeading = (FrameworkElement)settings.FindName("DockSectionHeading")!;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + dockHeading.TranslatePoint(new Point(), scroll).Y - 24);
        Pump(TimeSpan.FromMilliseconds(70));
        var beforeWheel = scroll.VerticalOffset;
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent });
        Pump(TimeSpan.FromMilliseconds(70));
        Check("Con lăn chuột trong Settings đồng bộ danh mục khi vượt qua tiêu đề và không nảy lại", Equals(list.SelectedItem, "Dock")
            && scroll.VerticalOffset > beforeWheel,
            "Cuộn chuột xuống qua tiêu đề Dock và chọn Dock", $"{list.SelectedItem}; {beforeWheel:0.0}→{scroll.VerticalOffset:0.0}");
        var oldHeight = settings.Height;
        settings.Height = 480;
        Pump(TimeSpan.FromMilliseconds(70));
        foreach (var (page, headingName) in sections)
        {
            var heading = (FrameworkElement)settings.FindName(headingName)!;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y - 12);
            Pump(TimeSpan.FromMilliseconds(70));
            Check($"Sau khi thay đổi kích thước Settings, danh mục cuộn vẫn khớp: {page}", Equals(list.SelectedItem, page), page,
                list.SelectedItem?.ToString() ?? "Chưa chọn");
        }
        settings.Height = oldHeight;
        Pump(TimeSpan.FromMilliseconds(70));
        list.SelectedItem = "Dock";
        list.SelectedItem = "Giới thiệu";
        list.SelectedItem = "Màn hình nền";
        Pump(TimeSpan.FromMilliseconds(100));
        Check("Chuyển đổi danh mục liên tiếp chỉ định vị tới mục tiêu cuối cùng", Equals(list.SelectedItem, "Màn hình nền")
            && Math.Abs(((FrameworkElement)settings.FindName("DesktopSectionHeading")!).TranslatePoint(new Point(), scroll).Y - 12) < 1,
            "Màn hình nền giữ trạng thái được chọn và định vị chính xác", list.SelectedItem?.ToString() ?? "Chưa chọn");
        var apply = settings.InteractionSettingsApply;
        var applyCalls = 0;
        settings.InteractionSettingsApply = (shortcuts, display) => { applyCalls++; return apply?.Invoke(shortcuts, display); };
        try
        {
            list.SelectedItem = "Màn hình nền";
            Pump(TimeSpan.FromMilliseconds(90));
            var before = data.Settings.Current.Shortcuts.ToggleLauncher.Key;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Khôi phục mặc định Màn hình nền không đi vào định tuyến áp dụng thao tác nhanh hoặc màn hình", reset.IsEnabled && applyCalls == 0
                && Reload(paths).Settings.Current.Shortcuts.ToggleLauncher.Key == before,
                "Màn hình nền có thể khôi phục và không gọi dịch vụ thao tác nhanh", $"enabled={reset.IsEnabled}; applyCalls={applyCalls}");
            list.SelectedItem = "Sao lưu và khôi phục";
            Pump(TimeSpan.FromMilliseconds(90));
            var bytes = File.ReadAllBytes(paths.SettingsFile);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Danh mục Sao lưu và khôi phục cấm khôi phục mặc định trang và ghi đĩa", !reset.IsEnabled && applyCalls == 0
                && bytes.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                "Nút bị vô hiệu hóa và dữ liệu không đổi", $"enabled={reset.IsEnabled}; applyCalls={applyCalls}");
        }
        finally { settings.InteractionSettingsApply = apply; }
    }

    private static string PageText(LauncherWindow window) => ((TextBlock)window.FindName("PageText")!).Text;

    private static bool TryParse(string[] args, out string report, out string? dataDirectory, out string error)
    {
        report = Path.Combine(Environment.CurrentDirectory, "docs", "evidence", "Giai đoạn 6", "phase6-wpf.json");
        dataDirectory = null;
        error = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--report" or "--data-dir")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                { error = $"Missing value for {args[i]}. Usage: --report <json> [--data-dir <temp>]"; return false; }
                if (args[i] == "--report") report = Path.GetFullPath(args[++i]);
                else dataDirectory = Path.GetFullPath(args[++i]);
                continue;
            }
            error = $"Unknown argument: {args[i]}. Usage: --report <json> [--data-dir <temp>]";
            return false;
        }
        return true;
    }

    private static void Check(string name, bool okay, string expected, string actual)
    {
        if (!okay) _failed++;
        Cases.Add(new { name, driver = "production WPF routes / native registration / isolated files", expected, actual,
            status = okay ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
    }

    private static PrimaryDisplaySession CapturePrimaryDisplaySession() =>
        new(DisplayService.Primary() ?? throw new InvalidOperationException("Windows Primary Monitor is unavailable."));

    private static void WriteReport(string report, string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            artifact = typeof(InteractionController).Assembly.FullName,
            environment = Environment.OSVersion.ToString(),
            machine = Environment.MachineName,
            dataDirectory = root,
            driver = "real WPF windows and controls; this process's RegisterHotKey/WM_HOTKEY; no system input injection",
            screenshots = Screenshots,
            supersedes = "phase6-wpf-initial.json",
            correctionsSinceInitialRun = new[]
            {
                "Lỗi lật trang khi ẩn trong báo cáo đầu tiên xuất phát từ việc Runner vẫn truyền canNavigate là true; nay Runner đồng bộ trạng thái ẩn thành false.",
                "Lỗi trang trước trong báo cáo đầu tiên xuất phát từ phán đoán sai của khẳng định: số trang thực tế 1 / 2 từng bị nhận nhầm là không phải trang 1; nay kiểm tra rõ ràng bắt đầu bằng 1 /.",
                "Mục lỗi ghi đĩa trong báo cáo đầu tiên thực tế dừng ở bước kiểm tra đầu vào không hợp lệ, chưa thử lưu; nay khôi phục giá trị thời gian dừng hợp lệ trước, sau đó khóa settings.json và yêu cầu báo cáo phải chứa chi tiết lưu thất bại.",
            },
            notRun = new[] { "Nhập liệu bàn phím thật / nút hông chuột / con trỏ góc nóng", "Chuyển đổi biểu tượng màn hình nền Explorer", "Cắm rút vật lý màn hình hiển thị" },
            passed = Cases.Count - _failed,
            failed = _failed,
            exitCode = _failed == 0 && Cases.Count > 0 ? 0 : 1,
            cases = Cases,
        }, JsonOptions));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path);
        encoder.Save(output);
    }
}