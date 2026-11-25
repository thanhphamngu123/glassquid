using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyDragMotionLoad(LauncherWindow window, UniformGrid grid, string evidence)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", flags)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", flags)!;
        var move = typeof(LauncherWindow).GetMethod("MoveIconToOffset", flags)!
            .CreateDelegate<Action<Button, double, double>>(window);
        var tiles = grid.Children.Cast<Button>().ToArray();
        long allocated = 0;
        double milliseconds = 0;
        try
        {
            prepare.Invoke(window, [tiles[0], new Point(20, 20)]);
            Pump(TimeSpan.FromMilliseconds(80));
            // Warm up every real tile before measuring repeated whole-page reversals.
            foreach (var tile in tiles) { move(tile, 100, 80); move(tile, -100, -80); }
            var startedBytes = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < 200; i++)
                foreach (var tile in tiles)
                    move(tile, i % 2 == 0 ? 100 : -100, i % 2 == 0 ? 80 : -80);
            milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - startedBytes;
            Check("7000 lần đổi đích đảo chiều trên biểu tượng thật dùng lại trạng thái chuyển động, cấp phát nhỏ hơn 1 MiB", "regression/drag-motion-load",
                allocated < 1024 * 1024, "<1048576 bytes", allocated.ToString());
            // Retargeting must not teleport the visible tile, and the common frame
            // driver must keep moving even when the pointer no longer changes slots.
            var tile7 = tiles[7];
            var before = tile7.TranslatePoint(new Point(0, 0), grid);
            move(tile7, 90, 60);
            var after = tile7.TranslatePoint(new Point(0, 0), grid);
            Check("Cập nhật đảo chiều hàng loạt không bị nhảy vị trí", "regression/drag-motion-load", (after - before).Length < 1,
                "<1 DIP", (after - before).Length.ToString());
            Pump(TimeSpan.FromMilliseconds(95));
            var middle = tile7.TranslatePoint(new Point(0, 0), grid);
            Check("Tiếp tục di chuyển nhường chỗ khi không có đích mới", "regression/drag-motion-load", (middle - after).Length > 1,
                ">1 DIP", (middle - after).Length.ToString());
            Pump(TimeSpan.FromMilliseconds(240));
            var motion = (TranslateTransform)tile7.RenderTransform;
            Check("Vào đúng vị trí sau khi nhường chỗ đồng bộ hoàn tất", "regression/drag-motion-load",
                Math.Abs(motion.X - 90) < .1 && Math.Abs(motion.Y - 60) < .1,
                "90,60", $"{motion.X},{motion.Y}");
        }
        finally { finish.Invoke(window, null); }
        File.WriteAllText(Path.Combine(evidence, "drag-motion-load.json"), JsonSerializer.Serialize(new
        { updates = 7000, allocatedBytes = allocated, milliseconds, note = "Real retarget CPU/allocation load; not displayed FPS." }));
    }
}
