using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glassquid.Dock.Tests;

public class DockTileAccessibilityTests
{
    [Fact]
    public void TileVisualExposesStableIdentityAndFullName()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetTiles([new DockTileModel { EntryId = "test-identity", DisplayName = "Tên đầy đủ tiếng Việt", Glyph = "?" }]);
            strip.Measure(new Size(500, 200));
            strip.Arrange(new Rect(0, 0, 500, 200));
            strip.UpdateLayout();
            var body = Descendants(strip).OfType<TextBlock>().Single(x => x.Text == "?");
            Assert.Equal("Tên đầy đủ tiếng Việt", AutomationProperties.GetName(body));
            Assert.Equal("dock-tile:test-identity", AutomationProperties.GetAutomationId(body));
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
