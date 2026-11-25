using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Theo dõi hướng sự kiện trạng thái "hiện có đang toàn màn hình hay không", dùng để chặn kích hoạt.
/// </summary>
/// <remarks>
/// <para>
/// Lý do tồn tại là một ràng buộc cứng: **không dùng polling**. Trong báo cáo cơ sở, bản tham chiếu khởi chạy
/// PowerShell mỗi 2500 mili-giây để hỏi cùng một việc, đó là một trong những nguồn tiêu tốn CPU thường trực chính.
/// Bản cài đặt này chuyển sang lắng nghe hai sự kiện <c>EVENT_SYSTEM_FOREGROUND</c> và <c>EVENT_OBJECT_LOCATIONCHANGE</c>,
/// chỉ tính lại phán đoán một lần khi có sự kiện đến.
/// </para>
/// <para>
/// Không thể thiếu sự kiện nào: <c>EVENT_SYSTEM_FOREGROUND</c> chỉ phát sinh khi cửa sổ tiền cảnh **đổi sang cửa sổ khác**,
/// còn việc trình duyệt nhấn F11 hay trình phát chuyển toàn màn hình đều là cùng một cửa sổ tự thay đổi kích thước, sẽ không kích hoạt sự kiện đó.
/// </para>
/// <para>
/// Hook được cài trên một luồng riêng; luồng đó chỉ chạy một vòng lặp thông điệp và không tham chiếu WPF,
/// nhờ vậy kiểu này có thể giữ trong <c>Glassquid.Platform</c> (dự án này chủ đích không tham chiếu WPF).
/// Callback kích hoạt trên luồng hook, nên bên gọi cần cập nhật giao diện phải tự chuyển về luồng của mình.
/// </para>
/// <para>
/// Gộp sự kiện: khi kéo cửa sổ, sự kiện thay đổi vị trí có thể lên tới hàng trăm lần mỗi giây. Việc tính lại được gộp vào một độ trễ 120 mili-giây,
/// nghĩa là phán đoán trễ tối đa 120 mili-giây. Bộ định thời này **chỉ được lên lịch bởi sự kiện và chỉ kích hoạt một lần**, không phải polling định kỳ.
/// </para>
/// <para>
/// Chủ đích **không thêm** <c>WINEVENT_SKIPOWNPROCESS</c>: khi cửa sổ của chính sản phẩm này trở thành cửa sổ tiền cảnh,
/// kết luận phán đoán cũng sẽ chuyển từ "toàn màn hình" về "không toàn màn hình" (cửa sổ của chính nó được loại trừ), sự kiện đó cũng bắt buộc phải nhận được,
/// nếu không phán đoán sẽ kẹt ở giá trị cũ và cơ chế chặn sẽ không bao giờ được gỡ bỏ. Phần lớn sự kiện từ cửa sổ của chính nó được chặn lại bởi
/// kiểm tra "có phải cửa sổ tiền cảnh không" bên trong callback.
/// </para>
/// </remarks>
public sealed class FullscreenWatcher : IDisposable
{
    /// <summary>Cửa sổ gộp sự kiện (mili-giây). Bão sự kiện khi kéo cửa sổ được nén thành một lần tính lại.</summary>
    private const int CoalesceMilliseconds = 120;

    /// <summary>Giới hạn thời gian chờ luồng hook báo cáo kết quả cài đặt.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Giới hạn thời gian chờ luồng hook thoát.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly FullscreenOptions _options;
    private readonly Action<bool>? _onChanged;
    private readonly Action? _onStateInvalidated;
    private readonly Func<long>? _getStateGeneration;
    private readonly Action<long, bool>? _onStateRefreshed;
    private readonly Action<PerformanceFullscreenObservation>? _onPerformanceObservation;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _hookThread;
    private readonly List<nint> _hooks = [];
    private readonly Lock _sync = new();

    /// <summary>Delegate callback phải được giữ bởi một trường: delegate bị thu gom rác sẽ khiến phía native gọi vào stub đã giải phóng.</summary>
    private readonly User32.WinEventProc _callback;

    private Timer? _coalesceTimer;
    private uint _threadId;
    private volatile bool _hooksInstalled;
    private bool _isFullscreen;
    private volatile bool _disposed;

    private FullscreenWatcher(Action<bool>? onChanged, FullscreenOptions options,
        Action? onStateInvalidated, Func<long>? getStateGeneration,
        Action<long, bool>? onStateRefreshed, Action<PerformanceFullscreenObservation>? onPerformanceObservation)
    {
        _onChanged = onChanged;
        _options = options;
        _onStateInvalidated = onStateInvalidated;
        _getStateGeneration = getStateGeneration;
        _onStateRefreshed = onStateRefreshed;
        _onPerformanceObservation = onPerformanceObservation;
        _callback = OnWinEvent;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "Glassquid Hook sự kiện toàn màn hình",
        };

        // Giá trị ban đầu là "không bao giờ kích hoạt": chỉ được lên lịch một lần khi có sự kiện đến.
        _coalesceTimer = new Timer(OnCoalesceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Phán đoán gần nhất: hiện có ứng dụng toàn màn hình nào ở tiền cảnh không.</summary>
    public bool IsFullscreen
    {
        get
        {
            lock (_sync)
            {
                return _isFullscreen;
            }
        }
    }

    /// <summary>Chỉ trả về true khi cả hai hook sự kiện và luồng thông điệp đều vẫn khả dụng.</summary>
    public bool IsAvailable => !_disposed && _hooksInstalled && _hookThread.IsAlive;

    /// <summary>
    /// Khởi động trình theo dõi. Trả về null nếu không cài được bất kỳ hook sự kiện nào; các tính năng đầu vào thụ động yêu cầu chặn liên tục
    /// nên từ chối kích hoạt, còn các tính năng vẫn có thể truy vấn độc lập theo nhu cầu có thể tự truy vấn.
    /// </summary>
    /// <param name="onFullscreenChanged">
    /// Callback khi kết quả phán đoán thay đổi, tham số là giá trị phán đoán mới. Được kích hoạt trên luồng hook.
    /// </param>
    public static FullscreenWatcher? TryStart(Action<bool>? onFullscreenChanged = null,
        FullscreenOptions? options = null, Action? onStateInvalidated = null,
        Func<long>? getStateGeneration = null, Action<long, bool>? onStateRefreshed = null,
        Action<PerformanceFullscreenObservation>? onPerformanceObservation = null)
    {
        var watcher = new FullscreenWatcher(onFullscreenChanged, options ?? FullscreenOptions.Default,
            onStateInvalidated, getStateGeneration, onStateRefreshed, onPerformanceObservation);

        if (watcher.Start())
        {
            return watcher;
        }

        watcher.Dispose();
        return null;
    }

    /// <summary>
    /// Tính lại ngay lập tức một lần và trả về phán đoán mới nhất.
    /// </summary>
    /// <remarks>
    /// Để bên gọi xác nhận lần cuối trước khi thực sự gọi Dock hiện ra, hoặc làm mới theo nhu cầu khi không quan tâm đến sự kiện.
    /// </remarks>
    public bool Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Recompute();
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
            // Cho vòng lặp thông điệp thoát. Việc gỡ hook phải diễn ra trên chính luồng của hook:
            // nếu gỡ từ luồng khác, các sự kiện đã xếp hàng nhưng chưa được phân phối sẽ rơi vào đối tượng đã giải phóng.
            if (_threadId != 0)
            {
                try
                {
                    User32.PostThreadMessage(_threadId, User32.WmQuit, 0, 0);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // Không thể gỡ hook, nhưng luồng này là luồng nền nên sẽ tự kết thúc khi tiến trình thoát.
                }
            }

            _hookThread.Join(ShutdownTimeout);
        }

        _ready.Dispose();
    }

    private bool Start()
    {
        _hookThread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            return false;
        }

        if (!_hooksInstalled)
        {
            return false;
        }

        // Phán đoán ban đầu phải được tính sau khi cài xong hook, nếu không những thay đổi trạng thái
        // xảy ra giữa lúc "cài hook" và "sự kiện đầu tiên" sẽ bị bỏ sót.
        Recompute();
        return true;
    }

    private void HookThreadMain()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // Ép tạo hàng đợi thông điệp của luồng này trước: PostThreadMessage yêu cầu luồng đích đã có hàng đợi,
            // mà hàng đợi chỉ được tạo ở lần gọi PeekMessage/GetMessage đầu tiên.
            User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);

            var foregroundHook = User32.SetWinEventHook(
                User32.EventSystemForeground,
                User32.EventSystemForeground,
                0,
                _callback,
                0,
                0,
                User32.WineventOutOfContext);
            if (foregroundHook != 0) _hooks.Add(foregroundHook);

            var locationHook = User32.SetWinEventHook(
                User32.EventObjectLocationChange,
                User32.EventObjectLocationChange,
                0,
                _callback,
                0,
                0,
                User32.WineventOutOfContext);
            if (locationHook != 0) _hooks.Add(locationHook);
            _hooksInstalled = foregroundHook != 0 && locationHook != 0;
        }
        catch (Exception)
        {
            // Không tuyên bố trình theo dõi khả dụng khi API không có sẵn hoặc khởi tạo thất bại. Bên gọi khởi động dựa vào đây để từ chối
            // các thiết lập đầu vào thụ động phụ thuộc vào cơ chế chặn toàn màn hình.
            _hooksInstalled = false;
        }
        finally
        {
            // Dù thành công hay thất bại đều phải mở khóa Start, nếu không nó sẽ chờ đến hết thời gian chờ.
            _ready.Set();
        }

        if (_hooksInstalled)
        {
            try
            {
                var result = 1;
                try
                {
                    while ((result = User32.GetMessage(out var message, 0, 0, 0)) > 0)
                    {
                        User32.TranslateMessage(in message);
                        User32.DispatchMessage(in message);
                    }
                }
                catch (Exception)
                {
                    // Mọi ngoại lệ runtime trong vòng lặp thông điệp đều làm mất hiệu lực bộ đệm, không thể tiếp tục dùng giá trị cũ.
                    result = -1;
                }

                if (result <= 0 && !_disposed) MarkMonitorUnavailable();
            }
            catch (Exception)
            {
                if (!_disposed) MarkMonitorUnavailable();
            }
        }

        _hooksInstalled = false;
        foreach (var hook in _hooks)
        {
            try { User32.UnhookWinEvent(hook); }
            catch (Exception) { /* Luồng đã thoát; bộ đệm giữ trạng thái không khả dụng. */ }
        }

        _hooks.Clear();
    }

    /// <summary>
    /// Callback sự kiện. Chạy trên luồng hook, **tuyệt đối không để ngoại lệ thoát ngược về stack native** — điều đó sẽ làm chấm dứt tiến trình ngay lập tức.
    /// </summary>
    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_hooksInstalled) return;

            if (eventType == User32.EventObjectLocationChange)
            {
                // Chỉ quan tâm đến bản thân cửa sổ cấp cao nhất và chỉ quan tâm đến cửa sổ tiền cảnh: việc kéo cửa sổ nền không liên quan đến chúng ta,
                // kiểm tra này chặn bão sự kiện ngay trước khi tính lại.
                if (objectId != User32.ObjIdWindow || childId != 0) return;

                if (window != User32.GetForegroundWindow()) return;
            }

            NotifyStateInvalidated();
            ScheduleRecompute();
        }
        catch (Exception) { MarkMonitorUnavailable(); }
    }

    private void ScheduleRecompute()
    {
        var timer = _coalesceTimer;

        if (timer is null)
        {
            return;
        }

        try
        {
            // Chỉ lên lịch một lần; các sự kiện đến lặp lại trong lúc chờ chỉ đẩy lùi thời điểm này về sau,
            // không tích tụ thành nhiều lần tính lại.
            timer.Change(CoalesceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Tranh chấp với Dispose. Bỏ qua là được.
        }
    }

    private void OnCoalesceElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsAvailable)
        {
            MarkMonitorUnavailable();
            return;
        }

        try
        {
            Recompute();
        }
        catch (ObjectDisposedException)
        {
            // Tranh chấp với Dispose. Bỏ qua là được.
        }
        catch (Exception)
        {
            MarkMonitorUnavailable();
        }
    }

    /// <summary>Tính lại phán đoán; mỗi lần thành công đều phát hành trạng thái, callback thay đổi vẫn chỉ kích hoạt khi giá trị đổi.</summary>
    private bool Recompute()
    {
        var generation = _getStateGeneration?.Invoke() ?? 0;
        var observation = _onPerformanceObservation is not null ? FullscreenDetector.ReadPerformanceObservation() : default;
        var current = _onPerformanceObservation is not null ? observation.IsFullscreen ?? IsFullscreen
            : FullscreenDetector.IsFullscreenActive(_options);
        if (_getStateGeneration is not null && _getStateGeneration() != generation) return current;

        Action<bool>? callback = null;

        lock (_sync)
        {
            if (current != _isFullscreen)
            {
                _isFullscreen = current;
                callback = _onChanged;
            }
        }

        // Gọi callback ngoài khóa: nếu bên gọi lại gọi vào kiểu này ngay trong callback (rất phổ biến),
        // gọi callback trong khóa sẽ gây deadlock ngay lập tức.
        try { _onStateRefreshed?.Invoke(generation, current); }
        catch (Exception) { /* Phía đầu vào vẫn ở trạng thái Unknown, giữ đóng an toàn. */ }
        try { callback?.Invoke(current); }
        catch (Exception) { /* Thông báo thay đổi không được làm chấm dứt luồng theo dõi. */ }
        try { _onPerformanceObservation?.Invoke(observation); }
        catch (Exception) { /* Performance notifications never escape the native callback path. */ }

        return current;
    }

    private void NotifyStateInvalidated()
    {
        try { _onStateInvalidated?.Invoke(); }
        catch (Exception) { /* Phía đầu vào sẽ không coi lại giá trị cũ là hợp lệ. */ }
    }

    private void MarkMonitorUnavailable()
    {
        _hooksInstalled = false;
        try { _coalesceTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        if (!_disposed) NotifyStateInvalidated();
    }
}
