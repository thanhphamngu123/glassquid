using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.Launcher;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

internal static partial class Program
{
    private static void VerifyContinuousBackdrop(LauncherWindow window, string? fixtureExe, string evidence, bool desktop = false, int sampleSeconds = 6)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var readyPath = Path.Combine(evidence, "moving-background-ready-" + Guid.NewGuid().ToString("n") + ".json");
        Process? fixture = null;
        if (!desktop)
        {
            var start = new ProcessStartInfo(fixtureExe!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--moving-backdrop-fixture"); start.ArgumentList.Add(readyPath);
            fixture = Process.Start(start)!;
        }
        using var fixtureLifetime = fixture;
        try
        {
            if (!desktop) Check("Nền động của tiến trình độc lập đã hiển thị", "continuous", PumpUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(4)), "ready", fixture!.Id.ToString());
            var display = window.PrimaryDisplaySession!.Display;
            window.CloseWithTransition(); Pump(TimeSpan.FromMilliseconds(400));
            window.ShowWithTransition(new Glassquid.Platform.Displays.PixelRect(display.WorkArea.X + 24, display.WorkArea.Bottom - 88, 48, 64));
            Pump(TimeSpan.FromSeconds(2));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            var queue = (Queue<long>)typeof(LiquidGlassSurface).GetField("_opticalFrameTimestamps", fields)!.GetValue(surface)!;
            var cpuField = typeof(LiquidGlassSurface).GetField("_lastOpticsMilliseconds", fields)!;
            var bitmapField = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", fields)!;
            var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
            var runs = new List<object>();
            foreach (var clarity in new[] { AppearanceSettings.DefaultLauncherGlassTransparency, 1.0 })
            {
                var appearance = ThemeService.Current!.Appearance;
                appearance.LauncherGlassTransparency = clarity;
                ThemeService.Current.Update(appearance); Pump(TimeSpan.FromSeconds(1));
                var observations = new List<object>(); var timings = new List<long>();
                var heartbeat = new List<double>(); var previous = Stopwatch.GetTimestamp(); long last = 0;
                var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
                timer.Tick += (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp(); heartbeat.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now;
                    if (queue.Count == 0 || queue.Last() == last) return;
                    last = queue.Last(); timings.Add(last);
                    var captured = background.LastCaptureTiming;
                    observations.Add(new { atTicks = last, opticsMilliseconds = (double)cpuField.GetValue(surface)!,
                        capturePixels = captured?.PixelStatistics,
                        sourceAgeMilliseconds = captured is null ? 0 : Stopwatch.GetElapsedTime(captured.SourceTicks, now).TotalMilliseconds,
                        acquisitionMilliseconds = captured is null ? 0 : Stopwatch.GetElapsedTime(captured.StartTicks, captured.EndTicks).TotalMilliseconds });
                };
                var clock = Stopwatch.StartNew(); timer.Start();
                try { Pump(TimeSpan.FromSeconds(sampleSeconds)); } finally { timer.Stop(); }
                var bitmap = (BitmapSource?)bitmapField.GetValue(surface);
                object? opticalPixels = null;
                if (bitmap is not null)
                {
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    opticalPixels = InspectPixels(pixels);
                }
                runs.Add(new { clarity, elapsedSeconds = clock.Elapsed.TotalSeconds, opticalUpdates = timings.Count,
                    opticalPixels, windowVisible = window.IsVisible, surfaceVisible = surface.IsVisible,
                    measuredOpticalUpdatesPerSecond = timings.Count / clock.Elapsed.TotalSeconds,
                    frameGapsMilliseconds = timings.Zip(timings.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).ToArray(),
                    heartbeatGapsMilliseconds = heartbeat, observations, bitmapWidth = bitmap?.PixelWidth, bitmapHeight = bitmap?.PixelHeight });
                Check($"Trình khởi chạy clarity={clarity} đã nhận nền màn hình desktop", "continuous", (desktop ? bitmap is not null : timings.Count > 20) && surface.BackdropSource == GlassBackdropSource.LiveDesktop,
                    desktop ? "Đã nhận khung hình desktop; hình nền tĩnh có thể giữ nguyên một khung hình" : "Nền thực tế động liên tục cập nhật", $"updates={timings.Count},source={surface.BackdropSource}");
                if (!desktop) Check($"Nhịp cập nhật của nền tĩnh clarity={clarity} đạt mục tiêu kiểm tra trên máy cục bộ", "continuous/cadence",
                    timings.Count / clock.Elapsed.TotalSeconds >= 40,
                    "Nền động được kiểm soát ở 60Hz trên máy cục bộ ghi ít nhất 40 lần/giây, không đại diện cho FPS hiển thị", $"updatesPerSecond={timings.Count / clock.Elapsed.TotalSeconds:F2}");
            }
            var clockField = typeof(LiquidGlassSurface).GetField("_renderClockAttached", fields);
            var beforeHide = queue.LastOrDefault();
            window.Hide(); Pump(TimeSpan.FromMilliseconds(250));
            Check("Dừng gửi nền và hủy đăng ký sự kiện kết xuất sau khi ẩn", "continuous/lifecycle",
                queue.LastOrDefault() == beforeHide && clockField?.GetValue(surface) is false,
                "Đã dừng cập nhật và hủy đăng ký", $"latestUnchanged={queue.LastOrDefault() == beforeHide},clockAttached={clockField?.GetValue(surface)}");
            window.Show();
            Check("Khôi phục khung hình nền mới sau khi mở lại", "continuous/lifecycle",
                PumpUntil(() => queue.Count > 0 && queue.Last() > beforeHide && surface.BackdropSource == GlassBackdropSource.LiveDesktop, TimeSpan.FromSeconds(3)),
                "Nền thời gian thực mới", surface.BackdropSource.ToString());
            File.WriteAllText(Path.Combine(evidence, "continuous-backdrop.json"), JsonSerializer.Serialize(new
            { note = "Production launcher transition material. Optical/dispatcher timings are not GPU displayed FPS. Pixel statistics describe captured/CPU material buffers, not the final GPU shader output.", desktop, sampleSeconds,
                environment = Environment.OSVersion.VersionString, processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                logicalProcessors = Environment.ProcessorCount, renderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                primaryDisplay = display, runs, capture = _isolatedTrace?.Snapshot() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (fixture is { HasExited: false }) fixture.Kill(); }
    }

    // Numeric samples distinguish an empty/black source from an optical failure
    // without exporting desktop pixels or screenshots in the friend's report.
    private static object InspectPixels(ReadOnlySpan<byte> pixels)
    {
        var count = pixels.Length / 4; var step = Math.Max(1, count / 512);
        var samples = 0; var black = 0; var transparent = 0; long sum = 0;
        for (var pixel = 0; pixel < count; pixel += step)
        {
            var i = pixel * 4; var maximum = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            samples++; if (maximum <= 3) black++; if (pixels[i + 3] == 0) transparent++;
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }
        return new { samples, nearBlackFraction = (double)black / Math.Max(1, samples),
            transparentFraction = (double)transparent / Math.Max(1, samples), meanChannel = (double)sum / Math.Max(1, samples * 3) };
    }
}
