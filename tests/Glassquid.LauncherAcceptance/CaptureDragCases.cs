using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Glassquid.Core.Storage;
using Glassquid.Core.Models;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyCaptureDragInput(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        var origin = typeof(LauncherWindow).GetField("_dragOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var moves = 0;
        MouseEventHandler moving = (_, _) => { if (origin.GetValue(window) is not null) moves++; };
        window.AddHandler(Mouse.PreviewMouseMoveEvent, moving, true);
        try { VerifyIosNativeInput(window, grid, data, evidence); }
        finally { window.RemoveHandler(Mouse.PreviewMouseMoveEvent, moving); }

        window.Activate();
        var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var overlay = (Canvas)window.FindName("DragOverlay")!;
        var starts = 0; var finishes = 0; var interrupted = false;
        EventHandler started = (_, _) => starts++;
        EventHandler finished = (_, _) => finishes++;
        window.InternalDragStarted += started;
        window.InternalDragFinished += finished;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            interrupted = overlay.Children.OfType<Button>().Count() == 1;
            Mouse.Capture(null);
        };
        var point = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var target = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        IosInput.SetCursorPos((int)point.X, (int)point.Y);
        IosInput.Mouse(2);
        timer.Start();
        var script = Task.Run(async () =>
        {
            await Task.Delay(430);
            for (var i = 0; i < 8; i++)
            {
                IosInput.SetCursorPos((int)(i % 2 == 0 ? target.X : point.X), (int)point.Y);
                await Task.Delay(20);
            }
            IosInput.SetCursorPos((int)target.X, (int)target.Y);
            await Task.Delay(450); IosInput.Mouse(4);
        });
        moves = 0;
        window.AddHandler(Mouse.PreviewMouseMoveEvent, moving, true);
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4));
            Pump(TimeSpan.FromMilliseconds(250));
            Check("Di chuyển cục bộ liên tục vẫn gửi sự kiện chuột thông thường", "regression/capture-input", moves >= 5,
                "Ít nhất 5 trên 8 lần di chuyển được gửi bình thường", moves.ToString());
            Check("Sau khi bị ngắt bắt chuột, hủy bản nháp và không lưu", "regression/capture-loss",
                interrupted && before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && overlay.Children.Count == 0,
                "Tệp bố cục không đổi, lớp nổi được làm trống", $"interrupted={interrupted},unchanged={before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))},ghost={overlay.Children.Count}");
            Check("Ngắt bắt chuột chỉ kết thúc phiên kéo một lần", "regression/capture-loss", starts == 1 && finishes == 1,
                "start=1,finish=1", $"start={starts},finish={finishes}");
        }
        finally
        {
            timer.Stop(); IosInput.Mouse(4);
            window.RemoveHandler(Mouse.PreviewMouseMoveEvent, moving);
            window.InternalDragStarted -= started; window.InternalDragFinished -= finished;
        }
        VerifyCaptureWindowTransfer(window, grid, data);
        VerifyCaptureHide(window, grid, data);
        VerifyCaptureTrash(window, grid, data);
    }

    private static void VerifyCaptureWindowTransfer(LauncherWindow window, UniformGrid grid, GlassquidDataStore data)
    {
        var receiver = new Window { Width = 110, Height = 80, Left = window.Left + window.Width - 120,
            Top = window.Top + 70, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, AllowDrop = true, Content = new Border { Background = System.Windows.Media.Brushes.SteelBlue } };
        var overs = 0; string? dropped = null;
        receiver.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)) { overs++; e.Effects = DragDropEffects.Copy; }
            e.Handled = true;
        };
        receiver.Drop += (_, e) =>
        {
            dropped = e.Data.GetData(LauncherDragContract.SlotDataFormat) as string;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
        };
        receiver.Show(); Pump(TimeSpan.FromMilliseconds(150)); window.Activate();
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var local = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        var outside = receiver.PointToScreen(new Point(55, 40));
        var source = (string)((Button)grid.Children[0]).Tag;
        var recaptured = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) => { timer.Stop(); recaptured = ReferenceEquals(Mouse.Captured, window); };
        void Move(Point p) => IosInput.SetCursorPos((int)p.X, (int)p.Y);
        try
        {
            Move(start); IosInput.Mouse(2); timer.Start();
            var returnScript = Task.Run(async () =>
            {
                await Task.Delay(500); Move(outside);
                await Task.Delay(350); Move(start);
                await Task.Delay(350); Move(local);
                await Task.Delay(350); IosInput.Mouse(4);
            });
            PumpUntil(() => returnScript.IsCompleted, TimeSpan.FromSeconds(5)); Pump(TimeSpan.FromMilliseconds(300));
            Check("Kéo qua cửa sổ giữ nguyên dữ liệu OLE chuẩn", "regression/capture-transfer", overs > 0,
                "Cửa sổ đích nhận được SlotDataFormat của Launchpad", overs.ToString());
            Check("Chưa thả tay mà quay lại Launchpad sẽ khôi phục bắt chuột và lưu bình thường", "regression/capture-transfer",
                recaptured && LauncherLayoutRules.AppIds(data.Layouts.Launcher)[3] == source,
                "Khôi phục bắt chuột cục bộ sau khi quay lại, cuối cùng đặt ở ô thứ 4", $"recaptured={recaptured},slot3={LauncherLayoutRules.AppIds(data.Layouts.Launcher)[3]}");
            var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            source = (string)((Button)grid.Children[0]).Tag;
            Move(start); IosInput.Mouse(2);
            var dropScript = Task.Run(async () =>
            {
                await Task.Delay(500); Move(outside);
                await Task.Delay(400); IosInput.Mouse(4);
            });
            PumpUntil(() => dropScript.IsCompleted, TimeSpan.FromSeconds(5)); Pump(TimeSpan.FromMilliseconds(250));
            Check("Cửa sổ ngoài nhận thả chuột và bản nháp Launchpad không ghi xuống đĩa", "regression/capture-transfer",
                dropped == source && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && Mouse.Captured is null,
                "Cửa sổ ngoài nhận đúng Id, bố cục Launchpad không đổi, giải phóng bắt chuột", $"payload={dropped},source={source},unchanged={bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))},capture={Mouse.Captured}");
        }
        finally { timer.Stop(); IosInput.Mouse(4); receiver.Close(); }
    }

    private static void VerifyCaptureHide(LauncherWindow window, UniformGrid grid, GlassquidDataStore data)
    {
        var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var starts = 0; var finishes = 0; var picked = false;
        EventHandler started = (_, _) => starts++;
        EventHandler finished = (_, _) => finishes++;
        window.InternalDragStarted += started; window.InternalDragFinished += finished;
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var target = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => { timer.Stop(); picked = ((Canvas)window.FindName("DragOverlay")!).Children.OfType<Button>().Count() == 1; window.Hide(); };
        IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2); timer.Start();
        var script = Task.Run(async () =>
        {
            await Task.Delay(500); IosInput.SetCursorPos((int)target.X, (int)target.Y);
            await Task.Delay(550); IosInput.Mouse(4);
        });
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4));
            Check("Ẩn cửa sổ hủy bản nháp, giải phóng bắt chuột và kết thúc một lần", "regression/capture-hide",
                picked && starts == 1 && finishes == 1 && Mouse.Captured is null
                && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))
                && ((Canvas)window.FindName("DragOverlay")!).Children.Count == 0,
                "Bắt chuột và lớp nổi đều trống, bố cục không đổi, start/finish mỗi loại một lần", $"picked={picked},start={starts},finish={finishes},capture={Mouse.Captured},unchanged={bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))}");
        }
        finally
        {
            timer.Stop(); IosInput.Mouse(4);
            window.InternalDragStarted -= started; window.InternalDragFinished -= finished;
            window.Show(); window.Activate(); Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    private static void VerifyCaptureTrash(LauncherWindow window, UniformGrid grid, GlassquidDataStore data)
    {
        var source = (string)((Button)grid.Children[0]).Tag;
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        Point target = default; var ready = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); var trash = (FrameworkElement)window.FindName("TrashDropTarget")!;
            target = trash.PointToScreen(new Point(trash.ActualWidth / 2, trash.ActualHeight / 2));
            Volatile.Write(ref ready, true);
        };
        IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2); timer.Start();
        var script = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; !Volatile.Read(ref ready) && i < 40; i++) await Task.Delay(50);
                if (Volatile.Read(ref ready)) IosInput.SetCursorPos((int)target.X, (int)target.Y);
                await Task.Delay(300);
            }
            finally { IosInput.Mouse(4); }
        });
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4)); Pump(TimeSpan.FromMilliseconds(250));
            Check("Kéo khi đang bắt chuột vào vùng xóa chỉ xóa lối tắt khỏi Launchpad", "regression/capture-trash",
                ready && !LauncherLayoutRules.AppIds(data.Layouts.Launcher).Contains(source)
                && data.Library.Find(source) is not null && Mouse.Captured is null,
                "Xóa khỏi Launchpad, giữ lại trong thư viện ứng dụng, giải phóng bắt chuột", $"ready={ready},layoutContains={LauncherLayoutRules.AppIds(data.Layouts.Launcher).Contains(source)},libraryContains={data.Library.Find(source) is not null},capture={Mouse.Captured}");
        }
        finally { timer.Stop(); IosInput.Mouse(4); }
    }
}
