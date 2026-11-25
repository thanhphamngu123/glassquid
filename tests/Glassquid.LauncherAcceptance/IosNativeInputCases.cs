using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyIosNativeInput(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        window.Activate();
        Check("Phím bổ trợ đang nhả khi bắt đầu kiểm thử nhập liệu thật", "IOS-INPUT", System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None,
            "Không có phím bổ trợ", System.Windows.Input.Keyboard.Modifiers.ToString());
        var canvas = (Canvas)window.FindName("DragOverlay")!;
        var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var timers = new List<DispatcherTimer>();
        var cellPoints = Enumerable.Range(0, 35).Select(index => grid.PointToScreen(new Point(
            grid.ActualWidth / 7 * ((index % 7) + .5), grid.ActualHeight / 5 * ((index / 7) + .5)))).ToArray();
        var sortingPoints = Enumerable.Range(0, 35).Select(index => grid.PointToScreen(new Point(
            grid.ActualWidth / 7 * ((index % 7) + .5), grid.ActualHeight / 5 * ((index / 7) + .85)))).ToArray();
        void At(int ms, Action action)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timers.Add(timer);
            timer.Start();
        }
        void MoveToCell(int index)
        {
            var p = cellPoints[index];
            IosInput.SetCursorPos((int)p.X, (int)p.Y);
        }
        void MoveToSortingArea(int index)
        {
            // Convert the complete DIP position, including the edge offset, to
            // physical pixels. Mixing a DIP offset into screen pixels lands in
            // the folder center instead on a scaled Windows desktop.
            var p = sortingPoints[index];
            IosInput.SetCursorPos((int)p.X, (int)p.Y);
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        var hotkeys = 0;
        HwndSourceHook hook = (nint _, int message, nint wParam, nint _, ref bool handled) =>
        {
            if (message == 0x0312 && wParam.ToInt32() == 0x7717)
            { hotkeys++; window.NavigatePage(1); handled = true; }
            return 0;
        };
        source.AddHook(hook);
        var registered = IosInput.RegisterHotKey(hwnd, 0x7717, 0x4000 | 2 | 4, 0x7e); // Ctrl+Shift+F15
        Check("Đăng ký phím tắt kiểm thử kéo thả", "IOS-INPUT", registered, "Đăng ký thành công", registered.ToString());
        var done = false;
        try
        {
            MoveToCell(0);
            IosInput.Mouse(2);
            var inputScript = Task.Run(async () =>
            {
                await Task.Delay(550); MoveToSortingArea(3);
                await Task.Delay(550); // Include the sorting dwell and its settle animation.
                IosInput.Key(0x11, false); IosInput.Key(0x10, false); IosInput.Key(0x7e, false);
                IosInput.Key(0x7e, true); IosInput.Key(0x10, true); IosInput.Key(0x11, true);
                await Task.Delay(400); IosInput.Key(0x12, false); MoveToCell(0);
                await Task.Delay(450); IosInput.Mouse(4);
                await Task.Delay(250); IosInput.Key(0x12, true);
            });
            At(200, () => Check("Giai đoạn nhấn ngắn không kích hoạt kéo sớm", "IOS-INPUT", canvas.Children.Count == 0, "Chưa nhấc lên", canvas.Children.Count.ToString()));
            At(470, () => Check("Nhấn giữ chuột thật nhấc biểu tượng nổi lên", "IOS-INPUT", canvas.Children.Count == 1, "Một biểu tượng nổi", canvas.Children.Count.ToString()));
            At(1030, () =>
            {
                var visibleOrder = VisualTileOrder(grid);
                Check("Kéo thật dừng ở mép biểu tượng sẽ nhường chỗ theo thời gian thực và không ghi đĩa", "IOS-INPUT", Equals(visibleOrder[3].Tag, "wpf-00")
                    && Equals(visibleOrder[0].Tag, "wpf-01") && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                    "01, 02, 03, 00; tệp không thay đổi", string.Join(",", visibleOrder.Take(4).Select(b => b.Tag)) + "; modifiers=" + System.Windows.Input.Keyboard.Modifiers);
                Capture(window, Path.Combine(evidence, "native-live-insertion.png"));
            });
            At(1650, () =>
            {
                Check("Phím tắt thật chuyển trang và hoán đổi trong khi kéo", "IOS-INPUT", hotkeys == 1 && grid.Children.Count == 1
                    && Equals(((Button)grid.Children[0]).Tag, "wpf-00"), "Trang hai có một mục là biểu tượng đang kéo, phím tắt kích hoạt một lần", $"Phím tắt {hotkeys}; biểu tượng {grid.Children.Count}");
                Capture(window, Path.Combine(evidence, "native-cross-page-preview.png"));
            });
            At(2350, () => done = true);
            PumpUntil(() => done, TimeSpan.FromSeconds(5));
            inputScript.GetAwaiter().GetResult();
            var order = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("Thả chuột thật lưu hoán đổi và khớp biểu tượng vào vị trí", "IOS-INPUT", order[0] == "wpf-35" && order[35] == "wpf-00"
                && canvas.Children.Count == 0 && grid.Children.OfType<Button>().All(b => b.Opacity == 1),
                "35 và 00 hoán đổi, lớp nổi trống", $"Mục đầu {order[0]}; mục cuối {order[35]}; lớp nổi {canvas.Children.Count}");
            Capture(window, Path.Combine(evidence, "native-snapped.png"));

            // Escape must discard the draft while the primary button is still held.
            window.NavigatePage(-1);
            Pump(TimeSpan.FromMilliseconds(300));
            var beforeCancel = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            done = false;
            MoveToCell(0); IosInput.Mouse(2);
            var cancelScript = Task.Run(async () =>
            {
                await Task.Delay(500); MoveToCell(4);
                await Task.Delay(350); IosInput.Key(0x1b, false); IosInput.Key(0x1b, true);
                await Task.Delay(250); IosInput.Mouse(4);
            });
            At(1300, () => done = true);
            PumpUntil(() => done, TimeSpan.FromSeconds(4));
            cancelScript.GetAwaiter().GetResult();
            Check("Nhấn Esc thật hủy kéo và khôi phục sắp xếp", "IOS-INPUT", beforeCancel.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))
                && Equals(((Button)grid.Children[0]).Tag, "wpf-35") && canvas.Children.Count == 0,
                "Khôi phục thứ tự và tệp ban đầu", ((Button)grid.Children[0]).Tag?.ToString() ?? "trống");
        }
        finally
        {
            foreach (var timer in timers) timer.Stop();
            IosInput.Mouse(4);
            foreach (var key in new[] { 0x11, 0x10, 0x12, 0x7e, 0x1b }) IosInput.Key((ushort)key, true);
            if (registered) IosInput.UnregisterHotKey(hwnd, 0x7717);
            source.RemoveHook(hook);
        }
    }

    private static class IosInput
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort VirtualKey;
            [FieldOffset(12)] public uint KeyFlags;
            [FieldOffset(8)] public int MouseX;
            [FieldOffset(12)] public int MouseY;
            [FieldOffset(20)] public uint MouseFlags;
        }
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
        public static void MovePacket(Point p)
        {
            var x = GetSystemMetrics(76); var y = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            var input = new Input { MouseX = (int)((p.X - x) * 65535 / (width - 1)),
                MouseY = (int)((p.Y - y) * 65535 / (height - 1)), MouseFlags = 0x8000 | 0x4000 | 0x2000 | 1 };
            if (SendInput(1, [input], 40) != 1) throw new InvalidOperationException("SendInput movement packet was rejected.");
        }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
        public static void Mouse(uint flags) => SendInput(1, [new Input { MouseFlags = flags }], 40);
        public static void Key(ushort key, bool up) => SendInput(1, [new Input { Type = 1, VirtualKey = key, KeyFlags = up ? 2u : 0 }], 40);
    }
}
