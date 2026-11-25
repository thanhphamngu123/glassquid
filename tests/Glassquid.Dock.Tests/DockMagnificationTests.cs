using GlassDock = Glassquid.Dock.DockMagnification;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Các khẳng định về hình học phóng đại: vị trí ô cố định, mức đỉnh có hiệu lực, kiểm tra va chạm (hit-test) và vẽ dùng chung một nguồn (hiển thị sao nhận vậy).
/// Sử dụng các tham số mặc định 48 DIP / 8 DIP / 1.35× (D-21) làm đường cơ sở.
/// </summary>
public class DockMagnificationTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    [Fact]
    public void SlotCentersAreEquallySpacedWhenUnmagnified()
    {
        var geometry = GlassDock.Compute(4, Size, Spacing, Peak, pointerPosition: null);

        Assert.Equal(4, geometry.Length);
        Assert.All(geometry, tile => Assert.Equal(1.0, tile.Scale));

        // Tâm đã bao gồm phần chừa lề hai đầu 8.4 (đỉnh 1.35, biểu tượng 48 DIP), bước nhảy 56.
        var padding = GlassDock.EndPadding(Size, Peak);
        Assert.Equal(padding + 24, geometry[0].Center);
        Assert.Equal(padding + 24 + Size + Spacing, geometry[1].Center);
        Assert.Equal(padding + 24 + (2 * (Size + Spacing)), geometry[2].Center);
    }

    [Fact]
    public void FirstAndLastTilesStayWithinCanvasAtPeakMagnification()
    {
        // Tái hiện lỗi: nếu chỉ tính tổng chiều dài lớn hơn mà các ô không dịch vào trong,
        // mép trái của ô đầu tiên vẫn mang tọa độ âm (−8.4 DIP ở mức đỉnh mặc định), và khi khung chứa cắt xén thì cạnh đó sẽ bị cắt mất sau khi phóng đại.
        var count = 4;

        foreach (var pointer in new double?[] { null, 0, GlassDock.SlotCenter(0, Size, Spacing, Peak), 5_000 })
        {
            var geometry = GlassDock.Compute(count, Size, Spacing, Peak, pointer);
            var length = GlassDock.RequiredLength(count, Size, Spacing, Peak);

            for (var index = 0; index < count; index++)
            {
                var (center, scale) = geometry[index];
                var half = Size * scale / 2;

                Assert.True(
                    center - half >= -0.001,
                    $"Con trỏ {pointer}: mép trái của ô thứ {index} là {center - half:F2} (âm), sẽ bị cắt xén");

                Assert.True(
                    center + half <= length + 0.001,
                    $"Con trỏ {pointer}: mép phải của ô thứ {index} là {center + half:F2} vượt quá tổng chiều dài {length:F2}");
            }
        }
    }

    [Fact]
    public void DrawOrderMatchesHitTestOrder()
    {
        // Quy tắc vùng chồng lấn "hiển thị sao nhận vậy" yêu cầu thứ tự z khi vẽ phải khớp với thứ tự kiểm tra va chạm;
        // cả hai dùng chung TopToBottomOrder, ca kiểm thử này khóa chặt chính việc "dùng chung" đó.
        var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak) - 6;
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        var order = GlassDock.TopToBottomOrder(geometry);

        // Thứ tự = giảm dần theo tỷ lệ thu phóng.
        for (var index = 1; index < order.Length; index++)
        {
            Assert.True(
                geometry[order[index - 1]].Scale >= geometry[order[index]].Scale,
                "Thứ tự z không giảm dần theo tỷ lệ phóng đại");
        }

        // Kết quả trúng đích là phần tử đầu tiên khớp trong thứ tự này: tạo một điểm được hai ô cùng phủ lên,
        // và khẳng định kết quả kiểm tra va chạm bằng "chỉ mục đầu tiên phủ lên nó trong thứ tự đó".
        var probe = geometry[2].Center;
        var expected = order.First(index =>
        {
            var (center, scale) = geometry[index];
            var half = Size * scale / 2;
            return probe >= center - half && probe < center + half;
        });

        Assert.Equal(expected, GlassDock.HitTest(geometry, Size, probe));
    }

    [Fact]
    public void TileDirectlyUnderPointerGetsPeakScale_D17()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointerPosition: GlassDock.SlotCenter(2, Size, Spacing, Peak));

        Assert.Equal(Peak, geometry[2].Scale, 9);

        // Đường cong phạm vi hữu hạn hiện tại trở về 1 ngoài một bước ô, nên các ô ở rìa ngoài có thể bằng nhau.
        Assert.Equal(1, geometry[0].Scale);
        Assert.True(geometry[0].Scale <= geometry[1].Scale);
        Assert.True(geometry[1].Scale < geometry[2].Scale);
        Assert.True(geometry[3].Scale < geometry[2].Scale);
        Assert.True(geometry[4].Scale <= geometry[3].Scale);
        var midpoint = (GlassDock.SlotCenter(1, Size, Spacing, Peak) + GlassDock.SlotCenter(2, Size, Spacing, Peak)) / 2;
        var between = GlassDock.Compute(5, Size, Spacing, Peak, midpoint);
        Assert.Equal(between[1].Scale, between[2].Scale, 9);
        Assert.InRange(between[1].Scale, 1.01, Peak - .01);
        Assert.Equal(1, between[0].Scale);
    }

    [Fact]
    public void ConfigurablePeak_DoublesCenterTileWhenSetToTwo()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, 2.0, pointerPosition: GlassDock.SlotCenter(1, Size, Spacing, 2.0));

        Assert.Equal(2.0, geometry[1].Scale, 9);
    }

    [Fact]
    public void HitTestHitsTileDirectlyUnderPointer()
    {
        var pointer = GlassDock.SlotCenter(3, Size, Spacing, Peak);
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        Assert.Equal(3, GlassDock.HitTest(geometry, Size, pointer));
    }

    [Fact]
    public void OverlappingRegionBelongsToLargestScaleTile_Wysiwyg()
    {
        // Con trỏ nằm giữa ô thứ 1 và ô thứ 2, lệch về phía ô thứ 2:
        // ô thứ 2 gần con trỏ hơn nên phóng đại lớn hơn, phình ra phủ qua mép phải của ô thứ 1.
        var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak) - 6;
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        // Khẳng định lúc này thực sự có chồng lấn (mép trái ô thứ 2 vượt qua mép phải ô thứ 1),
        // nếu không ca kiểm thử này sẽ chẳng kiểm chứng được gì.
        var overlapStart = geometry[2].Center - Size * geometry[2].Scale / 2;
        var previousEnd = geometry[1].Center + Size * geometry[1].Scale / 2;
        Assert.True(overlapStart < previousEnd, "Tiền đề ca kiểm thử không thỏa mãn: hai ô không chồng lấn lên nhau");

        Assert.Equal(2, GlassDock.HitTest(geometry, Size, pointer));
    }

    [Fact]
    public void HitTestAndDrawShareSameFrameGeometry_NoDriftMidAnimation()
    {
        // Mô phỏng giữa chừng hoạt ảnh: lấy liên tiếp 21 vị trí con trỏ giữa hai ô.
        // Mỗi khung hình đều tính hình học trước rồi mới kiểm tra va chạm — đây chính là việc runtime làm ở mỗi khung hình;
        // nếu kết quả trúng đích nhảy ra ngoài "hai ô gần nhất ở hai bên con trỏ" thì chứng tỏ hình học và va chạm bị lệch pha.
        for (var step = 0; step <= 20; step++)
        {
            var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak)
                + (GlassDock.SlotCenter(3, Size, Spacing, Peak) - GlassDock.SlotCenter(2, Size, Spacing, Peak)) * step / 20.0;

            var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);
            var hit = GlassDock.HitTest(geometry, Size, pointer);

            Assert.True(hit is 2 or 3, $"Con trỏ {pointer:F1} đã trúng ô thứ {hit} (kỳ vọng 2 hoặc 3)");
        }
    }

    [Fact]
    public void HitTestReturnsNoneWhenInTileGap()
    {
        // Kéo con trỏ ra ngoài mép phải cùng: không có ô nào nhận va chạm.
        var geometry = GlassDock.Compute(3, Size, Spacing, Peak, pointerPosition: null);
        Assert.Equal(-1, GlassDock.HitTest(geometry, Size, 1000));
    }

    [Fact]
    public void RequiredLengthMatchesMagnifiedOuterEdgesExactly()
    {
        // Tổng chiều dài khi chưa phóng đại: 3 ô = 3*48 + 2*8 = 160.
        Assert.Equal(160, GlassDock.RequiredLength(3, Size, Spacing, 1.0));

        // Khi đỉnh là 1.35, mỗi đầu chừa thêm (1.35-1)*48/2 = 8.4: tổng chiều dài = 160 + 16.8.
        Assert.Equal(160 + 2 * 8.4, GlassDock.RequiredLength(3, Size, Spacing, Peak), 9);

        // Tính chất then chốt: khi ô ở một đầu phóng đại, mép ngoài của nó nằm khít đúng trên biên canvas —
        // tổng chiều dài không phải là "đủ dùng đại khái", mà bằng chính xác biên sau khi phóng đại ở đầu đó.
        // (Hai đầu không thể cùng phóng đại đồng thời: con trỏ chỉ có thể ở một chỗ.)
        var length = GlassDock.RequiredLength(3, Size, Spacing, Peak);

        var atFirst = GlassDock.Compute(3, Size, Spacing, Peak, GlassDock.SlotCenter(0, Size, Spacing, Peak));
        Assert.Equal(0, atFirst[0].Center - (Size * atFirst[0].Scale / 2), 6);

        var atLast = GlassDock.Compute(3, Size, Spacing, Peak, GlassDock.SlotCenter(2, Size, Spacing, Peak));
        Assert.Equal(length, atLast[2].Center + (Size * atLast[2].Scale / 2), 6);
    }

    [Fact]
    public void RequiredThicknessReservesMagnificationSpace_NotClippedByWindow()
    {
        // Biểu tượng 48 DIP ở mức đỉnh 1.35 sẽ phình lên thành 64.8; khi chừa lề trong trên dưới mỗi bên 6 DIP
        // thì độ dày khung chứa phải >= 64.8 + 12.
        var thickness = GlassDock.RequiredThickness(Size, Peak, 6);

        Assert.True(thickness >= Size * Peak, "Độ dày khung chứa chưa bao phủ kích thước phóng đại cực đại — biểu tượng sẽ bị cắt xén");
        Assert.Equal(Size * Peak + 12, thickness, 9);
    }
}
