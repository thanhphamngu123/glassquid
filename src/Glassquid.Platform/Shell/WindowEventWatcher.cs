using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Nguồn sự kiện khi cửa sổ xuất hiện / biến mất / thay đổi khả năng hiển thị / đổi cửa sổ tiền cảnh.
/// </summary>
/// <remarks>
/// Nguồn tín hiệu cho trạng thái đang chạy (D-07), cấu trúc đồng hình với <see cref="FullscreenWatcher"/>:
/// hook được gắn trên một luồng có vòng lặp thông điệp riêng, hàm gọi lại <strong>chỉ đưa vào hàng đợi</strong> —
/// gộp việc tính toán lại vào một bộ hẹn giờ một lần 120 mili giây, toàn bộ việc liệt kê cửa sổ thực sự và mọi
/// cập nhật giao diện đều không diễn ra trong hàm gọi lại của hook. Việc gọi lại lồng nhau của WinEvent và "luồng gọi lại phải bơm thông điệp"
/// là hai cái bẫy được tài liệu chính thức nhắc rõ; đưa vào hàng đợi giúp ngắt cái trước, luồng riêng giải quyết cái sau.
///
/// <para>
/// Nhóm sự kiện theo dõi (Đầu dò B đã đo thực tế phạm vi bao phủ, xem <c>docs/evidence/giai-doan-4-ket-qua-tham-do.txt</c>):
/// OBJECT_CREATE / OBJECT_DESTROY (sinh tử của cửa sổ), OBJECT_SHOW / OBJECT_HIDE (khả năng hiển thị),
/// SYSTEM_MINIMIZESTART / SYSTEM_MINIMIZEEND (thu nhỏ), SYSTEM_FOREGROUND (đổi cửa sổ tiền cảnh).
/// Sự kiện chỉ là <strong>tín hiệu kích hoạt</strong>: sau khi nhận thông báo, bên gọi phải liệt kê lại tập cửa sổ
/// (<see cref="WindowLocator.EnumerateTopLevelWindows"/>) và lấy kết quả liệt kê làm chuẩn,
/// chứ không đoán trạng thái theo ngữ nghĩa sự kiện — chỉ dựa vào CREATE/DESTROY không thể bao phủ
/// hai trường hợp "cửa sổ đã tồn tại trước khi Dock khởi động" và "ẩn đi rồi hiển thị lại".
/// </para>
///
/// <para>
/// Cố ý <strong>không thêm</strong> <c>WINEVENT_SKIPOWNPROCESS</c>: các phần tử bật lên của chính sản phẩm này
/// khi xuất hiện/biến mất cũng cần thúc đẩy cập nhật trạng thái, cùng lý do như <see cref="FullscreenWatcher"/>.
/// </para>
/// </remarks>
public sealed class WindowEventWatcher : IDisposable
{
    /// <summary>Cửa sổ gộp sự kiện (mili giây). Cùng bậc độ lớn với giám sát toàn màn hình: không cảm nhận được độ trễ phán đoán, nhưng làm phẳng được bão sự kiện.</summary>
    private const int CoalesceMilliseconds = 120;

    private static readonly uint[] WatchedEvents =
    [
        User32.EventObjectCreate,
        User32.EventObjectDestroy,
        User32.EventObjectShow,
        User32.EventObjectHide,
        User32.EventSystemMinimizeStart,
        User32.EventSystemMinimizeEnd,
        User32.EventSystemForeground,
    ];

    private readonly Action? _onChanged;
    private readonly Action<uint, nint, int, int>? _onRawEvent;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _hookThread;
    private readonly List<nint> _hooks = [];
    private readonly Lock _sync = new();

    /// <summary>Ủy quyền gọi lại phải được giữ bởi trường: ủy quyền bị thu gom rác sẽ khiến phía native gọi vào stub đã giải phóng.</summary>
    private readonly User32.WinEventProc _callback;

    private Timer? _coalesceTimer;
    private uint _threadId;
    private bool _hooksInstalled;
    private volatile bool _disposed;

    private WindowEventWatcher(Action? onChanged, Action<uint, nint, int, int>? onRawEvent)
    {
        _onChanged = onChanged;
        _onRawEvent = onRawEvent;
        _callback = OnWinEvent;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "Nguồn sự kiện cửa sổ Glassquid",
        };

        _coalesceTimer = new Timer(OnCoalesceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>ID cấp hệ thống của luồng chứa hook. Dùng để chẩn đoán.</summary>
    public uint HookThreadId => _threadId;

    /// <summary>
    /// Khởi động nguồn sự kiện. Trả về null khi không gắn được hook — bên gọi nên lùi về liệt kê theo nhu cầu,
    /// thay vì coi "không gắn được hook" là lỗi.
    /// </summary>
    /// <param name="onChanged">Thông báo "có thay đổi" sau khi gộp, kích hoạt trên luồng thread pool.</param>
    /// <param name="onRawEvent">
    /// Nhánh rẽ chẩn đoán: gọi lại trực tiếp cho mỗi sự kiện thô
    /// (loại sự kiện, handle cửa sổ, OBJID, id con). Kích hoạt trên <strong>luồng hook</strong>,
    /// phần cài đặt phải thật nhẹ (cỡ ghi một dòng nhật ký), tuyệt đối không được truy vấn hay đụng tới giao diện tại đây.
    /// </param>
    public static WindowEventWatcher? TryStart(Action? onChanged, Action<uint, nint, int, int>? onRawEvent = null)
    {
        var watcher = new WindowEventWatcher(onChanged, onRawEvent);

        if (watcher.Start())
        {
            return watcher;
        }

        watcher.Dispose();
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _coalesceTimer?.Dispose();
        _coalesceTimer = null;

        if (_hookThread.IsAlive)
        {
            // Cho vòng lặp thông điệp thoát ra. Việc gỡ hook phải diễn ra trên chính luồng của hook:
            // khi luồng khác gỡ, các sự kiện đã xếp hàng nhưng chưa phân phát sẽ rơi vào đối tượng đã bị giải phóng.
            if (_threadId != 0)
            {
                try
                {
                    _ = User32.PostThreadMessage(_threadId, User32.WmQuit, 0, 0);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // Không gỡ được hook, nhưng luồng là luồng nền nên sẽ tự kết thúc khi tiến trình thoát.
                }
            }

            _hookThread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    private bool Start()
    {
        _hookThread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        return _hooksInstalled;
    }

    private void HookThreadMain()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // Ép tạo hàng đợi thông điệp của luồng này trước: PostThreadMessage yêu cầu luồng đích phải có sẵn hàng đợi.
            _ = User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);

            foreach (var eventType in WatchedEvents)
            {
                var hook = User32.SetWinEventHook(
                    eventType,
                    eventType,
                    0,
                    _callback,
                    0,
                    0,
                    User32.WineventOutOfContext);

                if (hook != 0)
                {
                    _hooks.Add(hook);
                }
            }

            _hooksInstalled = _hooks.Count > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Không gắn được hook chỉ có nghĩa là "nguồn sự kiện không khả dụng", tuyệt đối không để cả luồng chết cùng
            // ngoại lệ chưa xử lý — điều đó sẽ làm chấm dứt ngay tiến trình.
            _hooksInstalled = false;
        }
        finally
        {
            _ready.Set();
        }

        if (!_hooksInstalled)
        {
            return;
        }

        try
        {
            while (User32.GetMessage(out var message, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(in message);
                User32.DispatchMessage(in message);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Tương tự trên: chỉ làm việc giám sát mất hiệu lực.
        }

        foreach (var hook in _hooks)
        {
            User32.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    /// <summary>
    /// Hàm gọi lại sự kiện. Chạy trên luồng hook, **tuyệt đối không để ngoại lệ lọt ngược về ngăn xếp native** — điều đó sẽ làm chấm dứt ngay tiến trình.
    /// </summary>
    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed)
        {
            return;
        }

        if (objectId != User32.ObjIdWindow || childId != 0 || window == 0)
        {
            return;
        }

        // Nhánh rẽ chẩn đoán được gọi trực tiếp trên luồng hook — giao ước với bên gọi yêu cầu nó phải nhẹ.
        try
        {
            _onRawEvent?.Invoke(eventType, window, objectId, childId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }

        ScheduleReconcile();
    }

    private void ScheduleReconcile()
    {
        var timer = _coalesceTimer;

        if (timer is null)
        {
            return;
        }

        try
        {
            // Chỉ hẹn một lần; các sự kiện đến lặp lại trong thời gian đó chỉ đẩy thời điểm này lùi về sau.
            timer.Change(CoalesceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Đụng độ với Dispose. Bỏ qua là được.
        }
    }

    private void OnCoalesceElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _onChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lỗi tính toán lại của bên gọi không làm sập chính nguồn sự kiện.
            _ = ex;
        }
    }
}
