using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Khẳng định về phán đoán kết thúc kéo và gộp ghim — tương ứng với hai lỗi trong báo cáo rà soát.
/// </summary>
public class DockDragGestureTests
{
    [Fact]
    public void EscapeEnd_KeepsLayoutAndDoesNotTriggerUnpin()
    {
        // Khi nhấn Esc, con trỏ vẫn ở trong thanh và chưa Drop. Bản cài đặt trước đây đã ghi
        // "kéo ra" trong DragLeave nên nhấn Esc không hoàn tác được.
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: false, cancelled: true));
    }

    [Fact]
    public void DraggedOutsideAndEscape_CancelsOnlyWithoutRemovingItem()
    {
        // Lỗi trong báo cáo rà soát: phán đoán chỉ phân biệt "nhận Drop trong thanh" và "con trỏ ngoài thanh",
        // nên "kéo ra ngoài thanh rồi nhấn Esc" bị coi là kéo ra để bỏ ghim — mục mà người dùng chủ động hủy bị xóa mất.
        // Hủy được ưu tiên hơn kéo ra.
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: true));
    }

    [Fact]
    public void ReleasedOutsideNormally_TriggersCancelAndUnpin()
    {
        Assert.Equal(
            DragEndAction.CancelAndUnpin,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: false));
    }

    [Fact]
    public void DroppedInStrip_KeepsCommittedOrderWithNoExtraAction()
    {
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: false, cancelled: false));
    }

    [Fact]
    public void DroppedInStripWithPointerOutside_TakesDroppedInStrip()
    {
        // Rung biên: Drop đã xảy ra trong thanh thì không thể coi là kéo ra nữa.
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: true, cancelled: false));
    }

    [Fact]
    public void DroppedInStripWithEscapeCancelFlag_KeepsCommittedOrder()
    {
        // Drop đã xảy ra = người dùng đã chủ động thả tay đặt xuống; lúc này dù có tín hiệu hủy cũng không hoàn tác.
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: true, cancelled: true));
    }

    [Fact]
    public void EndedWithPrimaryButtonStillDown_CancelsWithoutUnpinning()
    {
        // Khi tín hiệu hủy đến từ nhánh dự phòng "chuột vẫn đang nhấn", cũng không được kích hoạt xóa.
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: true));
    }
}

/// <summary>Quy tắc gộp danh sách ghim sau khi kéo thả để nhập.</summary>
public class DockPinMergeTests
{
    [Fact]
    public void ExistingLibraryItemDraggedBack_IsRepinned()
    {
        // Tái hiện lỗi: khi chỉ xét "thêm mới", kéo lại ứng dụng đã gỡ khỏi Dock vào, nhập báo "trùng lặp 1",
        // cử chỉ không có phản hồi nào.
        var merged = DockPinMerge.Merge(["a", "b"], added: [], duplicates: ["c"]);

        Assert.Equal(["a", "b", "c"], merged);
    }

    [Fact]
    public void MixedAddedAndDuplicateItems_AllEnterPinnedList()
    {
        var merged = DockPinMerge.Merge(["a"], added: ["b"], duplicates: ["c"]);

        Assert.Equal(["a", "b", "c"], merged);
    }

    [Fact]
    public void AlreadyPinnedItems_AreNotAppendedAgain()
    {
        var merged = DockPinMerge.Merge(["a", "b"], added: ["b"], duplicates: ["a"]);

        Assert.Equal(["a", "b"], merged);
    }

    [Fact]
    public void DuplicateIdInSingleImport_IsPinnedOnlyOnce()
    {
        var merged = DockPinMerge.Merge([], added: ["x"], duplicates: ["x"]);

        Assert.Equal(["x"], merged);
    }

    [Fact]
    public void CountNewlyPinned_CountsOnlyTrulyAddedPinnedItems()
    {
        Assert.Equal(1, DockPinMerge.CountNewlyPinned(["a"], added: ["b"], duplicates: ["a"]));
        Assert.Equal(0, DockPinMerge.CountNewlyPinned(["a"], added: [], duplicates: ["a"]));
    }
}