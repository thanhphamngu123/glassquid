using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glassquid.Core.Storage;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyRapidInteraction(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var initialTiles = grid.Children.Cast<Button>().ToArray();
        var pageTimings = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var watch = Stopwatch.StartNew();
            window.NavigatePage(i % 2 == 0 ? 1 : -1);
            pageTimings.Add(watch.Elapsed.TotalMilliseconds);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        Check("Lật trang qua lại tái sử dụng điều khiển cả trang và mẫu đã tải", "regression/page-reuse",
            initialTiles.SequenceEqual(grid.Children.Cast<Button>()), "Danh tính điều khiển trang đầu không đổi, vẫn có hoạt ảnh hai trang",
            "sameTiles=" + initialTiles.SequenceEqual(grid.Children.Cast<Button>()));
        VerifyPageSlide(window, grid, evidence);

        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", flags)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", flags)!;
        var queue = typeof(LauncherWindow).GetMethod("QueueIconPreview", flags);
        var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        Check("Xem trước chuột được gộp theo khung kết xuất và giữ vị trí cuối cùng", "regression/drag-coalesce", queue is not null,
            "Mỗi khung kết xuất xử lý tối đa một lần xem trước", queue is null ? "Mỗi sự kiện con trỏ tự bố cục trực tiếp" : "Hỗ trợ gộp");
        if (queue is not null)
        {
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            var originalOrder = grid.Children.Cast<Button>().Select(t => t.Tag).ToArray();
            var unaffectedTile = (Button)grid.Children[30];
            var unaffectedMotion = unaffectedTile.RenderTransform;
            var burstClock = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                queue.Invoke(window, [new Point(grid.ActualWidth / 7 * ((i % 6) + .5), grid.ActualHeight / 10), false]);
            var burstMilliseconds = burstClock.Elapsed.TotalMilliseconds;
            var destination = new Point(grid.ActualWidth / 7 * 3.5, grid.ActualHeight / 10);
            queue.Invoke(window, [destination, false]);
            Check("1000 sự kiện con trỏ không dựng lại lưới đồng bộ", "regression/drag-coalesce",
                originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)),
                "Bố cục chưa được áp dụng, chỉ vị trí cuối cùng đang chờ xử lý", "orderUnchanged=" + originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)));
            Check("Khung hình tiếp theo dùng vị trí cuối cùng để nhường chỗ", "regression/drag-coalesce",
                PumpUntil(() => Equals(VisualTileOrder(grid)[3].Tag, "wpf-00")
                    && Equals(VisualTileOrder(grid)[0].Tag, "wpf-01"), TimeSpan.FromSeconds(1)),
                "00 chuyển sang ô thứ 4", string.Join(",", VisualTileOrder(grid).Take(4).Select(t => t.Tag)));
            var commits = (long)typeof(LauncherWindow).GetField("_dragPreviewCommits", flags)!.GetValue(window)!;
            Check("Một loạt sự kiện con trỏ chỉ áp dụng xem trước một lần", "regression/drag-coalesce", commits == 1, "1 lần", commits.ToString());
            Check("Nhường chỗ không đặt lại phép biến đổi của các biểu tượng không di chuyển", "regression/drag-layout",
                ReferenceEquals(unaffectedMotion, unaffectedTile.RenderTransform),
                "Các biểu tượng ở xa giữ nguyên trạng thái hiển thị và chuyển động", "motionRetained=" + ReferenceEquals(unaffectedMotion, unaffectedTile.RenderTransform));
            queue.Invoke(window, [new Point(grid.ActualWidth / 7 * 5.5, grid.ActualHeight / 10), true]);
            finish.Invoke(window, null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check("Hủy kéo đồng thời hủy bản xem trước chưa áp dụng", "regression/drag-coalesce",
                originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)) && before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "Thứ tự gốc và tệp không đổi", "fileUnchanged=" + before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)));
            File.WriteAllText(Path.Combine(evidence, "rapid-drag.json"), JsonSerializer.Serialize(new
            { burstEvents=1000, burstMilliseconds, commits, previewMilliseconds=typeof(LauncherWindow).GetField("_lastDragPreviewMilliseconds", flags)!.GetValue(window) }));
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            window.NavigatePage(1);
            // Wall-clock eligibility can arrive before WPF advances the outgoing clock.
            typeof(LauncherWindow).GetField("_pagePreviewAfter", flags)!.SetValue(window, DateTimeOffset.MinValue);
            Exception? reparentFailure = null;
            try { typeof(LauncherWindow).GetMethod("PreviewIconPlacement", flags)!.Invoke(window, [new Point(20, 20), true]); }
            catch (TargetInvocationException error) { reparentFailure = error.InnerException; }
            Check("Nhường chỗ qua trang giải phóng quyền sở hữu hiển thị ở trang cũ trước", "regression/drag-ownership", reparentFailure is null,
                "Biểu tượng có thể chuyển sang trang mới an toàn", reparentFailure?.Message ?? "Không có xung đột quyền sở hữu điều khiển");
            finish.Invoke(window, null);
            window.NavigatePage(-1);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        File.WriteAllText(Path.Combine(evidence, "rapid-pages.json"), JsonSerializer.Serialize(new
        { note="Synchronous call timings are not displayed FPS.", pageTimings }, new JsonSerializerOptions { WriteIndented=true }));
    }
}
