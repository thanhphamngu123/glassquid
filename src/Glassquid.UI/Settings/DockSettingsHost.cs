using System.Windows.Threading;
using Glassquid.Core.Models.Settings;

namespace Glassquid.UI.Settings;

/// <summary>
/// Nguồn chân lý duy nhất và điểm thông báo thay đổi cho cài đặt Dock.
/// </summary>
/// <remarks>
/// Theo tiền lệ của <see cref="Glassquid.UI.Theming.ThemeService"/>, lớp này được thiết kế cố ý ở cấp tiến trình:
/// chỉ có một bộ cài đặt Dock đang có hiệu lực, cửa sổ Dock và giao diện cài đặt sau này phải nhìn thấy cùng một bản.
///
/// Lý do cần có nó: cài đặt có hiệu lực tức thì (danh mục tính năng D-16/D-17) nghĩa là hễ cài đặt thay đổi,
/// Dock đang mở phải thay đổi theo ngay. Thông báo được **đẩy** đi từ đây,
/// Dock không được tự thăm dò (poll) tệp cài đặt.
/// </remarks>
public sealed class DockSettingsHost
{
    private readonly Dispatcher _dispatcher;

    public DockSettingsHost(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        Settings = new DockSettings();
    }

    /// <summary>Host cài đặt Dock duy nhất trong tiến trình này.</summary>
    public static DockSettingsHost? Current { get; private set; }

    /// <summary>Tạo và cài đặt thực thể cấp tiến trình.</summary>
    public static DockSettingsHost Initialize(Dispatcher dispatcher)
    {
        Current = new DockSettingsHost(dispatcher);
        return Current;
    }

    /// <summary>Ảnh chụp cài đặt đang có hiệu lực. Chỉ được thay thế toàn bộ khi gọi <see cref="Update"/>.</summary>
    public DockSettings Settings { get; private set; }

    /// <summary>Kích hoạt trên luồng UI sau khi cài đặt thay đổi.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Áp dụng cài đặt mới và thông báo cho mọi bên lắng nghe.
    /// </summary>
    /// <remarks>
    /// Khác với chủ đề, ở đây không có trạng thái phái sinh nào cần tính toán lại trước——bản thân giá trị cài đặt có hiệu lực ngay,
    /// nên bất kể nội dung có đổi hay không đều thông báo trực tiếp để bên lắng nghe quyết định làm mới phần nào.
    /// </remarks>
    public void Update(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        NotifyChanged();
    }

    /// <summary>
    /// Gọi sau khi đối tượng cài đặt bị sửa tại chỗ: không thay thế tham chiếu, chỉ gửi bổ sung một thông báo.
    /// </summary>
    public void Refresh() => NotifyChanged();

    private void NotifyChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Nếu đã ở trên luồng UI thì kích hoạt trực tiếp để đỡ một lượt điều phối thừa;
        // khi gọi từ luồng khác thì chuyển sang luồng UI để bên lắng nghe yên tâm thao tác với giao diện.
        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(NotifyChanged);
        }
    }
}
