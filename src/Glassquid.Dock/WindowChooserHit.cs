using System.Windows;
using System.Windows.Controls;

namespace Glassquid.Dock;

/// <summary>
/// Tìm ra handle cửa sổ nằm trong mục danh sách chứa "phần tử nguồn" của một lượt nhấp chuột.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Không được chỉ đọc <c>OriginalSource.Tag</c>.</strong>
/// Handle được gắn trên <see cref="ListBoxItem"/>, trong khi <c>OriginalSource</c> của lượt nhấp thường là
/// khối chữ hoặc phần tử chứa nội dung bên trong — bản thân nó không có Tag, nên trình xử lý sẽ im lặng không làm gì cả:
/// danh sách nhìn tưởng bấm được nhưng bấm vào không có phản ứng gì và cũng không báo lỗi. Thực tế đây là loại lỗi dễ bị bỏ sót nhất trong dự án này.
/// </para>
/// <para>
/// Cách làm đúng là tìm ngược lên phần tử chứa mục danh sách tương ứng (<see cref="ItemsControl.ContainerFromElement(ItemsControl, DependencyObject)"/>
/// chính là API được cung cấp cho việc này), rồi mới lấy handle từ phần tử chứa đó.
/// </para>
/// </remarks>
public static class WindowChooserHit
{
    /// <summary>Tìm handle cửa sổ tương ứng với lượt nhấp này; trả về 0 nếu không tìm thấy.</summary>
    public static nint Resolve(ItemsControl items, object? originalSource)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (originalSource is not DependencyObject source)
        {
            return 0;
        }

        // Kiểm tra chính phần tử nguồn trước (khi vị trí nhấp trúng ngay mục danh sách), sau đó mới tìm ngược lên phần tử chứa.
        if (source is ListBoxItem own && TryRead(own, out var direct))
        {
            return direct;
        }

        if (ItemsControl.ContainerFromElement(items, source) is ListBoxItem container && TryRead(container, out var handle))
        {
            return handle;
        }

        return 0;
    }

    private static bool TryRead(ListBoxItem item, out nint handle)
    {
        if (item.Tag is nint value && value != 0)
        {
            handle = value;
            return true;
        }

        handle = 0;
        return false;
    }
}