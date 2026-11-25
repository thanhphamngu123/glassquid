using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Launcher;
using Glassquid.Platform.Displays;

internal static partial class Program
{
    private static void RecordTransitionPerformance(LauncherWindow window, string evidence)
    {
        window.Hide();
        window.PrepareToShow();
        var preparedGrid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
        var preparedTiles = preparedGrid.Children.OfType<Button>().ToArray();
        window.Show();
        Check("Mở cửa sổ không tạo lại các control biểu tượng đã chuẩn bị", "performance/visual-parity",
            preparedTiles.Length > 0 && preparedTiles.SequenceEqual(preparedGrid.Children.OfType<Button>()),
            "Tái sử dụng cùng lô control biểu tượng", "prepared=" + preparedTiles.Length);
        VerifyRepeatedPreparation(window);
        var display = DisplayService.Primary();
        if (display is null) return;
        var anchor = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
        var samples = new List<object>();
        window.Hide();
        window.ContainerAnchorProvider = () => anchor;
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Measure("open", cycle, () => window.ShowWithTransition(anchor), () => window.CanNavigatePages);
                Pump(TimeSpan.FromMilliseconds(180));
                Measure("close", cycle, window.CloseWithTransition, () => !window.IsVisible);
            }
        }
        finally
        {
            window.ContainerAnchorProvider = null;
            window.ShowWithTransition(null);
            Pump(TimeSpan.FromMilliseconds(100));
        }
        File.WriteAllText(Path.Combine(evidence, "transition-performance.json"), JsonSerializer.Serialize(new
        {
            note = "WPF Rendering cadence, unique composition timestamps; not GPU-presented FPS. Cycle 0 includes cold setup. No system input injection.",
            hardwareShader3 = RenderCapability.IsPixelShaderVersionSupported(3, 0),
            samples,
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string direction, int cycle, Action start, Func<bool> complete)
        {
            var gaps = new List<double>();
            var lastComposition = TimeSpan.MinValue;
            long previous = 0;
            var frames = 0;
            EventHandler sample = (_, args) =>
            {
                if (args is not RenderingEventArgs rendering || rendering.RenderingTime == lastComposition) return;
                lastComposition = rendering.RenderingTime;
                if (!window.IsVisible || window.CanNavigatePages) return;
                var now = Stopwatch.GetTimestamp();
                if (previous != 0) gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
                frames++;
            };
            CompositionTarget.Rendering += sample;
            var watch = Stopwatch.StartNew();
            try
            {
                start();
                var dispatchMs = watch.Elapsed.TotalMilliseconds;
                var completed = PumpUntil(complete, TimeSpan.FromSeconds(2));
                gaps.Sort();
                samples.Add(new
                {
                    direction, cycle, completed, dispatchMs, elapsedMs = watch.Elapsed.TotalMilliseconds, frames,
                    medianGapMs = Percentile(.5), p95GapMs = Percentile(.95),
                    maxGapMs = gaps.Count == 0 ? 0 : gaps[^1],
                });
            }
            finally { CompositionTarget.Rendering -= sample; }
            double Percentile(double p) => gaps.Count == 0 ? 0 : gaps[(int)Math.Ceiling(p * gaps.Count) - 1];
        }
    }

    private static void VerifyRepeatedPreparation(LauncherWindow window)
    {
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = (AppServiceHost)typeof(LauncherWindow).GetField("_services", fields)!.GetValue(window)!;
        var view = (LauncherViewState)typeof(LauncherWindow).GetField("_view", fields)!.GetValue(window)!;
        var settings = (Glassquid.Core.Models.Settings.LauncherSettings)typeof(LauncherWindow)
            .GetField("_settings", fields)!.GetValue(window)!;
        var originalGroup = view.OpenGroupId;
        var originalPage = view.Page;
        var originalLayout = LauncherLayoutRules.DeepClone(host.Layouts.Launcher);
        var originalRemember = settings.RememberLastPage;
        var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
        var groupItems = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("GroupItems")!;
        var panel = (FrameworkElement)window.FindName("LauncherPanelRoot")!;
        var originalHeight = panel.Height;
        var entry = host.Library.Find("wpf-00")!;
        var originalName = entry.DisplayName;
        window.Hide();
        try
        {
            settings.RememberLastPage = true;
            window.PrepareToShow();
            var first = grid.Children.OfType<Button>().ToArray();
            window.PrepareToShow();
            Check("Chuẩn bị lại cùng chế độ xem Launcher tái sử dụng control biểu tượng thật", "performance/warm-prepare",
                first.Length > 0 && first.SequenceEqual(grid.Children.OfType<Button>()),
                "Cùng tham chiếu Button khi thư viện/bố cục/trang/kích thước không đổi", $"Số biểu tượng {first.Length}");

            window.RefreshLibrary();
            Check("Làm mới tường minh vẫn tạo lại control biểu tượng", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()),
                "Gọi công khai RefreshLibrary để ép làm mới", "Đã đối chiếu tham chiếu đối tượng Button");

            first = grid.Children.OfType<Button>().ToArray();
            entry.DisplayName = "Warm cache changed title";
            var updated = host.Library.Update(entry);
            window.PrepareToShow();
            var changed = grid.Children.OfType<Button>().Single(button => Equals(button.Tag, entry.Id));
            var displayed = ((StackPanel)changed.Content).Children.OfType<TextBlock>().Single().Text;
            Check("Thay đổi phiên bản thư viện ứng dụng sẽ tạo lại control và hiển thị tên mới", "performance/warm-prepare",
                updated && !first.SequenceEqual(grid.Children.OfType<Button>()) && displayed.Contains(entry.DisplayName),
                "Button mới và tên ứng dụng đã cập nhật", displayed);
            entry.DisplayName = originalName;
            host.Library.Update(entry);
            window.PrepareToShow();

            first = grid.Children.OfType<Button>().ToArray();
            var reordered = LauncherLayoutEditor.Move(host.Layouts.Launcher, "wpf-00", 1, 1, view.Capacity);
            var committed = host.Layouts.CommitLauncher(reordered, host.Layouts.LauncherVersion);
            window.PrepareToShow();
            Check("Thay đổi phiên bản bố cục sẽ tạo lại control và hiển thị thứ tự mới", "performance/warm-prepare",
                committed && !first.SequenceEqual(grid.Children.OfType<Button>()) && Equals(grid.Children[0].GetValue(FrameworkElement.TagProperty), "wpf-01"),
                "Mục đầu tiên mới wpf-01", ((Button)grid.Children[0]).Tag?.ToString() ?? "Không có mục đầu");
            host.Layouts.CommitLauncher(originalLayout, host.Layouts.LauncherVersion);
            window.PrepareToShow();

            first = grid.Children.OfType<Button>().ToArray();
            view.MoveToPage(1);
            window.PrepareToShow();
            Check("Thay đổi trang khi chuẩn bị sẽ kết xuất lại trang cuối", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()) && grid.Children.Count == 1 && Equals(((Button)grid.Children[0]).Tag, "wpf-35"),
                "Trang cuối 1 mục wpf-35", $"Trang {view.Page}; Số biểu tượng {grid.Children.Count}");
            settings.RememberLastPage = false;
            window.PrepareToShow();
            Check("Không ghi nhớ trang cuối thì khi chuẩn bị sẽ kết xuất lại trang đầu", "performance/warm-prepare",
                view.Page == 0 && grid.Children.Count == 35 && Equals(((Button)grid.Children[0]).Tag, "wpf-00"),
                "Trang đầu 35 mục và mục đầu wpf-00", $"Trang {view.Page}; Số biểu tượng {grid.Children.Count}");
            settings.RememberLastPage = true;

            first = grid.Children.OfType<Button>().ToArray();
            panel.Height = originalHeight + 16;
            window.PrepareToShow();
            Check("Thay đổi kích thước bảng điều khiển khi chuẩn bị sẽ tạo lại control biểu tượng", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()),
                "Thay đổi kích thước làm vô hiệu control cũ", "Đã đối chiếu tham chiếu đối tượng Button");
            panel.Height = originalHeight;
            window.PrepareToShow();

            var grouped = LauncherLayoutEditor.CreateGroup(host.Layouts.Launcher, "wpf-00", "wpf-01", "warm-cache-group", view.Capacity);
            host.Layouts.CommitLauncher(grouped, host.Layouts.LauncherVersion);
            window.PrepareToShow();
            grid.Children.OfType<Button>().Single(button => Equals(button.Tag, "warm-cache-group"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.PrepareToShow();
            var groupButtons = groupItems.Children.OfType<Button>().ToArray();
            first = grid.Children.OfType<Button>().ToArray();
            window.PrepareToShow();
            Check("Chuẩn bị lại giữ nguyên tham chiếu thành viên nhóm và control cấp cao nhất", "performance/warm-prepare",
                groupButtons.Length == 2 && groupButtons.SequenceEqual(groupItems.Children.OfType<Button>()) &&
                first.SequenceEqual(grid.Children.OfType<Button>()),
                "Cả 2 thành viên cùng nhóm và Button cấp cao nhất đều được tái sử dụng", $"Thành viên nhóm {groupButtons.Length}");
        }
        finally
        {
            entry.DisplayName = originalName;
            host.Library.Update(entry);
            host.Layouts.CommitLauncher(originalLayout, host.Layouts.LauncherVersion);
            settings.RememberLastPage = originalRemember;
            view.OpenGroupId = originalGroup;
            view.MoveToPage(originalPage);
            panel.Height = originalHeight;
            window.RefreshLibrary();
            window.Show();
        }
    }
}
