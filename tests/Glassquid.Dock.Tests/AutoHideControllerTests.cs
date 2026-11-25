using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Các khẳng định theo thời gian cho máy trạng thái tự động ẩn (D-19). Thời gian được phát lại bằng dấu thời gian tường minh —
/// các hành vi như "con trỏ lướt qua mép không bị rung giật" được khóa chặt bằng chuỗi thời gian, không đụng tới đồng hồ thật và chuột.
/// </summary>
public class AutoHideControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Giá trị mặc định của D-19: khoảng 300 ms.</summary>
    private const double DelayMs = 300;

    [Fact]
    public void PointerSkimsEdgeReentersWithin300MsDoesNotHide()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0.AddMilliseconds(80));   // Lướt qua mép
        controller.Poll(T0.AddMilliseconds(200));          // Chưa hết độ trễ
        controller.PointerEnter(T0.AddMilliseconds(250));  // Đã quay lại

        controller.Poll(T0.AddSeconds(5));

        Assert.Equal(AutoHideState.Shown, controller.State);
    }

    [Fact]
    public void PointerLeavesPastDelayHides()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);

        controller.Poll(T0.AddMilliseconds(299));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State);

        controller.Poll(T0.AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void ProductFrameCallOrderPointerLeavesHidesAfterAbout300Ms()
    {
        // Cách tái hiện: phát lại mỗi 16 mili giây theo đúng thứ tự gọi thực tế của DockWindow.OnAutoHideFrame:
        // "Dò → PointerLeave/PointerEnter → Poll".
        //
        // Dạng này từng gặp một lỗi không báo lỗi nào: mỗi khung hình gọi PointerLeave một lần sẽ đẩy
        // thời điểm rời đi lùi mãi về sau, bộ đếm không bao giờ tới hạn — thực nghiệm phát lại gần 2 giây vẫn kẹt ở
        // WaitingToHide. Cách sửa là "chỉ lần rời đi đầu tiên mới khởi động bộ đếm".
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);

        // Con trỏ rời đi ở khung hình thứ hai, sau đó luôn nằm ngoài thanh.
        var hiddenAtMs = -1;

        for (var milliseconds = 0; milliseconds <= 2000; milliseconds += 16)
        {
            var now = T0.AddMilliseconds(milliseconds);

            if (milliseconds < 16)
            {
                controller.PointerEnter(now);
            }
            else
            {
                controller.PointerLeave(now);
            }

            controller.Poll(now);

            if (controller.State == AutoHideState.Hidden)
            {
                hiddenAtMs = milliseconds;
                break;
            }
        }

        Assert.Equal(AutoHideState.Hidden, controller.State);
        Assert.InRange(hiddenAtMs, 300, 500); // Độ mịn khung hình 16 mili giây + một chút dự phòng
    }

    [Fact]
    public void RepeatedLeavePerFrameDoesNotResetTimerButReenterThenLeaveRestartsTimer()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);

        // 10 khung hình "rời đi" liên tiếp: bộ đếm tính từ khung hình đầu tiên.
        for (var milliseconds = 0; milliseconds < 160; milliseconds += 16)
        {
            controller.PointerLeave(T0.AddMilliseconds(milliseconds));
        }

        controller.Poll(T0.AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // Vào lại rồi rời đi: đây là một lần rời đi mới, phải đếm lại đủ 300 mili giây.
        controller.PointerEnter(T0.AddSeconds(1));
        controller.PointerLeave(T0.AddSeconds(1).AddMilliseconds(10));
        controller.Poll(T0.AddSeconds(1).AddMilliseconds(200));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State);

        controller.Poll(T0.AddSeconds(1).AddMilliseconds(310));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void PopupOpenDoesNotHideAndClosingRestartsTimer()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // Menu chuột phải mở

        controller.PointerLeave(T0.AddMilliseconds(100));
        controller.Poll(T0.AddSeconds(10));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddSeconds(10));         // Menu đóng
        controller.Poll(T0.AddSeconds(10).AddMilliseconds(150));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State); // Đếm lại từ đầu, không chớp biến mất

        controller.Poll(T0.AddSeconds(10).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void SuppressIsReentrantNestedPanelsRequireOutermostPop()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // Bảng thư mục
        controller.PushSuppress(T0);                       // Trên bảng lại mở tiếp danh sách chọn cửa sổ

        controller.PointerLeave(T0);
        controller.PopSuppress(T0.AddSeconds(1));          // Đóng một lớp
        controller.Poll(T0.AddSeconds(30));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddSeconds(30));         // Lớp cuối cùng đóng
        controller.Poll(T0.AddSeconds(30).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void DragSuppressUsesSameMechanismAsPopupSuppress()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // Bắt đầu kéo thả
        controller.PointerLeave(T0.AddMilliseconds(10));   // Kéo biểu tượng rời khỏi thanh

        controller.Poll(T0.AddMinutes(1));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddMinutes(1));          // Nhả chuột
        controller.Poll(T0.AddMinutes(1).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void HotZoneRevealWhenCoveredIsRejectedSuppressTakesPrecedence()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);
        controller.Poll(T0.AddSeconds(1));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // Ứng dụng toàn màn hình ở phía trước: gọi hiện từ vùng nóng bị từ chối (ngữ nghĩa của DockSettings.BlockRevealWhenCovered).
        Assert.False(controller.TryReveal(isCovered: true, T0.AddSeconds(2)));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // Quay lại màn hình nền: gọi hiện thành công.
        Assert.True(controller.TryReveal(isCovered: false, T0.AddSeconds(3)));
        Assert.Equal(AutoHideState.Shown, controller.State);
    }

    [Fact]
    public void HotZoneRevealDuringWaitingPeriodStaysShown()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);
        controller.Poll(T0.AddMilliseconds(100));          // WaitingToHide

        // Con trỏ lại chạm vào vùng nóng (chưa đi xa): trở về hiển thị ngay, không chớp hiện lại.
        Assert.True(controller.TryReveal(isCovered: false, T0.AddMilliseconds(150)));
        Assert.Equal(AutoHideState.Shown, controller.State);
    }
}
