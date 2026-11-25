using Glassquid.Core.Geometry;

namespace Glassquid.Core.Tests;

public sealed class RoundedRectTests
{
    // Một hình chữ nhật 200×100 với bán kính bo góc 20, đủ điển hình và đủ nhỏ để dễ tính giá trị kỳ vọng.
    private const double Width = 200;
    private const double Height = 100;
    private const double Radius = 20;

    [Fact]
    public void Contains_AcceptsTheCentre()
    {
        Assert.True(RoundedRect.Contains(100, 50, Width, Height, Radius));
    }

    [Fact]
    public void Contains_RejectsPointsOutsideTheRectangle()
    {
        Assert.False(RoundedRect.Contains(-1, 50, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(50, -1, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width + 1, 50, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(50, Height + 1, Width, Height, Radius));
    }

    [Fact]
    public void Contains_RejectsTheVeryCorner()
    {
        // Đây chính là điểm trong thực tế bị pixel đổ bóng "lấp đầy", dẫn đến chặn nhầm chuột.
        Assert.False(RoundedRect.Contains(0, 0, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width, 0, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(0, Height, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width, Height, Width, Height, Radius));
    }

    [Fact]
    public void Contains_AcceptsTheEdgeMidpoints()
    {
        // Trung điểm của bốn cạnh nằm trên đoạn thẳng, bắt buộc phải tính là bên trong — nếu không mép bảng sẽ không bấm được.
        Assert.True(RoundedRect.Contains(Width / 2, 0, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(Width / 2, Height, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(0, Height / 2, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(Width, Height / 2, Width, Height, Radius));
    }

    [Fact]
    public void Contains_TreatsTheArcBoundaryAsInside()
    {
        // Tâm đường tròn ở (20,20), một điểm trên cung cách tâm đúng một bán kính.
        // Ranh giới tính là bên trong: mép chênh một pixel không nên để lượt nhấp rơi xuống cửa sổ khác.
        var onArc = Radius - (Radius * Math.Sqrt(0.5));

        Assert.True(RoundedRect.Contains(onArc, onArc, Width, Height, Radius));
    }

    [Fact]
    public void Contains_IsMonotonicAlongTheCornerDiagonal()
    {
        // Đi dọc theo đường chéo từ đỉnh góc vào trong, ban đầu phải là bên ngoài, sau khi vượt qua đường cung sẽ thành bên trong và chỉ đổi trạng thái đúng một lần.
        var flipped = 0;
        var previous = false;

        for (var step = 0; step <= Radius; step++)
        {
            var current = RoundedRect.Contains(step, step, Width, Height, Radius);
            if (current != previous)
            {
                flipped++;
                previous = current;
            }
        }

        Assert.Equal(1, flipped);
    }

    [Fact]
    public void Contains_WithZeroRadius_BehavesLikeARectangle()
    {
        Assert.True(RoundedRect.Contains(0, 0, Width, Height, 0));
        Assert.True(RoundedRect.Contains(Width, Height, Width, Height, 0));
        Assert.False(RoundedRect.Contains(-0.5, 0, Width, Height, 0));
    }

    [Fact]
    public void Contains_ClampsAnOversizedRadiusToHalfTheShortSide()
    {
        // Khi bán kính lớn hơn một nửa cạnh ngắn, hai góc bo sẽ xuyên qua nhau, phép kiểm tra không còn là hình chữ nhật bo góc nữa.
        // Ở đây giới hạn theo một nửa cạnh ngắn, đồng thời vùng trung tâm vẫn phải nhấp được.
        Assert.True(RoundedRect.Contains(Width / 2, Height / 2, Width, Height, 5000));
        Assert.False(RoundedRect.Contains(0, 0, Width, Height, 5000));
    }

    [Fact]
    public void Contains_RejectsEverything_ForADegenerateRectangle()
    {
        Assert.False(RoundedRect.Contains(0, 0, 0, 100, Radius));
        Assert.False(RoundedRect.Contains(0, 0, 100, 0, Radius));
        Assert.False(RoundedRect.Contains(0, 0, 0, 0, Radius));
    }

    [Fact]
    public void Contains_WorksForTheDockAndLaunchpadRadii()
    {
        // Hai bán kính được quy định trong kế hoạch, mỗi loại lấy một bộ kích thước thực tế để kiểm chứng bốn góc cho phép xuyên qua và tâm nhấp được.
        // Dock: bo góc 22, chiều cao thanh khoảng 90.
        Assert.False(RoundedRect.Contains(0, 0, 400, 90, 22));
        Assert.True(RoundedRect.Contains(200, 45, 400, 90, 22));

        // Launchpad: bo góc 28, bảng khoảng 1127×602 DIP.
        Assert.False(RoundedRect.Contains(0, 0, 1127, 602, 28));
        Assert.True(RoundedRect.Contains(563, 301, 1127, 602, 28));
    }
}