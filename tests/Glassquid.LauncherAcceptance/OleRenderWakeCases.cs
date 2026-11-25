using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glassquid.Core.Storage;
using Glassquid.Launcher;
using Glassquid.Platform.Displays;

internal static partial class Program
{
    private static void VerifyOleRenderWake(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        var tile = (Button)grid.Children[0];
        var overlay = (Canvas)window.FindName("DragOverlay")!;
        var tested = false;
        var scheduled = false;
        EventHandler started = (_, _) =>
        {
            if (scheduled) return;
            scheduled = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
            Thread.Sleep(12); // Past the feedback sampling interval, after the initial render completed.
            tested = true;
            var ghost = (Button)overlay.Children[0];
            overlay.UpdateLayout();
            var grab = (Point)typeof(LauncherWindow).GetField("_grabPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var before = ghost.TranslatePoint(grab, overlay);
            var initial = overlay.PointToScreen(before);
            var savedCursor = DisplayService.GetCursorPixelPoint();
            var destination = new Point((int)initial.X + 33, (int)initial.Y + 17);
            try
            {
            IosInput.SetCursorPos((int)destination.X, (int)destination.Y);

            // Characterize WPF's actual pending render operation at Input priority.
            // Only this test accesses WPF internals; product code uses public visual updates.
            var mediaType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
            var media = mediaType.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window.Dispatcher]);
            mediaType.GetMethod("PostRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(media, null);
            var operation = (DispatcherOperation)mediaType.GetField("_currentRenderOp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media)!;
            operation.Priority = DispatcherPriority.Input;
            var commitField = typeof(LauncherWindow).GetField("_dragPreviewCommits", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var commits = (long)commitField.GetValue(window)!;
            var feedback = (GiveFeedbackEventArgs)Activator.CreateInstance(typeof(GiveFeedbackEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [DragDropEffects.Move, false], null)!;
            feedback.RoutedEvent = DragDrop.GiveFeedbackEvent;
            tile.RaiseEvent(feedback);
            var after = ghost.TranslatePoint(grab, overlay);
            var expected = overlay.PointFromScreen(destination) - before;
            Check("Khi kết xuất chưa tiến triển, phản hồi OLE vẫn đọc vị trí bám tay mới nhất", "regression/ole-render-wake",
                ((after - before) - expected).Length < 1,
                "Đã cập nhật DIP tương ứng với 33x17 pixel vật lý", $"actual={after-before},expected={expected}");
            Check("Cập nhật trực quan bám tay đánh thức tác vụ kết xuất WPF thực bị hạ xuống mức ưu tiên Input", "regression/ole-render-wake",
                operation.Priority >= DispatcherPriority.Render,
                "Tác vụ kết xuất đang chờ khôi phục về mức ưu tiên Render hoặc cao hơn", operation.Priority.ToString());
            Check("Phản hồi OLE không gửi bản xem trước sắp xếp và cũng không làm lớp phủ bố cục lại", "regression/ole-render-wake",
                (long)commitField.GetValue(window)! == commits && overlay.IsArrangeValid,
                "Số lần gửi bản xem trước không đổi, bố cục hợp lệ", $"commits={(long)commitField.GetValue(window)!-commits},arrange={overlay.IsArrangeValid}");
            }
            finally { if (savedCursor is { } p) IosInput.SetCursorPos(p.X, p.Y); }
            }));
        };
        window.InternalDragStarted += started;
        try { VerifyIosNativeInput(window, grid, data, evidence); }
        finally { window.InternalDragStarted -= started; }
        Check("Kiểm tra hồi quy điều phối đi qua đường dẫn nhấn giữ nhấc lên thực tế", "regression/ole-render-wake", tested, "Sự kiện nhấc lên đã kích hoạt", tested.ToString());
    }
}
