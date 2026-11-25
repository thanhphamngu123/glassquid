using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glassquid.Launcher;
using Glassquid.UI.Theming;

internal static partial class Program
{
    private static void VerifyPageSlide(LauncherWindow window, UniformGrid grid, string evidenceDirectory)
    {
        var outgoing = window.FindName("OutgoingPageGrid") as UniformGrid;
        var incomingMotion = grid.RenderTransform as TranslateTransform;
        Check("Chuyển trang cung cấp lớp chuyển tiếp theo chiều ngang", "L-08", outgoing is not null && incomingMotion is not null,
            "Biểu tượng thật trượt ra và trượt vào đồng thời", $"Lớp trang cũ {outgoing is not null}, biến đổi ngang {incomingMotion is not null}");
        if (outgoing is null || incomingMotion is null) return;
        var outgoingMotion = (TranslateTransform)outgoing.RenderTransform;
        var next = (Button)window.FindName("NextPage")!;
        var previous = (Button)window.FindName("PreviousPage")!;
        var pageText = (TextBlock)window.FindName("PageText")!;
        var search = (TextBox)window.FindName("SearchBox")!;
        bool Settled() => outgoing.Children.Count == 0 && Math.Abs(incomingMotion.X) < .01;

        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var initialX = incomingMotion.X;
        Check("Trang sau đi vào từ bên phải và giữ nguyên trang cũ", "L-08",
            initialX > 0 && outgoing.Children.Count == 35 && grid.Children.Count == 1 && pageText.Text == "2 / 2",
            "Trang mới ở bên phải, trang cũ 35 mục, trang mới 1 mục", $"Độ lệch {initialX:0.0}, trang cũ {outgoing.Children.Count}, trang mới {grid.Children.Count}");
        Pump(TimeSpan.FromMilliseconds(80));
        Check("Giữa quá trình sang trang sau cả hai trang cùng dịch sang trái", "L-08",
            incomingMotion.X > 0 && incomingMotion.X < initialX && outgoingMotion.X < 0,
            "Trang mới dần trượt vào, trang cũ trượt ra sang trái", $"Trang mới {incomingMotion.X:0.0}, trang cũ {outgoingMotion.X:0.0}");
        Capture(window, System.IO.Path.Combine(evidenceDirectory, "launcher-page-slide.png"));
        Check("Hoàn tất chuyển trang giải phóng trang cũ và khôi phục vị trí", "L-08", PumpUntil(Settled, TimeSpan.FromSeconds(1)),
            "Giải phóng trang cũ, độ dịch về 0", $"Trang cũ {outgoing.Children.Count}, độ dịch {incomingMotion.X:0.0}");

        window.NavigatePage(1);
        Check("Chuyển tiếp ở trang cuối không tạo hiệu ứng động", "L-08", Settled() && pageText.Text == "2 / 2",
            "Giữ nguyên trang cuối", pageText.Text);
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("Trang trước đi vào từ bên trái", "L-08", incomingMotion.X < 0 && outgoing.Children.Count == 1 && grid.Children.Count == 35,
            "Trang mới ở bên trái, trang cũ 1 mục", $"Độ lệch {incomingMotion.X:0.0}, trang cũ {outgoing.Children.Count}");
        var midPageClock = System.Diagnostics.Stopwatch.StartNew();
        Pump(TimeSpan.FromMilliseconds(60));
        Check("Giữa quá trình về trang trước trang cũ thoát sang phải", "L-08", incomingMotion.X < 0 && outgoingMotion.X > 0,
            "Cả hai trang dịch sang phải", $"Trang mới {incomingMotion.X:0.0}, trang cũ {outgoingMotion.X:0.0}, chờ lấy mẫu {midPageClock.Elapsed.TotalMilliseconds:F1}ms");
        PumpUntil(Settled, TimeSpan.FromSeconds(1));

        window.NavigatePage(1);
        window.NavigatePage(-1);
        Check("Chuyển trang đảo chiều liên tiếp nội dung cuối cùng chính xác và không còn tàn dư", "L-08",
            PumpUntil(Settled, TimeSpan.FromSeconds(1)) && grid.Children.Count == 35 && pageText.Text == "1 / 2",
            "35 mục, 1 / 2, không còn tàn dư trang cũ", $"{grid.Children.Count} mục, {pageText.Text}, trang cũ {outgoing.Children.Count}");

        window.NavigatePage(1);
        search.Text = "qidongtai";
        Check("Tìm kiếm ngắt chuyển trang và hiển thị ngay kết quả lọc", "L-03/L-08", Settled() && grid.Children.Count == 1 && pageText.Text == "1 / 1",
            "Xóa trang cũ và hiển thị kết quả tìm kiếm", $"Trang cũ {outgoing.Children.Count}, {pageText.Text}");
        search.Clear();
        window.NavigatePage(-1);
        PumpUntil(Settled, TimeSpan.FromSeconds(1));
        var appearance = ThemeService.Current!.Appearance;
        var originalReduceMotion = appearance.ReduceMotion;
        try
        {
            appearance.ReduceMotion = true;
            ThemeService.Current.Refresh();
            window.NavigatePage(1);
            Check("Chuyển trang trực tiếp khi bật thiết lập giảm hiệu ứng động", "L-08", Settled() && grid.Children.Count == 1 && pageText.Text == "2 / 2",
                "Không giữ lại lớp hiệu ứng động", $"Trang cũ {outgoing.Children.Count}, {pageText.Text}");
            window.NavigatePage(-1);
        }
        finally
        {
            appearance.ReduceMotion = originalReduceMotion;
            ThemeService.Current.Refresh();
        }
        window.NavigatePage(1);
        window.Hide();
        Check("Xóa hiệu ứng chuyển trang khi ẩn Launchpad", "L-08", Settled(),
            "Giải phóng trang cũ, độ dịch về 0", $"Trang cũ {outgoing.Children.Count}, độ dịch {incomingMotion.X:0.0}");
        window.Show();
        window.NavigatePage(-1);
        PumpUntil(Settled, TimeSpan.FromSeconds(1));
        Pump(TimeSpan.FromMilliseconds(60));
    }
}
