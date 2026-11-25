using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Khung vẽ và hợp đồng kiểm tra vùng nhấn của dải ô Dock (tái hiện lỗi và hồi quy).
/// </summary>
public class DockTileStripHitTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    private static List<DockTileModel> Models(int count)
        => Enumerable.Range(0, count)
            .Select(index => new DockTileModel { EntryId = $"e{index}", DisplayName = $"Mục {index}" })
            .ToList();

    [Fact]
    public void ClickUsesLastPaintedFrame_DoesNotRecomputeGeometryOnClick()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            // Vẽ một khung hình: con trỏ dừng ở ô thứ 1 (hoạt ảnh đã chạy tới đó).
            strip.PaintFrame(DockMagnification.SlotCenter(1, Size, Spacing, Peak));

            // Tọa độ nhấn 56 nằm trong vùng chồng lấn của hai ô.
            const double ClickAt = 56;

            // Nếu tính lại hình học theo con trỏ tức thời khi nhấn, sẽ cho ra kết quả khác với hình ảnh đang hiển thị.
            var fresh = DockMagnification.Compute(3, Size, Spacing, Peak, ClickAt);
            var freshHit = DockMagnification.HitTest(fresh, Size, ClickAt);

            var painted = strip.ResolveHit(ClickAt);

            Assert.True(
                freshHit != painted,
                $"Tiền đề ca kiểm thử không đúng: tính lại và khung đã vẽ cho cùng một kết quả (đều là {painted})");

            var expected = DockMagnification.HitTest(strip.PaintedGeometry.ToArray(), Size, ClickAt);
            Assert.Equal(expected, painted);
        });
    }

    [Fact]
    public void PaintedFrameZOrder_MatchesHitTestOrder()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(5));

            var pointer = DockMagnification.SlotCenter(2, Size, Spacing, Peak) - 6;
            strip.PaintFrame(pointer);

            var geometry = strip.PaintedGeometry.ToArray();
            var order = DockMagnification.TopToBottomOrder(geometry);

            // Chỉ số trả về khi kiểm tra vùng nhấn phải là ô đầu tiên che phủ điểm đó theo thứ tự z.
            foreach (var probe in new[] { geometry[0].Center, geometry[2].Center, pointer })
            {
                var hit = strip.ResolveHit(probe);

                if (hit < 0)
                {
                    continue;
                }

                var firstCoveringInOrder = order.FirstOrDefault(index =>
                {
                    var (center, scale) = geometry[index];
                    var half = Size * scale / 2;
                    return probe >= center - half && probe < center + half;
                }, -1);

                Assert.Equal(firstCoveringInOrder, hit);
            }
        });
    }

    [Fact]
    public void FirstTileAfterMagnification_IsNotClippedInsideCanvas()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            strip.PaintFrame(DockMagnification.SlotCenter(0, Size, Spacing, Peak));

            var geometry = strip.PaintedGeometry.ToArray();
            var first = geometry[0];
            var left = first.Center - (Size * first.Scale / 2);

            Assert.True(left >= -0.001, $"Cạnh trái của ô đầu tiên {left:F2} bị âm, sẽ bị vùng chứa cắt xén");
            Assert.True(
                geometry[2].Center + (Size * geometry[2].Scale / 2) <= strip.RequiredLength + 0.001,
                "Cạnh phải của ô cuối cùng vượt quá tổng chiều dài vùng vẽ");
        });
    }
}