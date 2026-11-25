using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Glassquid.Settings;

public partial class SettingsWindow
{
    private void LoadTransferQr()
    {
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream(
                "Glassquid.Settings.TransferQr.tpbank-qr.png");
            if (stream is null)
            {
                TransferQrImage.Visibility = Visibility.Collapsed;
                TransferQrUnavailable.Visibility = Visibility.Visible;
                return;
            }

            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();
            TransferQrImage.Source = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            TransferQrImage.Visibility = Visibility.Collapsed;
            TransferQrUnavailable.Visibility = Visibility.Visible;
        }
    }
}
