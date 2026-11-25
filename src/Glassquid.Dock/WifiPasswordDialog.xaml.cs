using System.Windows;
using System.Windows.Input;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

public partial class WifiPasswordDialog : GlassWindow
{
    public string Password => WifiPasswordBox.Password;

    public WifiPasswordDialog(string networkName)
    {
        InitializeComponent();
        NetworkNameText.Text = networkName;
        PanelCornerRadius = new CornerRadius(28);
        GlassSurface.IsBackdropActive = () => IsVisible;
        GlassSurface.SuppressAmbientFallback = true;
        Loaded += (_, _) => WifiPasswordBox.Focus();
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(28, appearance.DockCornerRadius);
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(WifiPasswordBox.Password))
        {
            ValidationText.Visibility = Visibility.Visible;
            WifiPasswordBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
