namespace Glassquid.Dock;

/// <summary>Hình học tức thời của một ô (tile) trên trục Dock.</summary>
/// <param name="Center">Vị trí tâm của ô dọc theo trục Dock (DIP, hệ tọa độ nội dung).</param>
/// <param name="Scale">Tỷ lệ phóng đại hiện tại. 1 = không phóng đại.</param>
public readonly record struct TileGeometry(double Center, double Scale);

/// <summary>
/// Tính toán hình học cho hiệu ứng phóng đại — nguồn duy nhất dùng chung cho vẽ và kiểm tra chạm (D-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Mô hình khe (slot): khe cố định.</b> Khi không phóng đại, vị trí tâm của mỗi ô là cố định; các ô gần con trỏ
/// phóng đại tại chỗ (giãn nở bốn phía), đè lên các ô lân cận bên dưới. Hai mô hình ứng viên từng được so sánh trong kế hoạch sửa đổi:
/// sắp xếp động (các ô lân cận bị đẩy ra) không chồng lấn, nhưng khi con trỏ di chuyển thì cả hàng đều dịch chuyển, điểm rơi của thao tác kéo và nhấp
/// bị trôi theo; dưới mô hình khe cố định, các khe chưa phóng đại là hằng số, dù vùng chạm có chồng lấn nhưng chỉ cần gán theo quy tắc "nhìn thấy gì nhấp trúng nấy"
/// là được. Việc lựa chọn giữa hai hướng đã được xác nhận (xem Sửa đổi 3 của kế hoạch Giai đoạn 4).
/// </para>
///
/// <para>
/// <b>Nhìn thấy gì nhấp trúng nấy.</b> Phán đoán chạm sử dụng <strong>cùng một</strong> kết quả hình học với bước vẽ
/// (đầu ra của <see cref="Compute"/> được đưa đồng thời cho cả bước vẽ và <see cref="HitTest"/>),
/// vùng chồng lấn thuộc về <strong>ô có tỷ lệ phóng đại lớn nhất</strong> — ô đó được vẽ ở lớp trên cùng, nhìn thấy ai thì nhấp trúng người đó.
/// Trong suốt quá trình chuyển động, cả hai cũng luôn nhất quán: hình học là hàm thuần túy của "vị trí con trỏ hiện tại",
/// mỗi khung hình vẽ và kiểm tra chạm đều dùng kết quả tính toán của cùng một khung hình.
/// </para>
///
/// <para>
/// Mọi tọa độ đều dọc theo trục Dock (Dock ngang là X, Dock dọc là Y), đơn vị DIP.
/// Không phụ thuộc vào trục vuông góc — phần giãn nở phóng đại theo phương vuông góc do độ dày dự phòng của vùng chứa đảm nhận
/// (xem <see cref="RequiredThickness"/>).
/// </para>
/// </remarks>
public static class DockMagnification
{
    /// <summary>
    /// Tính toán hình học tức thời cho từng ô. Tâm trả về đã bao gồm phần chừa trống hai đầu (xem <see cref="EndPadding"/>),
    /// do đó có thể dùng trực tiếp làm tọa độ canvas để bố trí và kiểm tra chạm.
    /// </summary>
    /// <param name="count">Số lượng ô.</param>
    /// <param name="iconSize">Độ dài cạnh biểu tượng khi chưa phóng đại, DIP.</param>
    /// <param name="spacing">Khoảng cách giữa các biểu tượng, DIP.</param>
    /// <param name="peak">Hệ số phóng đại cực đại (D-17). 1 nghĩa là không phóng đại.</param>
    /// <param name="pointerPosition">Vị trí con trỏ dọc theo trục Dock (tọa độ canvas, DIP); truyền null khi con trỏ không nằm trên thanh.</param>
    public static TileGeometry[] Compute(
        int count,
        double iconSize,
        double spacing,
        double peak,
        double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var geometry = new TileGeometry[count];
        ComputeInto(geometry, iconSize, spacing, peak, pointerPosition);
        return geometry;
    }

    internal static void ComputeInto(Span<TileGeometry> geometry, double iconSize, double spacing,
        double peak, double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        for (var index = 0; index < geometry.Length; index++)
        {
            var center = SlotCenter(index, iconSize, spacing, peak);
            var scale = 1.0;

            if (pointerPosition is { } pointer)
            {
                var distance = Math.Abs(pointer - center);
                scale = 1 + (peak - 1) * Falloff(distance, iconSize, spacing);
            }

            geometry[index] = new TileGeometry(center, scale);
        }

    }

    internal static int CompareDrawOrder(TileGeometry[] geometry, int left, int right)
    {
        var scaleOrder = geometry[right].Scale.CompareTo(geometry[left].Scale);
        return scaleOrder != 0 ? scaleOrder : left.CompareTo(right);
    }

    /// <summary>
    /// Thứ tự z <strong>dùng chung</strong> giữa vẽ và kiểm tra chạm: ô phóng đại lớn hơn nằm trên, cùng tỷ lệ thì theo chỉ mục.
    /// </summary>
    /// <remarks>
    /// Phía vẽ dùng nó để đặt <c>Panel.ZIndex</c>, phía kiểm tra chạm duyệt theo cùng thứ tự để tìm ô đầu tiên bắt trúng con trỏ.
    /// Hai bên dùng chung hàm này thay vì tự viết lại phép sắp xếp — "nhìn thấy gì thì nhấp trúng nấy" mới đứng vững về mặt cấu trúc,
    /// chứ không phải nhờ hai nơi ngầm thống nhất quy ước với nhau.
    /// </remarks>
    public static int[] TopToBottomOrder(TileGeometry[] geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var order = Enumerable.Range(0, geometry.Length).ToArray();
        Array.Sort(order, Comparer<int>.Create((left, right) => CompareDrawOrder(geometry, left, right)));
        return order;
    }

    /// <summary>
    /// Kiểm tra chạm: con trỏ đang rơi vào ô nào (nhìn thấy gì nhấp trúng nấy).
    /// </summary>
    /// <remarks>
    /// Tìm ô đầu tiên chứa con trỏ theo thứ tự <see cref="TopToBottomOrder"/>.
    /// <paramref name="geometry"/> bắt buộc phải là bản của <strong>lần vẽ thực tế cuối cùng</strong>
    /// (đây chính là lý do <c>DockTileStrip</c> lưu đệm khung hình đã vẽ) — nếu tính lại bản mới bằng vị trí con trỏ tức thời,
    /// giữa chừng hoạt ảnh nó sẽ lệch khỏi hình ảnh trên màn hình, dẫn đến lỗi "nhìn thấy A nhưng nhấp trúng B".
    /// Nếu không có ô nào bắt trúng con trỏ (rơi vào khe hở) thì trả về -1.
    /// </remarks>
    public static int HitTest(TileGeometry[] geometry, double iconSize, double pointerPosition)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        foreach (var index in TopToBottomOrder(geometry))
        {
            var (center, scale) = geometry[index];
            var half = iconSize * scale / 2;

            if (pointerPosition >= center - half && pointerPosition < center + half)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Phần chừa trống ở hai đầu dọc theo trục Dock (DIP): khi phóng đại cực đại, ô đầu và ô cuối mỗi ô giãn ra ngoài từng này.
    /// </summary>
    /// <remarks>
    /// Phải thực sự đẩy các ô <strong>vào trong</strong> để nhường khoảng dư này, chứ không chỉ cộng thêm vào tổng chiều dài.
    /// Nếu chỉ cộng tổng chiều dài thì cạnh trái của ô đầu tiên vẫn nằm ở tọa độ âm (đo thực tế −8.4 DIP ở mức đỉnh mặc định),
    /// trong khi vùng chứa có cắt xén (clip), nên khi phóng đại sẽ bị cắt mất một cạnh.
    /// </remarks>
    public static double EndPadding(double iconSize, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        return (peak - 1) * iconSize / 2;
    }

    /// <summary>
    /// Tổng chiều dài cần thiết cho một hàng ô (dọc theo trục Dock, DIP).
    /// </summary>
    /// <remarks>
    /// Bằng "tổng chiều dài chưa phóng đại + phần chừa trống hai đầu", khớp hoàn toàn với tọa độ của <see cref="Compute"/>:
    /// cạnh trái của ô đầu tiên sau khi phóng đại nằm đúng tại 0, cạnh phải của ô cuối cùng bằng đúng chiều dài này.
    /// </remarks>
    public static double RequiredLength(int count, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
        {
            return 0;
        }

        return count * iconSize + (count - 1) * spacing + (2 * EndPadding(iconSize, peak));
    }

    /// <summary>
    /// Tổng độ dày cần thiết theo phương vuông góc với trục Dock (DIP): độ dày chưa phóng đại + phần giãn nở phóng đại.
    /// </summary>
    /// <remarks>
    /// Vùng chứa dự phòng không gian theo độ dày này (Sửa đổi 3: dự phòng không gian phóng đại khi chạm trần),
    /// đảm bảo biểu tượng không bị cửa sổ cắt xén khi phóng đại cực đại.
    /// </remarks>
    public static double RequiredThickness(double iconSize, double peak, double verticalPadding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(verticalPadding, 0);

        return iconSize * peak + verticalPadding * 2;
    }

    /// <summary>
    /// Tâm tĩnh của ô thứ <paramref name="index"/> (<strong>tọa độ canvas</strong>, DIP).
    /// </summary>
    /// <remarks>
    /// Đã bao gồm phần chừa trống hai đầu (<see cref="EndPadding"/>): dịch toàn bộ hàng vào trong một khoảng `EndPadding` trước,
    /// để khi phóng đại cực đại thì ô đầu và ô cuối không vượt ra ngoài canvas. Cả bước vẽ và kiểm tra chạm đều lấy tọa độ này làm chuẩn,
    /// do đó bên gọi không cần (và không nên) tự cộng thêm độ lệch nữa.
    /// </remarks>
    public static double SlotCenter(int index, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return EndPadding(iconSize, peak) + (index * (iconSize + spacing)) + (iconSize / 2);
    }

    /// <summary>
    /// Đường cong suy giảm độ phóng đại: tại khoảng cách 0 đạt 1 (phóng đại tối đa), ngoài một bước khoảng cách thì suy giảm mượt về 0.
    /// </summary>
    /// <remarks>
    /// Dùng dạng chuông cosin thay vì tam giác tuyến tính: suy giảm mượt mà, hoạt ảnh không bị gãy khúc giữa các ô lân cận.
    /// Bán kính ảnh hưởng đặt bằng đúng một bước khoảng cách (pitch) — khi con trỏ đi đến tâm khe, ô sát cạnh trở về kích thước gốc để tránh che khuất.
    /// </remarks>
    private static double Falloff(double distance, double iconSize, double spacing)
    {
        var pitch = iconSize + spacing;
        var radius = pitch;

        if (distance >= radius)
        {
            return 0;
        }

        var t = distance / radius;
        return 0.5 * (1 + Math.Cos(t * Math.PI));
    }
}
