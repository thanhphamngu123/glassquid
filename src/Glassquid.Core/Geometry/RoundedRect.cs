namespace Glassquid.Core.Geometry;

/// <summary>
/// Phán đoán hình dạng của hình chữ nhật bo góc.
/// </summary>
/// <remarks>
/// Đặt ở Core thay vì tầng giao diện vì đây là toán học thuần túy: vừa là căn cứ để vẽ góc bo,
/// vừa là căn cứ để kiểm tra va chạm (hit-test), cả hai nơi phải dùng cùng một logic phán đoán. Đặt ở đây còn được unit test bao phủ trực tiếp
/// mà không cần khởi chạy giao diện.
///
/// Lý do cần đến nó: đo thực tế ở giai đoạn 2 cho thấy bóng đổ sẽ kết xuất các pixel bán trong suốt tại vùng góc bo bị cắt bỏ,
/// khiến bốn góc hình chữ nhật của cửa sổ dù nhìn trống nhưng vẫn chặn chuột. Giao phán đoán cho hàm này
/// sẽ cho phép các điểm đó đi qua trong quá trình hit-test.
/// </remarks>
public static class RoundedRect
{
    /// <summary>
    /// Kiểm tra xem một điểm có nằm bên trong hình chữ nhật bo góc hay không.
    /// </summary>
    /// <param name="x">Hoành độ tương đối so với góc trên bên trái hình chữ nhật.</param>
    /// <param name="y">Tung độ tương đối so với góc trên bên trái hình chữ nhật.</param>
    /// <param name="width">Chiều rộng hình chữ nhật.</param>
    /// <param name="height">Chiều cao hình chữ nhật.</param>
    /// <param name="radius">Bán kính bo góc. Nếu vượt quá một nửa cạnh ngắn thì xử lý bằng một nửa cạnh ngắn.</param>
    /// <remarks>
    /// Điểm nằm trên biên được tính là bên trong: sai lệch một pixel ở mép không nên làm cú nhấp rơi sang cửa sổ khác.
    /// </remarks>
    public static bool Contains(double x, double y, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (x < 0 || y < 0 || x > width || y > height)
        {
            return false;
        }

        // Bán kính không thể vượt quá một nửa cạnh ngắn, nếu không hai góc bo sẽ đè xuyên vào nhau và phép kiểm tra không còn là hình chữ nhật bo góc nữa.
        var effective = Math.Min(radius, Math.Min(width, height) / 2);
        if (effective <= 0)
        {
            return true;
        }

        // Chỉ khi rơi vào vùng vuông ở bốn góc mới cần kiểm tra cung tròn, các vị trí còn lại chắc chắn nằm bên trong.
        var left = x < effective;
        var right = x > width - effective;
        var top = y < effective;
        var bottom = y > height - effective;

        if ((!left && !right) || (!top && !bottom))
        {
            return true;
        }

        var centerX = left ? effective : width - effective;
        var centerY = top ? effective : height - effective;

        var dx = x - centerX;
        var dy = y - centerY;

        // Thêm một chút dung sai để tránh sai số dấu phẩy động phán đoán nhầm điểm trên đường cung là nằm ngoài.
        const double Tolerance = 0.5;

        return (dx * dx) + (dy * dy) <= ((effective + Tolerance) * (effective + Tolerance));
    }
}