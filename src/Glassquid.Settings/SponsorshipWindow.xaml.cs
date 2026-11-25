using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Glassquid.UI.Controls;

namespace Glassquid.Settings;

/// <summary>Chỉ hiển thị mã nhận tiền của phương thức thanh toán đã chọn sau khi người dùng nhấn tài trợ.</summary>
public partial class SponsorshipWindow : GlassWindow
{
    protected override bool UseOpaquePanel => true;

    public SponsorshipWindow(bool useWechat, ImageSource? code)
    {
        InitializeComponent();
        var method = useWechat ? "WeChat Pay" : "Alipay";
        Title = "Tài trợ qua " + method;
        AutomationProperties.SetName(this, Title);
        SponsorDialogHeading.Text = Title;
        SponsorCodeImage.Source = code;
        SponsorCodeImage.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        SponsorCodeUnavailable.Visibility = code is null ? Visibility.Visible : Visibility.Collapsed;
        SponsorCodeUnavailable.Text = $"Chưa có mã nhận tiền {method}\nVẫn có thể sử dụng miễn phí toàn bộ tính năng";
        SponsorInstructions.Text = code is null
            ? $"Tính năng tài trợ qua {method} hiện chưa mở, cảm ơn sự ủng hộ và thấu hiểu của bạn."
            : $"Vui lòng dùng chức năng quét mã của {method} để quét mã nhận tiền bên dưới.";
        AutomationProperties.SetName(SponsorCodeImage, "Mã nhận tiền tài trợ " + method);
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Mouse.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
