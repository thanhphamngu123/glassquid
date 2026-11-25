using System.ComponentModel;
using System.Windows;
using Glassquid.Core.Models.Settings;

namespace Glassquid.Dock;

/// <summary>
/// Dữ liệu hiển thị của một ô trên Dock.
/// </summary>
/// <remarks>
/// Được thiết kế tối giản có chủ đích: biểu tượng và kích thước được xác định lúc khởi tạo (tạo lại cả hàng khi cài đặt thay đổi),
/// còn <strong>chấm trạng thái đang chạy</strong> được làm mới riêng qua thông báo thuộc tính — sự kiện đối chiếu chỉ cập nhật chấm tròn,
/// không tạo lại ô để tránh phải tải lại biểu tượng mỗi khi cửa sổ mở hoặc đóng.
/// </remarks>
public sealed class DockTileModel : INotifyPropertyChanged
{
    private Visibility _runningVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>ID mục trong thư viện ứng dụng. Dùng để khởi chạy hoặc kích hoạt khi nhấp.</summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>Ký tự biểu tượng cho mục tích hợp sẵn (Segoe Fluent Icons). Ô thông thường có giá trị null (dùng Icon).</summary>
    public string? Glyph { get; init; }

    /// <summary>Đích đã mất hiệu lực (R-07). Biểu tượng của ô không còn hợp lệ được hiển thị mờ đi.</summary>
    public bool IsMissing { get; init; }

    /// <summary>Ứng dụng do người dùng ghim trên Dock; các mục tạm thời khi chạy không thể tham gia sắp xếp hoặc bỏ ghim.</summary>
    public bool IsPinned { get; init; } = true;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Chú giải khi di chuột (D-06): tên đầy đủ, không cắt bớt; mục mất hiệu lực có kèm giải thích.</summary>
    public string ToolTipText { get; init; } = string.Empty;

    public System.Windows.Media.ImageSource? Icon { get; init; }

    /// <summary>Màu của chấm trạng thái đang chạy. Lấy từ màu tiền cảnh của bảng màu hiện tại.</summary>
    public System.Windows.Media.Brush? DotBrush { get; init; }

    /// <summary>Kích thước cạnh biểu tượng theo DIP, lấy từ cài đặt Dock.</summary>
    public double IconSize { get; init; } = DockSettings.FixedIconSize;

    /// <summary>Lề ngoài của ô, suy ra từ thiết lập khoảng cách biểu tượng.</summary>
    public Thickness TileMargin { get; init; } = new(4, 0, 4, 0);

    /// <summary>Khả năng hiển thị của chấm trạng thái đang chạy (D-07). Sự kiện đối chiếu cập nhật riêng thuộc tính này.</summary>
    public Visibility RunningVisibility
    {
        get => _runningVisibility;
        set
        {
            if (_runningVisibility == value)
            {
                return;
            }

            _runningVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
        }
    }
}
