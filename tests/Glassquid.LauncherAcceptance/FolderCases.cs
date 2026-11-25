using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Launcher;
using Glassquid.Platform.Displays;
using Glassquid.UI.Controls;

internal static partial class Program
{
    private static void VerifyFolderInteractions(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence, string? iconsDirectory)
    {
        data.Layouts.CommitLauncher(LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher,
            data.Library.Items.Select(e => e.Id), 35), data.Layouts.LauncherVersion);
        var icons = iconsDirectory is not null && Directory.Exists(iconsDirectory)
            ? Directory.GetFiles(iconsDirectory, "*.png").Take(36).ToArray() : [];
        for (var i = 0; i < icons.Length; i++) data.Library.Find($"wpf-{i:00}")!.CustomIconPath = icons[i];
        window.RefreshLibrary(); window.Activate(); Pump(TimeSpan.FromMilliseconds(1000));
        var stamps = new List<double>();
        TimeSpan? last = null;
        EventHandler measure = (_, e) =>
        {
            if (e is not RenderingEventArgs frame || frame.RenderingTime == last) return;
            if (last is { } previous) stamps.Add((frame.RenderingTime - previous).TotalMilliseconds);
            last = frame.RenderingTime;
        };
        CompositionTarget.Rendering += measure;
        Button Tile(string id) => grid.Children.OfType<Button>().First(t => Equals(t.Tag, id));
        Point Center(Button tile)
        {
            var host = (FrameworkElement)tile.GetType().GetProperty("IconHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tile)!;
            return host.PointToScreen(new Point(host.ActualWidth / 2, host.ActualHeight / 2));
        }
        void Move(Point p) => IosInput.SetCursorPos((int)p.X, (int)p.Y);
        void Drag(Button from, Point target, int hover, string? snapshot = null, Button? revisitTarget = null, Action? beforeDrop = null, bool verifyLanding = false)
        {
            var start = Center(from); Move(start); IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(440));
            File.AppendAllText(Path.Combine(evidence, "folder-input-trace.log"),
                $"{DateTimeOffset.Now:O} source={from.Tag} active={window.IsActive} mouse={System.Windows.Input.Mouse.LeftButton} captured={System.Windows.Input.Mouse.Captured is not null} logical={typeof(LauncherWindow).GetField("_logicalIconDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)}\n");
            for (var i = 1; i <= 8; i++)
            { Move(new Point(start.X + (target.X - start.X) * i / 8, start.Y + (target.Y - start.Y) * i / 8)); Pump(TimeSpan.FromMilliseconds(16)); }
            if (revisitTarget is not null)
            {
                Pump(TimeSpan.FromMilliseconds(100));
                var draft = (LauncherEditSession)typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var displacement = (Center(revisitTarget) - target).Length;
                Check("Không đẩy mục tiêu thư mục trước khi xác nhận giữ con trỏ ở tâm", "FOLDER-18",
                    displacement < 1 && LauncherLayoutRules.Equivalent(data.Layouts.Launcher, draft.Draft),
                    "Mục tiêu giữ nguyên vị trí và bản nháp sắp xếp không đổi", $"displacement={displacement:F1}");
                // Keep holding the mouse while leaving a displaced icon, then
                // approach its new visible position, as in the user's recording.
                Move(grid.PointToScreen(new Point(4, 4))); Pump(TimeSpan.FromMilliseconds(300));
                var visibleCenter = Center(revisitTarget);
                Move(visibleCenter); Pump(TimeSpan.FromMilliseconds(65));
                var anchor = (Rect)typeof(LauncherWindow).GetField("_folderHoverBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var overlay = (Canvas)window.FindName("DragOverlay");
                var actualAnchor = overlay.PointToScreen(new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2));
                var anchorError = (actualAnchor - visibleCenter).Length;
                Check("Sau khi bị đẩy chỗ, rời ra rồi lại gần sẽ dùng vị trí biểu tượng hiện tại", "FOLDER-12", anchorError < 8,
                    "Neo khi di chuột không quay về ô cũ, sai lệch < 8px", anchorError.ToString("F1"));
            }
            Pump(TimeSpan.FromMilliseconds(hover));
            if (snapshot is not null)
            {
                var targetTile = (Button?)typeof(LauncherWindow).GetField("_folderHoverTile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var ghost = (Button?)typeof(LauncherWindow).GetField("_dragGhost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var overlay = (Canvas)window.FindName("DragOverlay");
                var targetHost = (FrameworkElement?)targetTile?.GetType().GetProperty("IconHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targetTile);
                var motion = (TranslateTransform?)typeof(LauncherWindow).GetField("_dragGhostMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var grab = (Point)typeof(LauncherWindow).GetField("_grabPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var pointer = DisplayService.GetCursorPixelPoint();
                var error = double.MaxValue;
                if (pointer is { } pixels && ghost is not null && motion is not null)
                {
                    var mouse = overlay.PointFromScreen(new Point(pixels.X, pixels.Y));
                    error = (new Point(Canvas.GetLeft(ghost) + motion.X, Canvas.GetTop(ghost) + motion.Y) - (mouse - (Vector)grab)).Length;
                }
                var translation = targetHost?.RenderTransform.Value ?? Matrix.Identity;
                Check("Ở giai đoạn xác nhận, mục tiêu không bị hút dính và biểu tượng đang kéo vẫn đi theo chuột", "FOLDER-11",
                    targetHost?.Opacity == 1 && Math.Abs(translation.OffsetX) < .01 && Math.Abs(translation.OffsetY) < .01
                        && error < 1 && overlay.Children.Cast<UIElement>().All(c => c is Button or SharedGlassLens),
                    "Ảnh gốc mục tiêu hiển thị bình thường, không dịch chuyển hút dính, không có bản sao nổi, sai lệch chuột < 1 DIP", $"opacity={targetHost?.Opacity:F1}, pointerError={error:F2}");
                Capture(window, Path.Combine(evidence, snapshot));
            }
            beforeDrop?.Invoke();
            if (!verifyLanding) { IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(420)); }
            else
            {
                var ghost = (Button)typeof(LauncherWindow).GetField("_dragGhost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var motion = (TranslateTransform)typeof(LauncherWindow).GetField("_dragGhostMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var overlay = (Canvas)window.FindName("DragOverlay");
                Point Position() => new(Canvas.GetLeft(ghost) + motion.X, Canvas.GetTop(ghost) + motion.Y);
                var released = Position(); var points = new List<Point>();
                EventHandler sample = (_, _) =>
                {
                    if (overlay.Children.Contains(ghost)) points.Add(Position());
                };
                CompositionTarget.Rendering += sample;
                try { IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(420)); }
                finally { CompositionTarget.Rendering -= sample; }
                var end = Tile((string)from.Tag).TranslatePoint(new Point(), overlay);
                var path = end - released;
                var maximumError = points.Count == 0 ? double.MaxValue : points.Max(p =>
                {
                    var t = path.LengthSquared < .01 ? 0 : Math.Clamp(Vector.Multiply(p - released, path) / path.LengthSquared, 0, 1);
                    return (p - (released + path * t)).Length;
                });
                Check("Thả tay sau khi kéo ra sẽ quay thẳng về điểm rơi thực tế chứ không đi qua góc trên bên trái", "FOLDER-17", points.Count >= 2 && maximumError < 4,
                    "Hoạt ảnh đi theo đường từ điểm thả đến ô thực tế, sai số < 4 DIP", $"samples={points.Count}, deviation={maximumError:F1}");
                File.WriteAllText(Path.Combine(evidence, "member-landing-path.json"), JsonSerializer.Serialize(new
                { start = new { released.X, released.Y }, end = new { end.X, end.Y }, maximumError,
                    points = points.Select(p => new { p.X, p.Y }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        List<LauncherSlot> Slots() => data.Layouts.Launcher.Pages.SelectMany(p => p.Slots).ToList();
        void Click(Button button)
        { Move(Center(button)); IosInput.Mouse(2); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(650)); }
        void CheckHeldMember(string id, string caseId)
        {
            var placeholder = Tile(id);
            var overlay = (Canvas)window.FindName("DragOverlay");
            var ghosts = overlay.Children.OfType<Button>().Where(t => Equals(t.Tag, id) && t.Opacity > .01).Count();
            var closed = !((Grid)window.FindName("GroupPanel")).IsVisible;
            Check("Kéo ra rồi giữ chuột đến khi thư mục thu lại chỉ hiển thị một biểu tượng đang kéo", caseId,
                closed && placeholder.Opacity == 0 && !placeholder.IsHitTestVisible && ghosts == 1,
                "Bảng đã thu lại, vị trí gốc được ẩn, chỉ có một biểu tượng nổi",
                $"closed={closed}, sourceOpacity={placeholder.Opacity:F1}, sourceHit={placeholder.IsHitTestVisible}, ghosts={ghosts}");
            Capture(window, Path.Combine(evidence, $"member-held-{caseId}.png"));
        }
        try
        {
            Drag(Tile("wpf-00"), Center(Tile("wpf-01")), 100);
            Check("Kéo nhanh lướt qua vẫn sắp xếp thứ tự chứ không tạo thư mục", "FOLDER-01", Slots().All(s => s.Group is null)
                && Slots()[0].ItemId == "wpf-01", "Đã sắp xếp và không có thư mục", string.Join(',', Slots().Take(3).Select(s => s.ItemId)));
            Drag(Tile("wpf-00"), Center(Tile("wpf-02")), 700, "fusion-preview-live.png", Tile("wpf-02"));
            var folder = Slots().FirstOrDefault(s => s.Group is not null)?.Group;
            Check("Nhấn giữ, di chuột và thả gốc tạo thư mục", "FOLDER-02", folder?.ItemIds.SequenceEqual(["wpf-02", "wpf-00"]) == true,
                "02 và 00 gộp lại và chỉ xuất hiện một lần", folder is null ? "Chưa tạo thư mục" : string.Join(',', folder.ItemIds));
            if (folder is null) return;
            Capture(window, Path.Combine(evidence, "folder-created-live.png"));
            Drag(Tile("wpf-03"), Center(Tile(folder.Id)), 700);
            folder = Slots().First(s => s.Group is not null).Group!;
            Check("Kéo gốc vào thư mục đã có", "FOLDER-03", folder.ItemIds.Count == 3,
                "3 thành viên", folder.ItemIds.Count.ToString());
            Click(Tile(folder.Id));
            var items = (UniformGrid)window.FindName("GroupItems")!;
            Check("Mở rộng từ vị trí gốc và các thành viên có thể tương tác", "FOLDER-04", items.Children.Count == 3 && ((Grid)window.FindName("GroupPanel")!).IsVisible,
                "Hiển thị 3 thành viên", items.Children.Count.ToString());
            Capture(window, Path.Combine(evidence, "folder-open-live.png"));
            var source = items.Children.OfType<Button>().First();
            Drag(source, Center(items.Children.OfType<Button>().Last()), 180);
            folder = Slots().First(s => s.Group is not null).Group!;
            Check("Kéo sắp xếp lại bên trong thư mục gốc", "FOLDER-05", folder.ItemIds.Last() == "wpf-02", "02 được chuyển xuống cuối", string.Join(',', folder.ItemIds));
            items = (UniformGrid)window.FindName("GroupItems")!;
            var outside = grid.PointToScreen(new Point(14, grid.ActualHeight - 14));
            Drag(items.Children.OfType<Button>().First(), outside, 1100, beforeDrop: () =>
            {
                CheckHeldMember("wpf-00", "FOLDER-13");
                var crossed = Tile("wpf-04");
                Move(Center(crossed)); Pump(TimeSpan.FromMilliseconds(120));
                Move(outside); Pump(TimeSpan.FromMilliseconds(300));
                var host = (FrameworkElement)crossed.GetType().GetProperty("IconHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(crossed)!;
                var transform = host.RenderTransform.Value;
                var displacement = new Vector(transform.OffsetX, transform.OffsetY).Length;
                var target = typeof(LauncherWindow).GetField("_folderHoverTile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Check("Kéo ra rồi lướt nhanh qua biểu tượng khác và rời đi không gây hút dính", "FOLDER-16", target is null && host.Opacity == 1 && displacement < 1,
                    "Đã hủy trạng thái giữ trên ô, mục tiêu hiển thị và không có dịch chuyển hút dính", $"hover={target is not null}, opacity={host.Opacity:F1}, displacement={displacement:F1}");
                Capture(window, Path.Combine(evidence, "folder-exit-crossed-live.png"));
            }, verifyLanding: true);
            Check("Kéo ra gốc khôi phục thành biểu tượng cấp cao nhất", "FOLDER-06", Slots().First(s => s.Group is not null).Group!.ItemIds.Count == 2
                && Slots().Any(s => s.ItemId == "wpf-00"), "Thư mục còn 2 thành viên và cấp cao nhất có 00", string.Join(',', Slots().Where(s => s.ItemId is not null).Take(5).Select(s => s.ItemId)));
            folder = Slots().First(s => s.Group is not null).Group!;
            Click(Tile(folder.Id));
            IosInput.Key(0x1b, false); IosInput.Key(0x1b, true); Pump(TimeSpan.FromMilliseconds(700));
            Check("Nhấn Esc thu gọn ngược lại và Launcher vẫn mở", "FOLDER-07", window.IsVisible && !((Grid)window.FindName("GroupPanel")!).IsVisible,
                "Launcher đang mở, thư mục đã đóng", ((Grid)window.FindName("GroupPanel")!).Visibility.ToString());
            Click(Tile(folder.Id));
            Move(grid.PointToScreen(new Point(5, 5))); IosInput.Mouse(2); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(650));
            Check("Nhấp bên ngoài để thu gọn và không mở ứng dụng phía sau", "FOLDER-08", !((Grid)window.FindName("GroupPanel")!).IsVisible && window.IsVisible,
                "Thư mục đã thu gọn", ((Grid)window.FindName("GroupPanel")!).Visibility.ToString());
            Click(Tile(folder.Id));
            items = (UniformGrid)window.FindName("GroupItems")!;
            var extracted = items.Children.OfType<Button>().First();
            var extractedId = (string)extracted.Tag;
            Drag(extracted, outside, 1100, beforeDrop: () => CheckHeldMember(extractedId, "FOLDER-14"));
            Check("Thư mục hai thành viên tự động giải tán sau khi kéo ra và không trùng lặp hay mất biểu tượng", "FOLDER-15",
                Slots().All(s => s.Group is null) && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count() == 36
                    && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Distinct().Count() == 36,
                "36 ứng dụng độc lập và không còn thư mục", $"slots={Slots().Count}, groups={Slots().Count(s => s.Group is not null)}");
            var reloaded = new GlassquidDataStore(data.Paths); reloaded.LoadAll();
            Check("Bố cục sau khi sắp xếp gốc nhất quán khi tải lại", "FOLDER-09", LauncherLayoutRules.Equivalent(data.Layouts.Launcher, reloaded.Layouts.Launcher),
                "Bố cục và thành viên thư mục nhất quán", "So sánh từng mục về tên, vị trí, nội dung");
            Capture(window, Path.Combine(evidence, "folder-final-live.png"));
            var surfaces = FolderDescendants(window).OfType<LiquidGlassSurface>().Count();
            Check("Thư mục tái sử dụng một chất liệu thu thập duy nhất", "FOLDER-10", surfaces == 1, "Vẫn chỉ có một LiquidGlassSurface", surfaces.ToString());
        }
        finally
        {
            IosInput.Mouse(4); CompositionTarget.Rendering -= measure;
            var ordered = stamps.Order().ToArray();
            File.WriteAllText(Path.Combine(evidence, "folder-render-cadence.json"), JsonSerializer.Serialize(new
            {
                callbacks = ordered.Length,
                medianMilliseconds = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2],
                p95Milliseconds = ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * .95)],
                maxMilliseconds = ordered.Length == 0 ? 0 : ordered[^1],
                note = "Unique WPF RenderingTime intervals during native drags and folder animations; includes screenshot encoding and settle waits; not displayed GPU FPS."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<DependencyObject> FolderDescendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in FolderDescendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
