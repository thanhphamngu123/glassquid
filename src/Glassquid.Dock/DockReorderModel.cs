namespace Glassquid.Dock;

/// <summary>Kết quả xác nhận sắp xếp lại.</summary>
public enum ReorderOutcome
{
    /// <summary>Chưa từng bắt đầu kéo, không cần xác nhận.</summary>
    Idle = 0,

    /// <summary>Thứ tự có thay đổi, đã xác nhận và lưu.</summary>
    Committed = 1,

    /// <summary>Thứ tự không đổi (đặt lại vị trí cũ), không cần lưu.</summary>
    Unchanged = 2,

    /// <summary>Đã bị hủy (Esc hoặc kéo ra ngoài thanh), giữ nguyên thứ tự.</summary>
    Cancelled = 3,

    /// <summary>Xác nhận thất bại: đã khôi phục thứ tự ban đầu, bên gọi sẽ thông báo cho người dùng.</summary>
    SaveFailed = 4,
}

/// <summary>
/// Máy trạng thái sắp xếp kéo thả ô trên Dock — bộ quyết định duy nhất cho quy tắc "xem trước chỉ sửa bộ nhớ, thả tay mới ghi xuống đĩa" (R-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tại sao tách thành một lớp riêng.</b> Tiêu chí nghiệm thu của R-08 là "không được ghi đĩa trong lúc kéo; sau khi hoàn tất thao tác
/// mới lưu thống nhất một lần". Việc biến "xem trước → xác nhận/hủy" thành máy trạng thái tường minh giúp số lần lưu trở thành
/// đại lượng có thể khẳng định bằng kiểm thử: xem trước bao nhiêu lần, hủy bao nhiêu lần thì việc ghi đĩa chỉ có thể xảy ra
/// một lần duy nhất trong <see cref="Commit"/>. Lớp giao diện (<see cref="DockTileStrip"/>) chỉ chịu trách nhiệm
/// dịch cử chỉ kéo thành ba hành động tại đây.
/// </para>
///
/// <para>
/// <b>Hành vi khi xác nhận thất bại.</b> Khi lưu ném ngoại lệ thì <strong>khôi phục thứ tự ban đầu</strong> và giao lỗi
/// cho bên gọi thông báo — không được để xảy ra trạng thái âm thầm "thứ tự trên giao diện không khớp với trên đĩa".
/// </para>
/// </remarks>
public sealed class DockReorderModel
{
    private readonly List<string> _originalOrder = [];
    private readonly List<string> _previewOrder = [];
    private readonly Action<IReadOnlyList<string>> _save;

    private bool _active;

    /// <summary>Trả về true khi đang sắp xếp (đang kéo chưa thả tay và chưa hủy).</summary>
    public bool IsActive => _active;

    /// <summary>Thứ tự xem trước hiện tại (khi đang kéo) hoặc thứ tự gốc (khi không kéo).</summary>
    public IReadOnlyList<string> Order => _active ? _previewOrder : _originalOrder;

    /// <param name="save">
    /// Delegate ghi xuống đĩa: khi xác nhận sẽ gọi một lần với thứ tự cuối cùng. Bản cài đặt chỉ nên ghi tệp một lần
    /// (<c>LayoutStore</c>: sửa <c>PinnedItemIds</c> rồi gọi <c>SaveDock()</c>).
    /// </param>
    public DockReorderModel(Action<IReadOnlyList<string>> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = save;
    }

    /// <summary>Bắt đầu một lần kéo. Ghi nhớ thứ tự hiện tại làm mốc hoàn tác.</summary>
    public void Begin(IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        _originalOrder.Clear();
        _originalOrder.AddRange(order);
        _previewOrder.Clear();
        _previewOrder.AddRange(order);
        _active = true;
    }

    /// <summary>Di chuyển xem trước mục đang kéo tới chỉ mục đích. Chỉ sửa bộ nhớ; chỉ mục vượt biên sẽ được kẹp theo giới hạn.</summary>
    public void PreviewMove(string itemId, int targetIndex)
    {
        if (!_active)
        {
            return;
        }

        var current = _previewOrder.IndexOf(itemId);

        if (current < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _previewOrder.Count - 1);

        if (targetIndex == current)
        {
            return;
        }

        _previewOrder.RemoveAt(current);
        _previewOrder.Insert(targetIndex, itemId);
    }

    /// <summary>Xác nhận: lưu một lần nếu thứ tự có thay đổi. Nếu thất bại thì khôi phục thứ tự ban đầu và báo lỗi.</summary>
    public ReorderOutcome Commit()
    {
        if (!_active)
        {
            return ReorderOutcome.Idle;
        }

        _active = false;

        if (SameOrder(_previewOrder, _originalOrder))
        {
            return ReorderOutcome.Unchanged;
        }

        var finalOrder = _previewOrder.ToArray();

        try
        {
            _save(finalOrder);
            return ReorderOutcome.Committed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lưu thất bại: hủy bỏ bản xem trước trong bộ nhớ, quay về thứ tự gốc. Bên gọi chịu trách nhiệm thông báo lỗi.
            _previewOrder.Clear();
            _previewOrder.AddRange(_originalOrder);
            _ = ex;
            return ReorderOutcome.SaveFailed;
        }
    }

    /// <summary>Hủy (Esc, kéo ra ngoài thanh, mất tiêu điểm): giữ nguyên thứ tự gốc, tuyệt đối không ghi xuống đĩa.</summary>
    public ReorderOutcome Cancel()
    {
        if (!_active)
        {
            return ReorderOutcome.Idle;
        }

        _active = false;
        _previewOrder.Clear();
        _previewOrder.AddRange(_originalOrder);
        return ReorderOutcome.Cancelled;
    }

    private static bool SameOrder(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
