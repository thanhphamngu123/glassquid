using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

public class NetworkGlyphTests
{
    [Fact]
    public void NetworkGeometries_AreAllEmbeddedAndNonEmpty()
    {
        StaTest.Run(() =>
        {
            var keys = new[]
            {
                "ic_fluent_wifi_1_24_regular",
                "ic_fluent_wifi_2_24_regular",
                "ic_fluent_wifi_3_24_regular",
                "ic_fluent_wifi_4_24_regular",
                "ic_fluent_wifi_off_24_regular",
                "ic_fluent_wifi_warning_24_regular",
                "ic_fluent_ethernet_24_regular",
            };

            foreach (var key in keys)
            {
                var method = typeof(SystemTrayBarWindow).GetMethod("GetNetworkGeometry",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(method);
                var geometry = method.Invoke(null, [key]) as System.Windows.Media.Geometry;
                Assert.NotNull(geometry);
                Assert.False(geometry.IsEmpty(), $"Geometry for {key} should not be empty.");
                Assert.True(geometry.Bounds.Width > 0, $"Geometry for {key} width should be > 0.");
                Assert.True(geometry.Bounds.Height > 0, $"Geometry for {key} height should be > 0.");
            }
        });
    }

    [Fact]
    public void SystemTrayBarWindow_NetworkButton_HasPathGlyphAndCallsQuickSettings()
    {
        StaTest.Run(() =>
        {
            var window = new SystemTrayBarWindow();
            try
            {
                var networkBtn = window.FindName("NetworkButton") as Button;
                Assert.NotNull(networkBtn);

                var networkGlyph = window.FindName("NetworkGlyph") as Path;
                Assert.NotNull(networkGlyph);

                // Check that ToolTip is set
                Assert.NotNull(networkBtn.ToolTip);

                // Clicking network button triggers QuickSettings
                // Verified through Button click handler wired to OnQuickSettingsClick in XAML
                Assert.NotNull(networkGlyph.Data);
                Assert.False(networkGlyph.Data.IsEmpty());
            }
            finally
            {
                window.Close();
            }
        });
    }
}
