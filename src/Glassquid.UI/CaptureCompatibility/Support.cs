using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint RemoveMessage = 0x0001;
    private const uint WakeForAllInput = 0x04FF;
    private const uint WakeWhenInputIsAvailable = 0x0004;
    private const uint PeekWithoutRemoving = 0x0000;

    internal static void Pump(TimeSpan duration)
    {
        // Capture runs on a dedicated STA thread. Pump its Win32/COM message queue
        // directly instead of constructing a WPF DispatcherFrame and DispatcherTimer
        // on every wait; the helper process does not otherwise need WPF's dispatcher.
        var timeoutTicks = Math.Max(0, (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
        var deadline = Stopwatch.GetTimestamp() + timeoutTicks;

        // Force creation of this thread's message queue before waiting for input.
        _ = PeekMessage(out _, 0, 0, 0, PeekWithoutRemoving);
        while (true)
        {
            DispatchPendingMessages();
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = (uint)Math.Clamp(
                Math.Ceiling(remainingTicks * 1000d / Stopwatch.Frequency), 1, 1000);
            _ = MsgWaitForMultipleObjectsEx(
                0, 0, remainingMilliseconds, WakeForAllInput, WakeWhenInputIsAvailable);
        }
    }

    private static void DispatchPendingMessages()
    {
        while (PeekMessage(out var message, 0, 0, 0, RemoveMessage))
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);
}
