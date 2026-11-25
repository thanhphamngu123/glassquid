namespace Glassquid.Dock;

/// <summary>Hành động cần thực hiện khi kết thúc một lần kéo ô.</summary>
public enum DragEndAction
{
    /// <summary>Thả trong thanh: thứ tự đã được xác nhận trong Drop, không cần làm gì thêm.</summary>
    KeepCommitted = 0,

    /// <summary>Ở trong thanh nhưng không Drop (đã nhấn Esc): hủy bản xem trước, giữ nguyên bố cục.</summary>
    CancelOnly = 1,

    /// <summary>Thả ngoài thanh: hủy bản xem trước và xác nhận như thao tác "kéo ra để bỏ ghim" (D-03).</summary>
    CancelAndUnpin = 2,
}

/// <summary>
/// Phán đoán khi kết thúc thao tác kéo ô.
/// </summary>
/// <remarks>
/// <para>
/// <b>Việc phán đoán phải được thực hiện một lần khi kết thúc kéo, chứ không phải trong DragLeave.</b>
/// Bản cài đặt trước đây kích hoạt bỏ ghim ngay khi đạt ngưỡng khoảng cách trong <c>DragLeave</c>, máy chủ lập tức ghi xuống đĩa,
/// dẫn đến hai hệ quả thực tế:
/// </para>
/// <list type="number">
/// <item>Khi nhấn Esc để hủy toàn bộ thao tác kéo, việc xóa đã bị ghi xuống và không thể hoàn tác;</item>
/// <item>Khi di chuyển chậm ra khỏi biên, vị trí của một lần <c>DragLeave</c> đơn lẻ có thể chưa đạt ngưỡng,
/// cử chỉ lại không được nhận diện là kéo ra — kết quả của cùng một cử chỉ phụ thuộc vào tốc độ chuột.</item>
/// </list>
/// <para>
/// Nay đổi thành: trong khi kéo chỉ cập nhật nhãn "lúc này có đang ở trong thanh không", đợi khi cử chỉ kết thúc (<c>DoDragDrop</c> trả về)
/// rồi mới dùng <strong>vị trí con trỏ tại thời điểm đó</strong> để phán đoán dứt khoát một lần. Ngoài ra,
/// <strong>hủy được ưu tiên hơn kéo ra</strong>: "kéo ra ngoài thanh rồi nhấn Esc" là hủy toàn bộ thao tác kéo,
/// không phải "kéo ra để bỏ ghim" — nếu chỉ phán đoán theo vị trí sẽ xóa nhầm mục mà người dùng đã chủ động hủy.
/// </para>
/// </remarks>
public static class DockDragGesture
{
    /// <summary>
    /// Đưa ra hành động dựa trên ba dữ kiện khi kết thúc kéo.
    /// </summary>
    /// <param name="droppedInStrip">Đã nhận Drop trong thanh (thứ tự đã được xác nhận tại thời điểm đó).</param>
    /// <param name="endedOutside">Khi cử chỉ kết thúc, con trỏ đang ở ngoài thanh (bao gồm vượt quá ngưỡng bỏ ghim).</param>
    /// <param name="cancelled">
    /// Lần kéo này đã bị <strong>hủy</strong> (nhấn Esc, hoặc chuột vẫn đang nhấn trước khi thả).
    /// Ưu tiên hủy: ngay cả khi con trỏ ở ngoài thanh cũng chỉ hủy bản xem trước, không xóa mục.
    /// </param>
    public static DragEndAction DecideEnd(bool droppedInStrip, bool endedOutside, bool cancelled)
    {
        if (droppedInStrip)
        {
            return DragEndAction.KeepCommitted;
        }

        if (cancelled)
        {
            return DragEndAction.CancelOnly;
        }

        return endedOutside ? DragEndAction.CancelAndUnpin : DragEndAction.CancelOnly;
    }
}

/// <summary>Lý do hủy một lần kéo. Được ghi lại để phục vụ chẩn đoán và hiển thị trạng thái.</summary>
public enum DragCancelReason
{
    /// <summary>Không phải hủy.</summary>
    None = 0,

    /// <summary>Đã nhấn Esc.</summary>
    Escape = 1,

    /// <summary>Khi cử chỉ kết thúc, chuột vẫn đang nhấn (một dạng quan sát được khác của việc hủy bằng Esc).</summary>
    PrimaryButtonStillDown = 2,
}