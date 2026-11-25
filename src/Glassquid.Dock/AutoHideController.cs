using Glassquid.Core.Models.Settings;

namespace Glassquid.Dock;

/// <summary>Trạng thái tức thời của tính năng tự động ẩn.</summary>
public enum AutoHideState
{
    /// <summary>Đang hiển thị. Con trỏ đang trên thanh, hoặc đã rời đi nhưng chưa hết thời gian trễ.</summary>
    Shown = 0,

    /// <summary>Đang chờ thu gọn: con trỏ đã rời đi, đang đếm thời gian trễ. Nếu quay lại lúc này sẽ trở về <see cref="Shown"/>.</summary>
    WaitingToHide = 1,

    /// <summary>Đã thu gọn. Chỉ có thể trở về <see cref="Shown"/> khi kích hoạt vùng nóng hoặc có tác nhân khác.</summary>
    Hidden = 2,
}

/// <summary>
/// Máy trạng thái tự động ẩn (D-19).
/// </summary>
/// <remarks>
/// <para>
/// Cố ý thiết kế thành máy trạng thái thuần túy: mọi đầu vào đều kèm dấu thời gian tường minh, bên trong không đọc đồng hồ, không đụng tới chuột,
/// không di chuyển cửa sổ — nhờ vậy các hành vi theo thời gian như "con trỏ lướt qua mép không bị rung giật" có thể được phát lại để kiểm thử ngoại tuyến với
/// chuỗi thời gian tùy ý. Việc dò con trỏ thực tế và dịch chuyển cửa sổ nằm ở <see cref="DockWindow"/>.
/// </para>
///
/// <para>
/// Quy tắc tương tác (mục 4 của kế hoạch sửa đổi, triển khai từng mục):
/// <list type="number">
/// <item>Trong thời gian phần tử bật lên (menu chuột phải, bảng thư mục, danh sách chọn cửa sổ) đang mở thì <strong>không ẩn</strong>
/// — <see cref="PushSuppress"/>/<see cref="PopSuppress"/>;</item>
/// <item>Đang kéo thả thì không ẩn — dùng chung cặp cổng ức chế này;</item>
/// <item>Khi bị che phủ/toàn màn hình thì <strong>việc gọi hiện bị ức chế</strong> (<see cref="DockSettings.BlockRevealWhenCovered"/>
/// mặc định bật): cạnh trước của ứng dụng toàn màn hình không được bị Dock che mất; ức chế ưu tiên hơn mọi lệnh gọi hiện;</item>
/// <item>Độ trễ thu gọn khoảng 300 ms (giá trị mặc định của D-19): khi con trỏ lướt qua mép,
/// "rời đi → vào lại" diễn ra trong khoảng trễ nên máy trạng thái hoàn toàn không thu gọn — đó là cơ chế chống rung giật.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AutoHideController
{
    private readonly double _leaveDelayMilliseconds;

    private int _suppressCount;
    private DateTime _leftAtUtc;
    private bool _waiting;

    /// <summary>Kích hoạt khi trạng thái thay đổi (kèm trạng thái mới), để lớp chủ quyết định có di chuyển cửa sổ hay không.</summary>
    public event EventHandler<AutoHideState>? StateChanged;

    public AutoHideState State { get; private set; } = AutoHideState.Shown;

    /// <summary>Hiện có cơ chế ức chế nào đang hiệu lực hay không (phần tử bật lên đang mở hoặc đang kéo thả).</summary>
    public bool Suppressed => _suppressCount > 0;

    public AutoHideController(double leaveDelayMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaveDelayMilliseconds);
        _leaveDelayMilliseconds = leaveDelayMilliseconds;
    }

    /// <summary>Con trỏ đi vào trong thanh (hoặc gọi hiện từ vùng nóng thành công).</summary>
    public void PointerEnter(DateTime utcNow)
    {
        _ = utcNow;
        _waiting = false;
        Transition(AutoHideState.Shown);
    }

    /// <summary>
    /// Con trỏ rời khỏi thanh. Bắt đầu đếm trễ; nếu đang bị ức chế hoặc đang đếm thì giữ nguyên trạng thái.
    /// </summary>
    /// <remarks>
    /// <b>Chỉ lần rời đi đầu tiên mới khởi động bộ đếm.</b> Lớp chủ gọi hàm này ở mỗi khung hình (khi con trỏ ở ngoài thanh),
    /// nếu mỗi lần đều đẩy thời điểm rời đi về sau thì bộ đếm sẽ không bao giờ tới hạn — thực nghiệm phát lại theo thứ tự gọi của sản phẩm
    /// gần 2 giây vẫn kẹt ở <see cref="AutoHideState.WaitingToHide"/>,
    /// biểu hiện là tính năng tự động ẩn tê liệt hoàn toàn mà không báo bất kỳ lỗi nào.
    /// </remarks>
    public void PointerLeave(DateTime utcNow)
    {
        if (State == AutoHideState.Hidden || Suppressed)
        {
            return;
        }

        if (_waiting)
        {
            // Đang đếm rồi: đây là các khung hình tiếp theo của cùng một lần rời đi, không được đặt lại mốc bắt đầu.
            return;
        }

        _leftAtUtc = utcNow;
        _waiting = true;
        Transition(AutoHideState.WaitingToHide);
    }

    /// <summary>Kiểm tra định kỳ: nếu đã quá độ trễ và không bị ức chế thì thu gọn.</summary>
    /// <remarks>
    /// Được lớp chủ gọi theo nhịp (khoảng 60 Hz trong vòng lặp con trỏ/hoạt ảnh sẵn có,
    /// không phải luồng thăm dò riêng). Việc tính thời gian trễ thực tế chỉ chốt một lần tại đây.
    /// </remarks>
    public void Poll(DateTime utcNow)
    {
        if (!_waiting || Suppressed)
        {
            return;
        }

        if ((utcNow - _leftAtUtc).TotalMilliseconds >= _leaveDelayMilliseconds)
        {
            _waiting = false;
            Transition(AutoHideState.Hidden);
        }
    }

    /// <summary>Gọi hiện từ vùng nóng. <paramref name="isCovered"/> là true nghĩa là vùng Dock đang bị cửa sổ khác che hoặc ở chế độ toàn màn hình.</summary>
    /// <remarks>
    /// Việc cấm gọi hiện khi bị che phủ (<see cref="DockSettings.BlockRevealWhenCovered"/>) do bên gọi tự phát hiện rồi
    /// truyền vào: bản thân máy trạng thái không truy vấn cửa sổ. Ức chế được ưu tiên — khi bị che phủ, lệnh gọi hiện từ vùng nóng bị từ chối thẳng.
    /// </remarks>
    public bool TryReveal(bool isCovered, DateTime utcNow)
    {
        if (State == AutoHideState.Shown)
        {
            return true;
        }

        if (State == AutoHideState.WaitingToHide)
        {
            // "Gọi hiện" trong lúc đang đếm trễ = con trỏ đã quay lại: phải thực sự trở về trạng thái hiển thị và hủy chờ,
            // nếu không khi bộ đếm tới hạn vẫn sẽ thu gọn cửa sổ mất — dù con trỏ rõ ràng đang nằm trên thanh.
            PointerEnter(utcNow);
            return true;
        }

        if (isCovered)
        {
            return false;
        }

        PointerEnter(utcNow);
        return true;
    }

    /// <summary>Phần tử bật lên mở hoặc bắt đầu kéo thả: tạm dừng đếm ẩn. Có thể gọi lồng nhau (bảng chồng lên bảng).</summary>
    public void PushSuppress(DateTime utcNow)
    {
        _ = utcNow;
        _suppressCount++;
    }

    /// <summary>Phần tử bật lên đóng hoặc kết thúc kéo thả. Sau khi gỡ lớp ức chế cuối cùng, bắt đầu đếm lại độ trễ để thu gọn.</summary>
    /// <remarks>
    /// Lúc này phần lớn con trỏ đã không còn trên thanh, vì vậy khi gỡ bỏ sẽ tính lại từ mốc "vừa rời đi",
    /// thay vì thu gọn ngay lập tức — nếu không vừa đóng bảng là Dock chớp tắt biến mất ngay.
    /// </remarks>
    public void PopSuppress(DateTime utcNow)
    {
        if (_suppressCount == 0)
        {
            return;
        }

        _suppressCount--;

        if (_suppressCount == 0 && State == AutoHideState.Shown)
        {
            PointerLeave(utcNow);
        }
    }

    /// <summary>Trạng thái ban đầu khi tắt tính năng tự động ẩn.</summary>
    public void ForceShown(DateTime utcNow) => PointerEnter(utcNow);

    /// <summary>Bỏ qua độ trễ rời đi thông thường và thu gọn ngay lập tức, dùng cho toàn màn hình/bị che và khóa gọi hiện tường minh.</summary>
    public void ForceHide(DateTime utcNow)
    {
        _leftAtUtc = utcNow;
        _waiting = false;
        Transition(AutoHideState.Hidden);
    }

    private void Transition(AutoHideState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }
}
