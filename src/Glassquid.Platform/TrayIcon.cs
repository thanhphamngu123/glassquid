using Glassquid.Platform.Interop;

namespace Glassquid.Platform;

/// <summary>Ý nghĩa của thông điệp được gửi tới cửa sổ callback của khay hệ thống.</summary>
public enum TrayEvent
{
    /// <summary>Không phải thông báo khay hệ thống mà chúng ta quan tâm.</summary>
    None = 0,

    /// <summary>Nhấp chuột trái: người dùng muốn mở giao diện chính.</summary>
    Activate = 1,

    /// <summary>Nhấp chuột phải: hiển thị menu ngữ cảnh.</summary>
    ContextMenu = 2,

    /// <summary>Nhấp đúp.</summary>
    DoubleClick = 3,
}

/// <summary>
/// Giữ một biểu tượng ở vùng thông báo.
/// </summary>
/// <remarks>
/// shell gửi đầu vào của khay hệ thống dưới dạng thông điệp cửa sổ thông thường,
/// nên bên gọi cung cấp cửa sổ chủ và định tuyến thông điệp của nó tới <see cref="Classify"/>.
/// Đặt quyền sở hữu cửa sổ ở nơi khác giúp lớp này dễ kiểm thử,
/// đồng thời cho phép máy chủ chọn một cửa sổ chỉ nhận thông điệp không bao giờ hiển thị trên màn hình.
///
/// Handle biểu tượng thuộc quyền sở hữu của thực thể này và sẽ bị hủy khi <see cref="Dispose"/>.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Định danh được shell sử dụng khi gửi thông điệp của biểu tượng này tới cửa sổ chủ.</summary>
    public const int CallbackMessage = Shell32.WmTrayCallback;

    private readonly nint _ownerWindow;
    private readonly int _id;

    private nint _icon;
    private string _tooltip = string.Empty;
    private bool _added;
    private bool _disposed;

    public TrayIcon(nint ownerWindow, int id = 1)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("Biểu tượng khay hệ thống cần một handle cửa sổ hợp lệ.", nameof(ownerWindow));
        }

        _ownerWindow = ownerWindow;
        _id = id;
    }

    /// <summary>Trả về true sau khi biểu tượng thực sự đã xuất hiện ở vùng thông báo.</summary>
    public bool IsVisible => _added;

    /// <summary>
    /// Thêm biểu tượng vào vùng thông báo.
    /// </summary>
    /// <param name="icon">Handle biểu tượng. Quyền sở hữu được chuyển giao cho thực thể này.</param>
    public bool Show(nint icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TakeOwnership(icon);
        _tooltip = tooltip;

        var data = CreateData();
        _added = Shell32.Shell_NotifyIcon(Shell32.NimAdd, ref data);

        if (_added)
        {
            // Phiên bản 3 giúp shell báo cáo thao tác nhấp trong lParam thay vì gửi thông điệp nhấn nút truyền thống,
            // Classify bên dưới phân tích chính xác theo kỳ vọng này.
            var version = CreateData();
            version.VersionOrTimeout = Shell32.NotifyIconVersion3;
            Shell32.Shell_NotifyIcon(Shell32.NimSetVersion, ref version);
        }

        return _added;
    }

    /// <summary>Thay thế đồ họa hoặc văn bản chú giải của biểu tượng đang hiển thị trên màn hình.</summary>
    public bool Update(nint icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_added)
        {
            return Show(icon, tooltip);
        }

        TakeOwnership(icon);
        _tooltip = tooltip;

        var data = CreateData();
        return Shell32.Shell_NotifyIcon(Shell32.NimModify, ref data);
    }

    /// <summary>Gỡ biểu tượng khỏi vùng thông báo.</summary>
    public void Hide()
    {
        if (!_added || _disposed)
        {
            return;
        }

        var data = CreateData();
        Shell32.Shell_NotifyIcon(Shell32.NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();

        // Giải phóng handle biểu tượng mà chúng ta đang giữ; shell đã sao chép nó.
        if (_icon != 0)
        {
            IconFactory.DestroyIcon(_icon);
            _icon = 0;
        }

        _disposed = true;
    }

    /// <summary>
    /// Phân tích <c>lParam</c> mà shell gửi cho thông báo khay hệ thống.
    /// </summary>
    /// <remarks>
    /// Theo ngữ nghĩa phiên bản 3, sự kiện chuột nằm ở word thấp của lParam.
    /// Word cao mang id biểu tượng, ở đây không dùng đến.
    /// </remarks>
    public static TrayEvent Classify(int lParam)
    {
        var mouseMessage = lParam & 0xFFFF;

        return mouseMessage switch
        {
            Shell32.WmLButtonUp => TrayEvent.Activate,
            Shell32.WmRButtonUp => TrayEvent.ContextMenu,
            Shell32.WmLButtonDblClk => TrayEvent.DoubleClick,
            _ => TrayEvent.None,
        };
    }

    private void TakeOwnership(nint icon)
    {
        if (_icon == icon)
        {
            return;
        }

        if (_icon != 0)
        {
            IconFactory.DestroyIcon(_icon);
        }

        _icon = icon;
    }

    private unsafe Shell32.NotifyIconData CreateData()
    {
        var data = new Shell32.NotifyIconData
        {
            Size = sizeof(Shell32.NotifyIconData),
            WindowHandle = _ownerWindow,
            Id = _id,
            Flags = Shell32.NifMessage | Shell32.NifIcon | Shell32.NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _icon,
        };

        Shell32.NotifyIconData.SetText(data.Tip, Shell32.TipLength, _tooltip);
        return data;
    }
}