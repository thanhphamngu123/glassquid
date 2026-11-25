using System.Collections.Concurrent;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>
/// Theo dõi "những bản ghi nào lúc này đang có cửa sổ đang chạy", phục vụ chấm chỉ báo trạng thái chạy (D-07).
/// </summary>
/// <remarks>
/// <para>
/// Cấu trúc gồm bộ ba <strong>liệt kê ban đầu + kích hoạt bằng sự kiện + đối soát</strong>, cố ý không coi
/// sự kiện WinEvent là nguồn chân lý duy nhất của trạng thái:
/// </para>
/// <list type="number">
/// <item><b>Liệt kê ban đầu</b>: lúc <see cref="Start"/> sẽ liệt kê ngay một lượt các cửa sổ — các ứng dụng
/// đã chạy từ trước khi Dock khởi động (chiếm đa số) chỉ có cách này mới nhìn thấy được;</item>
/// <item><b>Sự kiện</b>: <see cref="WindowEventWatcher"/> chỉ đóng vai trò tín hiệu kích hoạt, hàm gọi lại chỉ đưa vào hàng đợi,
/// việc tính lại sau khi gộp không diễn ra trên luồng hook (tài liệu chính thức nhắc rõ hàm gọi lại có rủi ro gọi lồng nhau);</item>
/// <item><b>Đối soát</b>: sau khi nhận thông báo sẽ <strong>liệt kê lại</strong> tập cửa sổ, lấy kết quả liệt kê làm chuẩn.
/// Chỉ dựa vào CREATE/DESTROY không thể bao phủ các trường hợp như "ẩn rồi hiện lại" hay "được tiến trình chủ khác mở hộ cửa sổ",
/// còn việc liệt kê tự nhiên bao phủ hết.</item>
/// </list>
///
/// <para>
/// Định nghĩa "đang chạy": tồn tại ít nhất một cửa sổ cấp cao ứng viên khớp (hiển thị hoặc cửa sổ thu nhỏ có tiêu đề).
/// Ứng dụng chỉ thu gọn vào khay thông báo mà không có bất kỳ cửa sổ cấp cao nào thì không tính — điều này nhất quán với cách làm của macOS:
/// chấm trạng thái biểu thị "có cửa sổ đang mở", không phải "tiến trình đang tồn tại".
/// </para>
/// </remarks>
public sealed class RunningAppTracker : IDisposable
{
    /// <summary>Khoảng cách tối thiểu vẫn cần đối soát sau khi đã gộp sự kiện. Bão sự kiện đã được WindowEventWatcher làm phẳng, ở đây không lặp lại.</summary>
    private static readonly TimeSpan MinimumReconcileInterval = TimeSpan.FromMilliseconds(150);

    private readonly AppLibraryStore _library;
    private readonly ConcurrentDictionary<string, string?> _identityCache = new();
    private readonly Lock _sync = new();

    private WindowEventWatcher? _watcher;
    private IReadOnlyList<WindowIdentityInfo> _windows = [];
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private volatile bool _disposed;
    private bool _suspended, _dirty;
    private long _runtimeGeneration, _reconcileCount;

    /// <summary>Completed enumeration count, useful for resource-lifecycle diagnostics.</summary>
    public long ReconcileCount => Interlocked.Read(ref _reconcileCount);
    public bool IsSuspended { get { lock (_sync) return _suspended; } }

    public void SetSuspended(bool suspended)
    {
        bool reconcile;
        lock (_sync)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
            _runtimeGeneration++;
            reconcile = !suspended && _dirty;
            if (reconcile) _lastReconcileUtc = DateTime.MinValue;
        }
        if (reconcile) Reconcile();
    }

    public RunningAppTracker(AppLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>
    /// Kích hoạt khi trạng thái chạy (có khả năng) thay đổi. Kích hoạt trên luồng thread pool,
    /// bên lắng nghe cập nhật giao diện tự chuyển về luồng của mình.
    /// </summary>
    /// <remarks>
    /// Sự kiện là tín hiệu "có thể đã đổi": khi đối soát bị gộp và bỏ qua thì sẽ không phát.
    /// Bên lắng nghe sau khi nhận được nên <strong>đọc lại</strong> trạng thái, thay vì giả định mình biết chỗ nào đã đổi —
    /// đây là sự thể hiện ở phía thông báo của cùng nguyên tắc "lấy kết quả liệt kê làm chuẩn".
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>Tập cửa sổ ứng viên thu được ở lần đối soát gần nhất. Dùng cho chẩn đoán và kiểm thử.</summary>
    public IReadOnlyList<WindowIdentityInfo> LastWindows
    {
        get
        {
            lock (_sync)
            {
                return _windows;
            }
        }
    }

    /// <summary>
    /// Bắt đầu theo dõi: liệt kê ban đầu trước, rồi mới gắn hook sự kiện.
    /// </summary>
    /// <remarks>
    /// Không gắn được hook (về lý thuyết chỉ xảy ra trong môi trường bất thường bị thiếu điểm nhập) không bị coi là lỗi:
    /// trạng thái thoái hóa thành "chỉ có ảnh chụp lúc liệt kê ban đầu", bên gọi vẫn có thể theo nhu cầu
    /// gọi <see cref="Reconcile"/> để làm mới.
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Reconcile();

        _watcher = WindowEventWatcher.TryStart(onChanged: () => _ = Reconcile());
    }

    /// <summary>Liệt kê lại ngay và làm mới trạng thái; trả về số cửa sổ liệt kê được trong lần này.</summary>
    public int Reconcile()
    {
        if (_disposed)
        {
            return 0;
        }

        long generation;
        lock (_sync)
        {
            if (_suspended) { _dirty = true; return _windows.Count; }
            // Quá sát lần đối soát trước thì bỏ qua: sau khi gộp sự kiện (120 mili giây) vẫn có thể
            // có chùm thông báo nối tiếp, việc liệt kê toàn bộ lặp lại không có ý nghĩa.
            if (DateTime.UtcNow - _lastReconcileUtc < MinimumReconcileInterval)
            {
                return _windows.Count;
            }

            _lastReconcileUtc = DateTime.UtcNow;
            generation = _runtimeGeneration;
        }

        var windows = WindowLocator.EnumerateTopLevelWindows();
        bool changed;

        lock (_sync)
        {
            if (_disposed || _suspended || generation != _runtimeGeneration)
            { _dirty = true; return _windows.Count; }
            _dirty = false;
            _reconcileCount++;
            changed = !SameWindowSet(_windows, windows);
            _windows = windows;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return windows.Count;
    }

    /// <summary>Bản ghi này lúc này có cửa sổ đang chạy hay không. Đối khớp trên ảnh chụp của lần đối soát gần nhất, không liệt kê lại.</summary>
    public bool IsRunning(AppEntry entry)
        => FindWindows(entry).Count > 0;

    /// <summary>Các cửa sổ đang chạy khớp với bản ghi này lúc này (đối khớp trên ảnh chụp của lần đối soát gần nhất). Quy tắc xem <see cref="WindowMatcher"/>.</summary>
    public IReadOnlyList<WindowIdentityInfo> FindWindows(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return WindowMatcher.MatchWindows(entry, LastWindows, WindowMatcher.ResolveAumid(entry, _identityCache));
    }

    /// <summary>
    /// ID các bản ghi trong tập ứng viên cho trước mà lúc này đang có cửa sổ chạy.
    /// </summary>
    /// <remarks>
    /// Lấy tập ứng viên do bên gọi truyền vào làm chuẩn (Dock chỉ quan tâm những mục nó hiển thị),
    /// không tự quét toàn bộ thư viện — phần lớn trong số 500 mục không nằm trên Dock,
    /// kiểm tra từng mục cho chúng là lãng phí thời gian ở mỗi lần đối soát.
    /// </remarks>
    public IReadOnlyList<string> FindRunningIds(IEnumerable<AppEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        lock (_sync)
        {
            var running = new List<string>();

            foreach (var entry in candidates)
            {
                var aumid = WindowMatcher.ResolveAumid(entry, _identityCache);

                if (WindowMatcher.MatchWindows(entry, _windows, aumid).Count > 0)
                {
                    running.Add(entry.Id);
                }
            }

            return running;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Hai nhóm cửa sổ có tương đương nhau không. Chỉ so sánh tập handle: sự tồn tại hay biến mất của cửa sổ mới làm thay đổi
    /// "có đang chạy hay không", cùng một cửa sổ đổi tiêu đề hoặc dời vị trí thì không tính.
    /// </summary>
    private static bool SameWindowSet(IReadOnlyList<WindowIdentityInfo> left, IReadOnlyList<WindowIdentityInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var handles = new HashSet<nint>(left.Select(window => window.Handle));

        foreach (var window in right)
        {
            if (!handles.Remove(window.Handle))
            {
                return false;
            }
        }

        return handles.Count == 0;
    }
}
