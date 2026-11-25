using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyIosDrag(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("Phiên kéo thả thời gian thực có thể nhấc biểu tượng lên", "IOS-01", prepare is not null, "Hỗ trợ kéo thả thời gian thực", prepare is null ? "Thiếu cài đặt" : "Đã cài đặt");
        if (prepare is null) return;
        var preview = typeof(LauncherWindow).GetMethod("PreviewIconPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var tile = (Button)grid.Children[0];
        var reused = (Button)grid.Children[1];
        prepare.Invoke(window, [tile, new Point(tile.ActualWidth / 2, tile.ActualHeight / 2)]);
        var target = new Point(grid.ActualWidth / 7 * 3.5, grid.ActualHeight / 5 * .5);
        preview.Invoke(window, [target, false]);
        Check("Xem trước khi kéo không ghi xuống đĩa", "IOS-03", original.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "Tệp không thay đổi", "Đối chiếu từng byte");
        var displacementStart = reused.TranslatePoint(new Point(0, 0), grid);
        Pump(TimeSpan.FromMilliseconds(70));
        var displacementMiddle = reused.TranslatePoint(new Point(0, 0), grid);
        Check("Nhường chỗ có hoạt ảnh dịch chuyển thực tế", "IOS-04", (displacementMiddle - displacementStart).Length > 1,
            "Biểu tượng di chuyển từng khung hình hơn 1 DIP", (displacementMiddle - displacementStart).Length.ToString());
        Pump(TimeSpan.FromMilliseconds(280));
        var visibleOrder = VisualTileOrder(grid);
        Check("Trước khi thả tay, các biểu tượng đã lần lượt nhường chỗ", "IOS-02", Equals(visibleOrder[0].Tag, "wpf-01")
            && Equals(visibleOrder[3].Tag, "wpf-00") && ReferenceEquals(visibleOrder[0], reused),
            "01, 02, 03, 00, tái sử dụng biểu tượng hiện có", string.Join(",", visibleOrder.Take(4).Select(b => b.Tag)));
        Capture(window, Path.Combine(evidence, "live-insertion.png"));
        window.NavigatePage(1);
        Pump(TimeSpan.FromMilliseconds(280));
        preview.Invoke(window, [new Point(grid.ActualWidth / 14, grid.ActualHeight / 10), true]);
        Check("Chuyển trang nhanh vẫn giữ thao tác kéo và hoán đổi theo thời gian thực", "IOS-05", Equals(((Button)grid.Children[0]).Tag, "wpf-00"), "Ô đầu tiên trang hai là biểu tượng đang được kéo", ((Button)grid.Children[0]).Tag?.ToString() ?? "Rỗng");
        var payload = new DataObject("Glassquid.Launcher.Slot", "wpf-00");
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [payload, DragDropKeyStates.AltKey, DragDropEffects.Move, grid, new Point(1, 1)], culture: null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        grid.RaiseEvent(drop);
        finish.Invoke(window, null);
        Pump(TimeSpan.FromMilliseconds(280));
        var saved = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
        Check("Sau khi thả tay hoán đổi qua trang sẽ lưu đúng thứ tự", "IOS-06", saved[0] == "wpf-35" && saved[35] == "wpf-00" && saved.Distinct().Count() == 36,
            "35 và 00 hoán đổi, các biểu tượng còn lại giữ nguyên vị trí", $"Mục đầu {saved[0]}, mục cuối {saved[35]}");
        Check("Sau khi thả tay, xóa biểu tượng nổi và hiển thị biểu tượng đã khớp vị trí", "IOS-07", ((Canvas)window.FindName("DragOverlay")!).Children.Count == 0
            && ((Button)grid.Children[0]).Opacity == 1, "Lớp nổi đã trống và biểu tượng hiển thị", $"Lớp nổi {((Canvas)window.FindName("DragOverlay")!).Children.Count}");
        var savedBytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        window.NavigatePage(-1);
        Pump(TimeSpan.FromMilliseconds(280));
        prepare.Invoke(window, [(Button)grid.Children[0], new Point(20, 20)]);
        preview.Invoke(window, [target, false]);
        finish.Invoke(window, null);
        Pump(TimeSpan.FromMilliseconds(280));
        Check("Hủy bỏ khôi phục thứ tự gốc và tệp bố cục", "IOS-08", Equals(((Button)grid.Children[0]).Tag, "wpf-35")
            && savedBytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "Thứ tự gốc và tệp không đổi", ((Button)grid.Children[0]).Tag?.ToString() ?? "Rỗng");
        var reloaded = new GlassquidDataStore(data.Paths);
        reloaded.LoadAll();
        Check("Mở lại bố cục vẫn giữ kết quả hoán đổi", "IOS-09", LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).SequenceEqual(saved), "Khớp với thứ tự đã lưu", "Kiểm tra toàn bộ Id");
        prepare.Invoke(window, [(Button)grid.Children[0], new Point(20, 20)]);
        window.NavigatePage(1);
        var duringSlide = false;
        try
        {
            typeof(LauncherWindow).GetMethod("PlaceIconAtPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new Point(20, 20), true, true]);
            duringSlide = Equals(((Button)grid.Children[0]).Tag, "wpf-35");
        }
        catch (TargetInvocationException) { }
        Check("Thả tay trước khi hoạt ảnh lật trang kết thúc vẫn khớp vị trí được", "IOS-10", duringSlide, "Trang mới xuất hiện biểu tượng đang được kéo", duringSlide.ToString());
        finish.Invoke(window, null);
        Capture(window, Path.Combine(evidence, "settled-grid.png"));
    }
}
