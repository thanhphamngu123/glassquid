using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock.Tests;

public sealed class GlassPointerHighlightTests
{
    [Fact]
    public void SearchRecentPopupStillOpensAcceptsTextClearsAndCloses()
    {
        StaTest.Run(() =>
        {
            var dock = new SearchDockWindow();
            ThemeService.Initialize(dock.Dispatcher);
            try
            {
                dock.Show(); dock.UpdateLayout();
                dock.ToggleRecentSearchFlyout();
                var popup = Assert.Single(dock.OwnedWindows.OfType<RecentSearchFlyoutWindow>());
                Assert.True(popup.IsVisible);
                var surface = Assert.IsType<LiquidGlassSurface>(popup.FindName("GlassSurface"));
                Assert.NotNull(surface.PointerHighlight);
                Assert.False(surface.IsHitTestVisible);
                var input = Assert.IsType<TextBox>(popup.FindName("SearchInputBox"));
                input.Text = "Glassquid";
                Assert.Equal("Glassquid", input.Text);
                Assert.IsType<Button>(popup.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(string.Empty, input.Text);
                dock.ToggleRecentSearchFlyout();
                Assert.False(popup.IsVisible);
            }
            finally { dock.Close(); ThemeService.Current?.Dispose(); }
        });
    }

    [Fact]
    public void MouseBurstIsCoalescedWithoutDrawingOrChangingControls()
    {
        StaTest.Run(() => WithSurface((host, surface, button, light) =>
        {
            var before = (host.RenderSize, surface.RenderSize, button.RenderSize, button.Margin,
                button.Padding, button.Background, button.FontSize, button.Content, Keyboard.FocusedElement);
            var frames = light.DrawCount;
            for (var i = 0; i < 2000; i++) light.MoveTo(new Point(30 + i % 200, 40));
            Assert.Equal(frames, light.DrawCount);
            Assert.True(light.IsTimerRunning);
            light.AdvanceFrame(1d / 60);
            Assert.Equal(frames + 1, light.DrawCount);
            Assert.False(surface.IsHitTestVisible);
            Assert.Equal(before, (host.RenderSize, surface.RenderSize, button.RenderSize, button.Margin,
                button.Padding, button.Background, button.FontSize, button.Content, Keyboard.FocusedElement));
            Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(surface, 2));
            var clicked = false;
            button.Click += (_, _) => clicked = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(clicked);
        }));
    }

    [Fact]
    public void LightFollowsPointerAndLeaveRestoresExactlyTransparentPixels()
    {
        StaTest.Run(() => WithSurface((host, surface, button, light) =>
        {
            var visual = (Visual)VisualTreeHelper.GetChild(surface, 2);
            var baseline = Pixels(visual, 320, 120);
            Assert.All(baseline, value => Assert.Equal((byte)0, value));
            light.MoveTo(new Point(30, 30));
            for (var i = 0; i < 12; i++) light.AdvanceFrame(1d / 60);
            var left = Pixels(visual, 320, 120);
            Assert.True(AlphaAt(left, 320, 35, 30) > AlphaAt(left, 320, 280, 30));
            light.MoveTo(new Point(285, 30));
            for (var i = 0; i < 24; i++) light.AdvanceFrame(1d / 60);
            var right = Pixels(visual, 320, 120);
            Assert.InRange(light.Position.X, 284.9, 285.1);
            Assert.True(AlphaAt(right, 320, 280, 30) > AlphaAt(right, 320, 35, 30));
            light.Leave();
            light.AdvanceFrame(1d / 60);
            Assert.InRange(light.Strength, 0.8, .99);
            for (var i = 0; i < 20; i++) light.AdvanceFrame(1d / 60);
            Assert.Equal(0, light.Strength);
            Assert.False(light.IsTimerRunning);
            Assert.Equal(baseline, Pixels(visual, 320, 120));
        }));
    }

    [Fact]
    public void OutlineIsClippedAndLightDoesNotRedrawAtRest()
    {
        StaTest.Run(() => WithSurface((host, surface, button, light) =>
        {
            light.MoveTo(new Point(160, 8));
            for (var i = 0; i < 20; i++) light.AdvanceFrame(1d / 60);
            var visual = (Visual)VisualTreeHelper.GetChild(surface, 2);
            var pixels = Pixels(visual, 320, 120);
            Assert.Equal(0, AlphaAt(pixels, 320, 0, 0));
            Assert.True(AlphaAt(pixels, 320, 160, 0) > AlphaAt(pixels, 320, 160, 25));
            var count = light.DrawCount;
            for (var i = 0; i < 60; i++) light.AdvanceFrame(1d / 60);
            Assert.Equal(count, light.DrawCount);
        }));
    }

    [Fact]
    public void HideSleepFullscreenAndUnloadStopAndClearTheEffect()
    {
        StaTest.Run(() => WithSurface((host, surface, button, light) =>
        {
            void Illuminate() { light.MoveTo(new Point(100, 30)); light.AdvanceFrame(.05); Assert.True(light.Strength > 0); }
            void AssertOff() { Assert.Equal(0, light.Strength); Assert.False(light.IsTimerRunning); }
            Illuminate();
            surface.SetPerformanceSleeping(true);
            AssertOff();
            surface.SetPerformanceSleeping(false);
            Illuminate();
            light.IsFullscreen = () => true;
            light.AdvanceFrame(.016);
            AssertOff();
            light.IsFullscreen = () => false;
            Illuminate();
            host.Hide();
            AssertOff();
            host.Show();
            Illuminate();
            surface.Dispose();
            AssertOff();
            host.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
            AssertOff();
        }));
    }

    [Fact]
    public void ThrottledFramesDoNotStarveInputAndFadeCompletesWithoutFurtherMouseEvents()
    {
        StaTest.Run(() => WithSurface((host, surface, button, light) =>
        {
            light.MoveTo(new Point(160, 30));
            light.AdvanceFrame(.1);
            light.Leave();
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var inputCount = 0;
            var stop = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(420) };
            stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
            void Input()
            {
                inputCount++;
                if (elapsed.ElapsedMilliseconds < 300)
                    host.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)Input);
            }
            host.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)Input);
            stop.Start();
            Dispatcher.PushFrame(frame);
            Assert.True(inputCount > 100);
            Assert.Equal(0, light.Strength);
            Assert.False(light.IsTimerRunning);
            Assert.InRange(light.DrawCount, 2, 30);
        }));
    }

    private static void WithSurface(Action<Window, LiquidGlassSurface, Button, GlassPointerHighlight> test)
    {
        var surface = new LiquidGlassSurface { ReadBackdropScene = () => null };
        var button = new Button { Content = "Original", Width = 90, Height = 35, Margin = new Thickness(7), Padding = new Thickness(4) };
        var grid = new Grid(); grid.Children.Add(surface); grid.Children.Add(button);
        var host = new TestGlassWindow { Width = 320, Height = 120, Content = grid, ShowInTaskbar = false, ShowActivated = false };
        ThemeService.Initialize(host.Dispatcher);
        try
        {
            host.Show(); host.UpdateLayout();
            host.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var light = Assert.IsType<GlassPointerHighlight>(surface.PointerHighlight);
            light.IsFullscreen = () => false;
            Assert.True(surface.CanShowPointerHighlight);
            test(host, surface, button, light);
        }
        finally { host.Close(); ThemeService.Current?.Dispose(); }
    }

    private sealed class TestGlassWindow : GlassWindow;

    private static byte[] Pixels(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte AlphaAt(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];
}
