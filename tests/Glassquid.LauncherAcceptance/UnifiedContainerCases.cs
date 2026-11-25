using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Launcher;
using Glassquid.Platform.Displays;
using Glassquid.UI.Controls;

internal static partial class Program
{
    private static void VerifyUnifiedContainer(LauncherWindow window, UniformGrid grid, string evidence)
    {
        var display = DisplayService.Primary();
        if (display is null) return;
        window.Hide();
        var live = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
        window.ContainerAnchorProvider = () => live;
        window.ShowWithTransition(new PixelRect(live.X + 100, live.Y, 48, 48));
        Invoke("StopContainerTransform");
        var motion = (ContainerMorphMotion)Field("_containerMotion");
        Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        Check("Launchpad tái sử dụng tham số kính Dock mà không bật quang học độc lập", "L-01", !surface.UseLaunchpadOptics,
            "UseLaunchpadOptics false", surface.UseLaunchpadOptics.ToString());
        var host = (PixelRect)Field("_transformHostBounds");
        var dip = (double)Field("_transformDipPerPixel");
        var expected = new Rect((live.X - host.X) * dip, (live.Y - host.Y) * dip, live.Width * dip, live.Height * dip);
        Check("Điểm neo thời gian thực ghi đè tọa độ gọi đã lỗi thời", "L-01", Near(window.Clip.Bounds, expected), expected.ToString(), window.Clip.Bounds.ToString());
        Check("Khung hình đầu tiên của kính giữ trạng thái hiển thị thay vì mờ dần vào", "L-01", window.Opacity == 1, "Opacity 1", window.Opacity.ToString());
        window.UpdateLayout();
        var glyph = (FrameworkElement)window.FindName("CollapsedGlyph")!;
        var panelRoot = (FrameworkElement)window.FindName("LauncherPanelRoot")!;
        var launcherContent = (FrameworkElement)window.FindName("LauncherContent")!;
        var contentScale = (ScaleTransform)window.FindName("ContentScaleTransform")!;
        var clipRoot = (FrameworkElement)window.FindName("ContainerTransformClipRoot")!;
        Check("Cửa sổ và nội dung tái sử dụng cùng một đường viền", "L-01", ReferenceEquals(window.Clip, clipRoot.Clip),
            "Cùng một đường viền đã đóng băng", ReferenceEquals(window.Clip, clipRoot.Clip).ToString());
        var previousTime = 0d;
        foreach (var seconds in new[] { 0d, .05, .10, .18, .26, .38, .45 })
        {
            motion.Advance(seconds - previousTime);
            previousTime = seconds;
            Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
            var bounds = window.Clip.Bounds;
            var glyphBounds = glyph.RenderTransform.TransformBounds(new Rect(0, 0, glyph.Width, glyph.Height));
            var glyphSize = Math.Min(64, Math.Min(bounds.Width, bounds.Height));
            Check($"Biến đổi biểu tượng giữ nguyên kích thước và căn giữa {seconds:0.00}s", "L-01",
                Math.Abs(glyphBounds.Width - glyphSize) < .1 && Math.Abs(glyphBounds.Height - glyphSize) < .1
                && Math.Abs(glyphBounds.X + glyphBounds.Width / 2 - bounds.X - bounds.Width / 2) < .1
                && Math.Abs(glyphBounds.Y + glyphBounds.Height / 2 - bounds.Y - bounds.Height / 2) < .1,
                "Đồng tâm với vùng chứa và kích thước bằng min(64, chiều rộng/cao vùng chứa)", glyphBounds.ToString());
            var same = surface.CaptureBoundsInWindow is { } capture && Near(capture, bounds)
                && surface.RenderTransform.Value.IsIdentity;
            Check($"Lấy mẫu hình học/nền đồng khung và kính không bị kéo giãn theo nội dung {seconds:0.00}s", "L-01", same,
                "Hình chữ nhật lấy mẫu = đường viền khả kiến; phép biến đổi kính là ma trận đơn vị", $"clip {bounds}; capture {surface.CaptureBoundsInWindow}; transform {surface.RenderTransform.Value}");
            Check($"Biến dạng vỏ kính không làm co giãn bố cục nội dung cuối cùng {seconds:0.00}s", "L-01", panelRoot.RenderTransform.Value.IsIdentity,
                "Nút gốc nội dung giữ phép biến đổi đơn vị", panelRoot.RenderTransform.Value.ToString());
            Check($"Khung hình hoạt ảnh không đo lại bố cục ứng dụng {seconds:0.00}s", "L-01",
                panelRoot.IsMeasureValid && panelRoot.IsArrangeValid && launcherContent.IsMeasureValid && launcherContent.IsArrangeValid,
                "Bố cục ứng dụng vẫn hợp lệ", $"Nút gốc {panelRoot.IsMeasureValid}/{panelRoot.IsArrangeValid}; Nội dung {launcherContent.IsMeasureValid}/{launcherContent.IsArrangeValid}");
            Check($"Nội dung ứng dụng luôn chỉ thu phóng nhẹ {seconds:0.00}s", "L-01",
                contentScale.ScaleX >= .96 && contentScale.ScaleX <= 1 && contentScale.ScaleX == contentScale.ScaleY,
                "Đồng tỷ lệ .96..1", contentScale.Value.ToString());
            Check($"Đường viền nảy đàn hồi nằm gọn trong vùng chứa cố định {seconds:0.00}s", "L-01",
                bounds.X >= -.1 && bounds.Y >= -.1 && bounds.Right <= host.Width * dip + .1 && bounds.Bottom <= host.Height * dip + .1,
                "Toàn bộ đường viền nằm trong ranh giới vùng chứa", $"Đường viền {bounds}; Vùng chứa {host.Width * dip:0.00}x{host.Height * dip:0.00}");
            if (seconds == .05)
            {
                Check("Giai đoạn đầu mở rộng tạo vỏ kính trước khi hiển thị nội dung ứng dụng", "L-01", launcherContent.Opacity == 0,
                    "50ms nội dung ứng dụng ẩn hoàn toàn", launcherContent.Opacity.ToString());
            }
            if (seconds == .45)
                Check("Sau khi kính mở rộng, nội dung ứng dụng hoàn tất hiệu ứng hiện dần", "L-01", launcherContent.Opacity == 1 && contentScale.ScaleX == 1,
                    "450ms nội dung hiển thị đầy đủ và giữ nguyên kích thước gốc", $"Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
            if (seconds is .05 or .18 or .38)
                Capture(window, Path.Combine(evidence, $"transition-{seconds:0.00}s.png"));
        }
        motion.SnapToTarget();
        var captureField = typeof(LiquidGlassSurface).GetField("_transitionCaptureBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var movingCapture = (Rect?)captureField.GetValue(surface);
        VerifySettlingPixels();
        var settledCapture = (Rect?)captureField.GetValue(surface);
        if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
            Check("Khi mở rộng ổn định không chuyển phạm vi lấy mẫu nền và đường dẫn quang học", "L-01",
                movingCapture is { Width: > 0, Height: > 0 } && settledCapture == movingCapture,
                $"Giữ lấy mẫu vùng chứa cố định {movingCapture}", $"Lấy mẫu khi ổn định {settledCapture?.ToString() ?? "Bảng điều khiển lấy mẫu lại"}");
        Pump(TimeSpan.FromMilliseconds(120));
        Capture(window, Path.Combine(evidence, "unified-grid-expanded.png"));
        live = live with { X = live.X + 36 };
        window.CloseWithTransition();
        if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
            Check("Khung hình đầu tiên khi thu gọn tiếp tục dùng tọa độ lấy mẫu nền sau khi mở rộng", "L-01",
                settledCapture is not null && captureField.GetValue(surface) is Rect closingCapture && closingCapture == settledCapture,
                $"Lấy mẫu {settledCapture}", $"Lấy mẫu {captureField.GetValue(surface)}");
        Check("Đóng đảo ngược đọc lại vị trí nút", "L-01", (PixelRect)Field("_transformSourceBounds") == live, live.ToString(), Field("_transformSourceBounds").ToString()!);
        Pump(TimeSpan.FromMilliseconds(120));
        Check("Khi đóng làm mờ dần nội dung trước rồi mới thu hồi vỏ kính", "L-01", window.IsVisible && launcherContent.Opacity == 0,
            "120ms nội dung ẩn và vỏ vẫn hiển thị", $"Cửa sổ {window.IsVisible}; Nội dung {launcherContent.Opacity:0.000}");
        window.ShowWithTransition(null);
        var reversed = PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        Check("Mở lại trong khi đang đóng đảo chiều liên tục", "L-01", reversed && !window.IsClosingContainer, "Mở rộng lại và có thể tương tác", reversed.ToString());
        window.CloseWithTransition();
        Check("Ẩn sau khi thu hồi đảo ngược", "L-01", PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2)), "Hidden", window.IsVisible.ToString());
        window.ShowWithTransition(live);
        Pump(TimeSpan.FromMilliseconds(50));
        var interruptedOpening = window.IsVisible && !window.CanNavigatePages;
        window.RefreshDisplayPlacement();
        Check("Làm mới vị trí hiển thị khi đang mở rộng khôi phục đầy đủ nội dung có thể tương tác", "L-01",
            interruptedOpening && window.IsVisible && window.CanNavigatePages
            && launcherContent.Opacity == 1 && contentScale.ScaleX == 1 && contentScale.ScaleY == 1,
            "Sau khi ngắt mở rộng ở 50ms, cửa sổ hiển thị, chuyển trang được, Opacity/Scale nội dung là 1",
            $"Đã ngắt mở rộng {interruptedOpening}; Cửa sổ {window.IsVisible}; Chuyển trang được {window.CanNavigatePages}; Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
        window.Hide();
        window.ShowWithTransition(live);
        PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        window.CloseWithTransition();
        Pump(TimeSpan.FromMilliseconds(120));
        var interruptedClosing = window.IsVisible && window.IsClosingContainer;
        window.RefreshDisplayPlacement();
        Check("Làm mới vị trí hiển thị khi đang đóng khôi phục đầy đủ nội dung có thể tương tác", "L-01",
            interruptedClosing && window.IsVisible && window.CanNavigatePages
            && launcherContent.Opacity == 1 && contentScale.ScaleX == 1 && contentScale.ScaleY == 1,
            "Sau khi ngắt đóng ở 120ms, cửa sổ hiển thị, chuyển trang được, Opacity/Scale nội dung là 1",
            $"Đã ngắt đóng {interruptedClosing}; Cửa sổ {window.IsVisible}; Chuyển trang được {window.CanNavigatePages}; Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
        window.Hide();
        window.ShowWithTransition(live);
        PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        if (window.PrimaryDisplaySession is { } session)
        {
            var originalDisplay = session.Display;
            var originalPanel = (PixelRect)Field("_panelPixelBounds");
            try
            {
                var changedDpi = originalDisplay.Dpi == 144 ? 192u : 144u;
                var dpiRefreshed = session.TryRefreshPrimary(originalDisplay with { Dpi = changedDpi });
                window.RefreshDisplayPlacement();
                var refreshedBounds = window.Clip.Bounds;
                window.CloseWithTransition();
                var closingBounds = window.Clip.Bounds;
                Check("Sau khi DPI của cùng vùng làm việc vật lý thay đổi, khi đóng không tái sử dụng ánh xạ kích thước lỗi thời", "L-01",
                    dpiRefreshed && (PixelRect)Field("_panelPixelBounds") == originalPanel
                    && (!window.IsVisible || Math.Abs(closingBounds.Width - refreshedBounds.Width) < .1
                        && Math.Abs(closingBounds.Height - refreshedBounds.Height) < .1),
                    "Bảng vật lý không đổi, khi đóng ẩn an toàn hoặc kích thước khả kiến khung đầu tiên không bị nhảy",
                    $"DPI {originalDisplay.Dpi}->{changedDpi}; Hiển thị {window.IsVisible}; Kích thước sau làm mới {refreshedBounds.Width:0.00}x{refreshedBounds.Height:0.00}; Khung đầu khi đóng {closingBounds.Width:0.00}x{closingBounds.Height:0.00}");
            }
            finally
            {
                session.TryRefreshPrimary(originalDisplay);
                window.Hide();
                window.RefreshDisplayPlacement();
            }
        }
        window.ContainerAnchorProvider = null;
        window.Hide();
        window.ShowWithTransition(null);
        Pump(TimeSpan.FromMilliseconds(100));

        object Field(string name) => typeof(LauncherWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string name, params object[] args) => typeof(LauncherWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,args);
        static bool Near(Rect a, Rect b) => Math.Abs(a.X-b.X) < .1 && Math.Abs(a.Y-b.Y) < .1 && Math.Abs(a.Width-b.Width) < .1 && Math.Abs(a.Height-b.Height) < .1;

        void VerifySettlingPixels()
        {
            // A live desktop may advance while its older CPU optical frame still
            // matches the final shape. Settling must keep painting the current
            // transition texture, rather than replaying that older desktop frame.
            var names = new[] { "_transitionBackdrop", "_transitionBackdropBounds", "_frame",
                "_sampledPanelBounds", "_sampledGlassPanelBounds", "_sampledGlassPanelRadius", "_frameUsesAndroidGlass" };
            var fields = names.Select(name => typeof(LiquidGlassSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!).ToArray();
            var saved = fields.Select(field => field.GetValue(surface)).ToArray();
            try
            {
                Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
                window.UpdateLayout();
                var finalBounds = (PixelRect)Field("_panelPixelBounds");
                var finalHost = (PixelRect)Field("_transformHostBounds");
                var finalDip = (double)Field("_panelDipPerPixel");
                var panel = new Rect((finalBounds.X - finalHost.X) * finalDip,
                    (finalBounds.Y - finalHost.Y) * finalDip, finalBounds.Width * finalDip, finalBounds.Height * finalDip);
                var sample = new Rect(surface.RenderSize);
                var current = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 200, 80, 30, 255 }, 4);
                current.Freeze();
                var previous = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                previous.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 30, 80, 200, 255 }, 4, 0);
                object[] values = [current, sample, previous, sample, panel, window.PanelCornerRadius.TopLeft, true];
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(surface, values[i]);
                surface.SetPanelShape(panel, window.PanelCornerRadius.TopLeft);
                var before = ReadCenter("settling-current-backdrop.png");
                Invoke("CompleteContainerTransform", 1d);
                var after = ReadCenter("settling-after-backdrop.png");
                if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
                    Check("Khi mở rộng ổn định không phát lại khung hình màn hình nền cũ", "L-01",
                        before.SequenceEqual(after) && after[0] > after[2],
                        $"Giữ nền xanh dương hiện tại BGRA={string.Join(',', before)}", $"Khi ổn định BGRA={string.Join(',', after)}");

                byte[] ReadCenter(string file)
                {
                    surface.InvalidateVisual();
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)Math.Ceiling(sample.Width), (int)Math.Ceiling(sample.Height), 96, 96, PixelFormats.Pbgra32);
                    image.Render(surface);
                    var result = new byte[4];
                    image.CopyPixels(new Int32Rect((int)(panel.X + panel.Width / 2), (int)(panel.Y + panel.Height / 2), 1, 1), result, 4, 0);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(evidence, file));
                    encoder.Save(output);
                    return result;
                }
            }
            finally
            {
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(surface, saved[i]);
                surface.SetPanelShape((Rect)Field("_transitionPanelBoundsDip"), window.PanelCornerRadius.TopLeft);
            }
        }
    }
}
