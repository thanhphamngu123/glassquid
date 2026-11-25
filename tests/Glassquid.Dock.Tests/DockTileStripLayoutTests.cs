using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Thứ tự lớp vẽ và ràng buộc khung nhìn của dải ô Dock — tương ứng với lỗi 1 và lỗi 3 trong báo cáo rà soát.
/// </summary>
/// <remarks>
/// Cả hai đều yêu cầu <strong>đọc lại giá trị thực sự có hiệu lực</strong>: thứ tự lớp vẽ đọc <c>Panel.ZIndex</c>,
/// khung nhìn đọc chiều rộng thực tế sau khi bố cục. Việc chỉ đối chiếu đại lượng dẫn xuất (mảng thứ tự tính toán, tổng độ dài tính toán)
/// chính là nguyên nhân khiến chúng lọt qua vòng kiểm thử trước đó.
/// </remarks>
public class DockTileStripLayoutTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    private static List<DockTileModel> Models(int count)
        => Enumerable.Range(0, count)
            .Select(index => new DockTileModel { EntryId = $"e{index}", DisplayName = $"Mục {index}" })
            .ToList();

    [Fact]
    public void LargestScaledTileIsDrawnOnTop()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(5));

            var pointer = DockMagnification.SlotCenter(2, Size, Spacing, Peak);
            strip.PaintFrame(pointer);

            var tiles = strip.PaintedTiles;
            var topMost = tiles.OrderByDescending(tile => tile.ZIndex).First();
            var biggest = tiles.OrderByDescending(tile => tile.Scale).First();

            Assert.Equal(biggest.EntryId, topMost.EntryId);
            Assert.True(
                Math.Abs(biggest.Scale - Peak) < 0.0001,
                $"Điều kiện tiên quyết không đạt: ô dưới con trỏ không đạt tỷ lệ cực đại ({biggest.Scale})");
        });
    }

    [Fact]
    public void OverlapHitMatchesTopmostDrawnTile()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            // Con trỏ dừng trên ô thứ 1: khi phóng to nó chồng lấn với ô thứ 0.
            strip.PaintFrame(DockMagnification.SlotCenter(1, Size, Spacing, Peak));

            var tiles = strip.PaintedTiles;
            var geometry = strip.PaintedGeometry.ToArray();

            // Quét toàn bộ hàng: ở mọi vị trí có kết quả trúng khác -1, ô trúng phải là
            // ô "che phủ điểm đó và có thứ tự lớp vẽ cao nhất" — vẽ và nhận trúng không được trái ngược nhau.
            var checkedOverlaps = 0;

            for (var probe = 0.0; probe <= strip.RequiredLength; probe += 1)
            {
                var hit = strip.ResolveHit(probe);

                if (hit < 0)
                {
                    continue;
                }

                var covering = Enumerable.Range(0, tiles.Count)
                    .Where(index =>
                    {
                        var (center, scale) = geometry[index];
                        var half = Size * scale / 2;
                        return probe >= center - half && probe < center + half;
                    })
                    .ToList();

                if (covering.Count > 1)
                {
                    checkedOverlaps++;
                }

                // Ô có thứ tự lớp vẽ cao nhất trong số các ô che phủ.
                var topMostCovering = covering.OrderByDescending(index => tiles[index].ZIndex).First();

                Assert.Equal(topMostCovering, hit);
            }

            Assert.True(checkedOverlaps > 0, "Điều kiện tiên quyết của ca kiểm thử không đạt: không có vùng chồng lấn nào trên cả hàng");
        });
    }

    [Fact]
    public void ViewportCoordinatesAfterScrollApplyOffsetOnlyOnce()
    {
        StaTest.Run(() =>
        {
            const int Count = 200;

            var strip = new DockTileStrip();

            var host = new System.Windows.Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = strip,
            };

            try
            {
                host.Show();

                strip.SetMetrics(Size, Spacing, Peak, 8, 500);
                strip.SetTiles(Models(Count));
                host.UpdateLayout();

                // Cuộn 112 DIP: x=50 trên khung nhìn tương ứng với vị trí nội dung 162 (ô thứ 2),
                // chứ không phải 274 (ô thứ 4, đó là kết quả của việc cộng độ lệch hai lần).
                strip.ScrollToHorizontalOffset(112);
                host.UpdateLayout();

                Assert.Equal(112, strip.HorizontalOffset, 1);

                var expected = DockMagnification.HitTest(
                    strip.PaintedGeometry.ToArray(), Size, strip.ToContentX(50));

                Assert.Equal(2, expected);                        // Tiền đề ca kiểm thử: nội dung 162 thực sự rơi vào ô thứ 2
                Assert.Equal(2, strip.ResolveHitAtViewportX(50));  // Chỉ cộng độ lệch một lần

                // Cộng độ lệch hai lần sẽ trúng ô thứ 4 — khẳng định rõ ràng đây không phải kết quả ta nhận được.
                var doubled = DockMagnification.HitTest(
                    strip.PaintedGeometry.ToArray(), Size, 50 + (112 * 2));

                Assert.Equal(4, doubled);
                Assert.NotEqual(doubled, strip.ResolveHitAtViewportX(50));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void MaxWidthActuallyConstrainsViewportRatherThanOnlyScrollFlag()
    {
        StaTest.Run(() =>
        {
            const double Cap = 500;
            const int Count = 200;

            var strip = new DockTileStrip();

            var host = new System.Windows.Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = strip,
            };

            try
            {
                host.Show();

                strip.SetMetrics(Size, Spacing, Peak, 8, Cap);
                strip.SetTiles(Models(Count));
                host.UpdateLayout();

                // Chiều rộng kỳ vọng: tổng chiều dài cần thiết của bản thân các ô (lớn hơn nhiều so với 500).
                var required = strip.RequiredLength;
                Assert.True(required > Cap * 10, $"Điều kiện tiên quyết không đạt: {Count} ô chỉ chiếm {required:F0} DIP");

                // Chiều rộng thực sự có hiệu lực phải bị giới hạn quanh giá trị trần, thay vì tăng vô hạn theo nội dung.
                var actual = strip.ActualWidth;
                Assert.True(
                    actual <= Cap + 1,
                    $"Giới hạn độ rộng chưa có hiệu lực: chiều rộng thực tế của khung nhìn là {actual:F1} DIP (kỳ vọng ≤ {Cap})");

                Assert.True(strip.CanScroll, "Nội dung vượt quá giới hạn nhưng không chuyển sang trạng thái có thể cuộn");

                // Thanh cuộn thực sự cuộn được: sau khi cuộn đến cuối, mục cuối phải nằm trong khung nhìn và bấm trúng được theo tọa độ nội dung.
                strip.ScrollToEnd();
                host.UpdateLayout();

                var offset = strip.HorizontalOffset;
                var viewport = strip.ViewportWidth;

                Assert.True(offset > 0, $"Cuộn chưa có hiệu lực: độ lệch vẫn là {offset:F1}");

                var lastCenter = DockMagnification.SlotCenter(
                    Count - 1, Size, Spacing, Peak);

                Assert.True(
                    lastCenter >= offset && lastCenter <= offset + viewport + 1,
                    $"Sau khi cuộn đến cuối, mục cuối vẫn không nằm trong khung nhìn: tâm {lastCenter:F1}, khung nhìn [{offset:F1}, {offset + viewport:F1}]");

                // Điểm ảnh trong khung nhìn sau khi quy đổi sang tọa độ nội dung phải trúng mục cuối — sau khi cuộn vẫn bấm trúng được.
                // Điểm dò lấy ở bên trong mép phải của mục cuối 4 DIP, chứ không lấy sát mép phải cùng của khung nhìn: vài DIP ngoài cùng bên phải
                // rơi vào vùng dự phòng phóng đại ở cuối hàng ô, vốn dĩ không nên trúng ô nào.
                var lastRight = lastCenter + (Size / 2);
                var probeViewportX = lastRight - 4 - offset;

                Assert.True(
                    probeViewportX >= 0 && probeViewportX <= viewport,
                    $"Điểm dò không nằm trong khung nhìn: {probeViewportX:F1} không thuộc [0, {viewport:F1}]");

                Assert.Equal(Count - 1, strip.ResolveHit(strip.ToContentX(probeViewportX)));
            }
            finally
            {
                host.Close();
            }
        });
    }
}