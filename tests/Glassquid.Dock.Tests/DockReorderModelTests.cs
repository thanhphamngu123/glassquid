using System.IO;
using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Các khẳng định cho máy trạng thái kéo-thả sắp xếp — khóa chặt năm tiêu chí nghiệm thu của R-08 tại đây:
/// không ghi đĩa lần nào trong lúc xem trước, ghi đúng một lần khi xác nhận, hủy bằng Esc không ghi đĩa, khôi phục thứ tự cũ khi lưu thất bại,
/// và kéo ra để bỏ ghim không xóa mục khỏi thư viện (mục cuối được bao phủ ở tầng dịch vụ của lệnh nghiệm thu).
/// </summary>
public class DockReorderModelTests
{
    private static readonly string[] Original = ["a", "b", "c", "d"];

    [Fact]
    public void PreviewAnyNumberOfTimes_DoesNotSaveToDisk()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("c", 0);
        model.PreviewMove("c", 1);
        model.PreviewMove("d", 0);
        model.PreviewMove("a", 3);

        Assert.Equal(0, saveCount);
        Assert.True(model.IsActive);
    }

    [Fact]
    public void CommitOnRelease_SavesOnceWithCorrectOrder()
    {
        var saved = new List<IReadOnlyList<string>>();
        var model = new DockReorderModel(order => saved.Add([.. order]));

        model.Begin(Original);
        model.PreviewMove("c", 0);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Committed, outcome);
        var save = Assert.Single(saved);
        Assert.Equal(["c", "a", "b", "d"], save);
    }

    [Fact]
    public void ReturnToOriginalSlot_CommitDoesNotSave()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("b", 2); // Di chuyển đi
        model.PreviewMove("b", 1); // Đặt lại chỗ cũ

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Unchanged, outcome);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void CancelWithEsc_DoesNotSaveAndKeepsOriginalOrder()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("d", 0);

        var outcome = model.Cancel();

        Assert.Equal(ReorderOutcome.Cancelled, outcome);
        Assert.Equal(0, saveCount);
        Assert.Equal(Original, model.Order);
    }

    [Fact]
    public void CommitFailure_RestoresOriginalOrderAndReportsFailure()
    {
        var model = new DockReorderModel(_ => throw new IOException("Đĩa từ chối ghi"));

        model.Begin(Original);
        model.PreviewMove("b", 3);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.SaveFailed, outcome);
        Assert.Equal(Original, model.Order);
        Assert.False(model.IsActive);
    }

    [Fact]
    public void CommitBeforeDragBegins_IsNoOp()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Idle, outcome);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void PreviewOutOfBoundsTargetIndex_ClampsToBoundary()
    {
        var model = new DockReorderModel(_ => { });

        model.Begin(Original);
        model.PreviewMove("a", 100); // Kẹp về cuối

        Assert.Equal(["b", "c", "d", "a"], model.Order);
    }
}
