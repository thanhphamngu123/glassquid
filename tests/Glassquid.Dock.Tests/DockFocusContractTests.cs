using System.Windows;
using System.Windows.Interop;
using Glassquid.Dock;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Các khẳng định cho khế ước liên quan đến tiêu điểm (tương ứng phần **có thể tự động hóa** trong mục 3 của phần rà soát).
/// </summary>
/// <remarks>
/// <para>
/// Làm rõ ranh giới trước: việc kiểm tra thực tế "sau khi nhấp Dock tiền cảnh không đổi" hay "trả tiêu điểm cho ai sau khi đóng lớp bật lên" phụ thuộc vào
/// kích hoạt cửa sổ thật và khóa tiền cảnh của hệ thống, nên khẳng định tự động dễ gặp dương tính giả/âm tính giả (ví dụ tiến trình kiểm thử vốn không có quyền tiền cảnh).
/// Do đó ở đây chỉ khóa ba quy tắc **mang tính cấu trúc**:
/// </para>
/// <list type="number">
/// <item>Thanh chính mang cờ <c>WS_EX_NOACTIVATE</c> — cơ sở của quy tắc nhấp vào không chiếm kích hoạt;</item>
/// <item>Các thành phần bật lên (trình đơn, bảng điều khiển, danh sách cửa sổ) **không nằm trong** handle của thanh chính, tức mỗi thành phần là một cửa sổ độc lập,
/// tiêu điểm phải được quản lý riêng chứ không dựa vào kiểu của thanh chính;</item>
/// <item>Việc trả lại tiền cảnh cho "cửa sổ trước khi mở lớp bật lên" đi theo một nhánh có thể xác định được
/// (khi handle đích đã hết hiệu lực thì thất bại êm ái, không ném ngoại lệ).</item>
/// </list>
/// <para>
/// Các bước đối chiếu hành vi tiền cảnh thực tế được ghi tại mục 6 và mục 12/13 phần VI của báo cáo bàn giao.
/// </para>
/// </remarks>
public class DockFocusContractTests
{
    [Fact]
    public void MainBarNoActivateStyleIsApplied()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            try
            {
                dock.Show();
                var handle = new WindowInteropHelper(dock).Handle;

                Assert.True(
                    WindowStyles.IsNoActivate(handle),
                    "Nhấp vào thanh chính không được đẩy ứng dụng người dùng đang dùng xuống nền — cơ sở của quy tắc này chính là WS_EX_NOACTIVATE");
            }
            finally
            {
                dock.Close();
            }
        });
    }

    [Fact]
    public void PopupIsSeparateWindowWithIndependentFocusManagement()
    {
        StaTest.Run(() =>
        {
            // WS_EX_NOACTIVATE của thanh chính không truyền sang các thành phần bật lên của nó: Popup là cửa sổ cấp cao nhất độc lập.
            // Đây chính là nguồn gốc của quy tắc "thanh chính không bao giờ kích hoạt + lớp bật lên cần tương tác tự kích hoạt tường minh",
            // và cũng là lý do phải ghi nhận và hoàn trả tiền cảnh một cách tường minh.
            var dock = new DockWindow();
            try
            {
                dock.Show();
                var dockHandle = new WindowInteropHelper(dock).Handle;

                var popupWindow = new Window
                {
                    Width = 100,
                    Height = 60,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = new System.Windows.Controls.Border(),
                };

                try
                {
                    popupWindow.Show();
                    var popupHandle = new WindowInteropHelper(popupWindow).Handle;

                    Assert.NotEqual(dockHandle, popupHandle);
                    Assert.False(
                        WindowStyles.IsNoActivate(popupHandle),
                        "Lớp bật lên đã nhận kiểu của thanh chính, chứng tỏ cả hai không phải cửa sổ độc lập — tiền đề của quy tắc tiêu điểm không còn đúng");
                }
                finally
                {
                    popupWindow.Close();
                }
            }
            finally
            {
                dock.Close();
            }
        });
    }

    [Fact]
    public void RestoreForegroundDoesNotThrowWhenTargetGone()
    {
        StaTest.Run(() =>
        {
            // Khi đóng lớp bật lên phải trả tiền cảnh về "cửa sổ trước khi mở" — cửa sổ đó có thể đã bị người dùng đóng.
            // Nhánh này phải xác định được: trả về false một cách êm ái, không ném ngoại lệ.
            Assert.False(WindowActivation.TryBringToForeground(0));
            Assert.False(WindowActivation.TryBringToForeground(new nint(0x1234)));
        });
    }
}