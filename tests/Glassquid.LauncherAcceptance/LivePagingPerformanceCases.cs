using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.Core.Imaging;
using Glassquid.Core.Storage;
using Glassquid.Launcher;
using Glassquid.UI.Controls;

internal static partial class Program
{
    private static CaptureTraceLog? _isolatedTrace;
    private static void UseIsolatedBackdrop(LauncherWindow window, string executable)
    {
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
        var field = typeof(GlassBackgroundLayer).GetField("_compatible", fields)!;
        ((SharedBackdropFeed)field.GetValue(background)!).Dispose();
        _isolatedTrace = new CaptureTraceLog();
        field.SetValue(background, new SharedBackdropFeed((ScreenRect region, bool _, Action<CaptureTrace>? trace)
            => new RecoveringDesktopSceneCapture(region, trace, executable)) { TraceObserved = _isolatedTrace.Observe });
    }

    private static void VerifyAsyncPageIcons(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string root)
    {
        var path = Path.Combine(root, "page-icon.png");
        byte[] Pixels(byte green, byte red)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
            return pixels;
        }
        File.WriteAllBytes(path, PngWriter.EncodeBgra(Pixels(255, 0), 64, 64));
        foreach (var entry in data.Library.Items.ToArray())
        { entry.CustomIconPath = path; data.Library.Update(entry); }
        window.RefreshLibrary();
        Image? Icon() => grid.Children.OfType<Button>().FirstOrDefault()?.Content is StackPanel stack
            && stack.Children[0] is Border border ? border.Child as Image : null;
        Check("Biểu tượng nguội hiển thị ảnh thật sau khi giải mã nền hoàn tất", "performance/icons", PumpUntil(() => Icon()?.Source is BitmapImage, TimeSpan.FromSeconds(3)),
            "Giải mã nền hoàn tất, Image thật hiển thị", Icon()?.Source?.GetType().Name ?? "placeholder");
        var first = Icon()?.Source;
        window.NavigatePage(1); Pump(TimeSpan.FromMilliseconds(300));
        window.NavigatePage(-1); Pump(TimeSpan.FromMilliseconds(300));
        Check("Chuyển trang lại tái sử dụng biểu tượng đã giải mã", "performance/icons", first is not null && ReferenceEquals(first, Icon()?.Source),
            "Cùng một BitmapImage đã đóng băng", "referenceMatches=" + ReferenceEquals(first, Icon()?.Source));
        File.WriteAllBytes(path, PngWriter.EncodeBgra(Pixels(0, 255), 64, 64));
        window.RefreshLibrary();
        bool Red()
        {
            if (Icon()?.Source is not BitmapSource image) return false;
            var pixel = new byte[4]; image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            return pixel[2] >= 250 && pixel[1] <= 5;
        }
        Check("Cập nhật biểu tượng tùy chỉnh cùng đường dẫn làm mới để hiển thị pixel mới", "performance/icons", PumpUntil(Red, TimeSpan.FromSeconds(3)),
            "Ảnh mới màu đỏ", "red=" + Red());
    }

    private static void VerifyLivePaging(LauncherWindow window, UniformGrid grid, string evidence)
    {
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        Check("Quy trình thu thập nền đầy đủ nhận được nền thời gian thực của trình khởi chạy", "performance/live", PumpUntil(() => surface.BackdropSource == GlassBackdropSource.LiveDesktop,
            TimeSpan.FromSeconds(6)), "LiveDesktop", surface.BackdropSource.ToString());
        var dispatch = new List<double>();
        var heartbeatGaps = new List<double>();
        var previous = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        { var now = Stopwatch.GetTimestamp(); heartbeatGaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now; };
        timer.Start();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var watch = Stopwatch.StartNew();
                window.NavigatePage(i % 2 == 0 ? 1 : -1);
                dispatch.Add(watch.Elapsed.TotalMilliseconds);
                Pump(TimeSpan.FromMilliseconds(280));
            }
        }
        finally { timer.Stop(); }
        Check("Nội dung đúng và giao diện vẫn phản hồi sau khi chuyển trang liên tục", "performance/live", grid.Children.Count == 35 && heartbeatGaps.Count > 40,
            "Trang đầu 35 mục, nhịp độ ưu tiên nhập liệu vẫn chạy liên tục", $"items={grid.Children.Count},heartbeats={heartbeatGaps.Count}");
        Check("Xử lý đồng bộ mỗi lần chuyển trang không bị nghẽn lâu", "performance/live", dispatch.Max() < 250,
            "Lời gọi đồng bộ < 250 ms", $"maxDispatchMs={dispatch.Max():F2}");
        if (_isolatedTrace is not null)
            Check("Chuyển trang thực sự sử dụng tiến trình thu thập cô lập", "performance/live", _isolatedTrace.Snapshot().Counts.Any(c => c.Stage == "BackdropProvider" && c.Reason == "LiveDesktop"),
                "Đường dẫn IsolatedSharedVisual thật", "native helper trace recorded");
        File.WriteAllText(Path.Combine(evidence, "live-paging-performance.json"), JsonSerializer.Serialize(new
        {
            note = "Real WPF page navigation with the production background worker; dispatcher timings are not displayed FPS. Host build remains the actual host, not an emulated 25H2 OS.",
            dispatchMilliseconds = dispatch, heartbeatGapsMilliseconds = heartbeatGaps,
            capture = _isolatedTrace?.Snapshot()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
