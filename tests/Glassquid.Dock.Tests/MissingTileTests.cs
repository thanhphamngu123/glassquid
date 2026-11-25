using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glassquid.Dock.Tests;

public class MissingTileTests
{
    [Fact]
    public void InvalidEntryWithoutCachedIconShouldAppearDimmed()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetTiles([new DockTileModel { EntryId = "missing", DisplayName = "missing", IsMissing = true }]);
            var window = new Window { Content = strip, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout();
                var fallback = Children(strip).OfType<TextBlock>().Single(x => x.Text == "?");
                Assert.True(fallback.Opacity < 1, "Biểu tượng giữ chỗ không hợp lệ chưa được làm mờ");
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
