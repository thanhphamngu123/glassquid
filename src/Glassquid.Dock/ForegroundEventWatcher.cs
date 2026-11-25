using System.Runtime.InteropServices;

namespace Glassquid.Dock;

internal static class ForegroundEventWatcher
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool UnhookWinEvent(nint hook);

    private static readonly WinEventProc Callback = OnWinEvent;
    private static nint _hook;
    private static int _subscribers;
    private static readonly object Gate = new();

    public static event Action<nint>? ForegroundChanged;

    public static void Subscribe(Action<nint> handler)
    {
        lock (Gate)
        {
            ForegroundChanged += handler;
            _subscribers++;
            if (_hook == 0)
            {
                _hook = SetWinEventHook(
                    EventSystemForeground,
                    EventSystemForeground,
                    0,
                    Callback,
                    0,
                    0,
                    WinEventOutOfContext);
            }
        }
    }

    public static void Unsubscribe(Action<nint> handler)
    {
        lock (Gate)
        {
            ForegroundChanged -= handler;
            _subscribers--;
            if (_subscribers <= 0 && _hook != 0)
            {
                UnhookWinEvent(_hook);
                _hook = 0;
            }
        }
    }

    private static void OnWinEvent(nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType != EventSystemForeground || hwnd == 0) return;
        try
        {
            ForegroundChanged?.Invoke(hwnd);
        }
        catch { }
    }
}
