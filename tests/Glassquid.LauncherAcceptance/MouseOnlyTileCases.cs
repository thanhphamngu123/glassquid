using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using Glassquid.Launcher;
using Glassquid.UI.Controls;

internal static partial class Program
{
    private static void VerifyMouseOnlyTiles(LauncherWindow window, UniformGrid grid, TextBox search)
    {
        var groupPanel = (PhoneBorder)window.FindName("GroupPanel")!;
        var originalTiles = grid.Children.Cast<Button>().ToArray();
        search.Focus();
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Down)
        { RoutedEvent = Keyboard.KeyDownEvent });
        Check("Phím mũi tên trong ô tìm kiếm không đi vào biểu tượng ứng dụng", "L-MOUSE", Keyboard.FocusedElement == search
            && originalTiles.SequenceEqual(grid.Children.Cast<Button>()),
            "Ô tìm kiếm giữ nguyên tiêu điểm và lưới ứng dụng không bị dựng lại", $"Tiêu điểm {Keyboard.FocusedElement?.GetType().Name}; Tái sử dụng lưới {originalTiles.SequenceEqual(grid.Children.Cast<Button>())}");
        var tile = (Button)grid.Children[0];
        Check("Biểu tượng ứng dụng không thể nhận tiêu điểm bàn phím", "L-MOUSE", !tile.Focus() && !tile.IsTabStop,
            "Biểu tượng không thể nhận tiêu điểm hoặc được chọn qua Tab", $"Focusable {tile.Focusable}; TabStop {tile.IsTabStop}");
        foreach (var key in new[] { Key.Right, Key.Enter, Key.Space })
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Check("Phím mũi tên và phím xác nhận không mở ứng dụng hoặc nhóm", "L-MOUSE", groupPanel.Visibility == Visibility.Collapsed,
            "Nhóm vẫn đóng", groupPanel.Visibility.ToString());
        ((Button)grid.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RefreshLibrary();
        tile = (Button)grid.Children[0];
        tile.ApplyTemplate();
        var chrome = (PhoneBorder)tile.Template.FindName("TileChrome", tile);
        Check("Sau khi nhấp chuột và làm mới, biểu tượng không có viền cố định", "L-MOUSE", chrome.BorderBrush is SolidColorBrush { Color.A: 0 },
            "Viền trong suốt", chrome.BorderBrush?.ToString() ?? "Không có viền");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
        { RoutedEvent = Keyboard.KeyDownEvent });
        Check("Launchpad không còn thao tác nhóm bằng phím Esc", "L-MOUSE", groupPanel.Visibility == Visibility.Visible,
            "Nhóm vẫn mở, dùng chuột để quay lại", groupPanel.Visibility.ToString());
        FindVisual<Button>(groupPanel).Single(button => Equals(button.Content, "Quay lại"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        tile = (Button)grid.Children[0];
        Pump(TimeSpan.FromMilliseconds(250));
        var iconHost = ((StackPanel)tile.Content).Children[0] as Border;
        var pressedKey = (DependencyPropertyKey)typeof(ButtonBase)
            .GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        tile.SetValue(pressedKey, true);
        Pump(TimeSpan.FromMilliseconds(100));
        Check("Khi nhấn chỉ thu nhỏ nhẹ biểu tượng hiện tại", "L-MOUSE", iconHost?.RenderTransform is ScaleTransform { ScaleX: < 1 and >= .95, ScaleY: < 1 and >= .95 },
            "Biểu tượng hiện tại thu nhỏ nhẹ", iconHost?.RenderTransform.ToString() ?? "Không có khung chứa biểu tượng");
        tile.SetValue(pressedKey, false);
        Pump(TimeSpan.FromMilliseconds(300));
        Check("Sau khi thả, biểu tượng khôi phục và không còn hoạt ảnh tồn đọng", "L-MOUSE", iconHost?.RenderTransform is ScaleTransform { ScaleX: 1, ScaleY: 1, HasAnimatedProperties: false },
            "Kích thước ban đầu và đồng hồ hoạt ảnh đã kết thúc", iconHost?.RenderTransform is ScaleTransform motion
                ? $"X {motion.ScaleX}; Y {motion.ScaleY}; Animated {motion.HasAnimatedProperties}"
                : "Không có khung chứa biểu tượng");
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisual<T>(child)) yield return nested;
        }
    }
}
