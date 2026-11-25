using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glassquid.Settings;

public partial class SettingsWindow
{
    private ImageSource? _alipaySponsorCode;
    private ImageSource? _wechatSponsorCode;

    private void LoadSponsorshipCodes() => SetSponsorshipCodes(
        LoadSponsorshipCode("alipay.jpg") ?? LoadSponsorshipCode("alipay.png"), LoadSponsorshipCode("wechat.png"));

    private static BitmapFrame? LoadSponsorshipCode(string fileName)
    {
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream(
                "Glassquid.Settings.Sponsorship." + fileName);
            if (stream is null) return null;
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            // Missing or unreadable payment artwork must not prevent free use of Settings.
            return null;
        }
    }

    /// <summary>Thiết lập mã QR nhận ủng hộ gốc do tác giả cung cấp; kênh không được cung cấp sẽ không hiển thị mã QR của kênh khác.</summary>
    public void SetSponsorshipCodes(ImageSource? alipay, ImageSource? wechat)
    {
        _alipaySponsorCode = alipay;
        _wechatSponsorCode = wechat;
    }

    private void OnSponsorClick(object sender, RoutedEventArgs e)
    {
        var useWechat = (FindName("SponsorWechatOption") as System.Windows.Controls.RadioButton)?.IsChecked == true;
        var dialog = new SponsorshipWindow(useWechat, useWechat ? _wechatSponsorCode : _alipaySponsorCode)
        { Owner = this };
        dialog.ShowDialog();
    }
}
