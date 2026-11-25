using Glassquid.Core.Models.Settings;
using Glassquid.Dock;
using Glassquid.Platform.Displays;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Khẳng định hình học cho phép tính vị trí đặt. Hình học màn hình dùng tổ hợp DPI hỗn hợp đo thực tế trên máy làm giá trị khởi tạo
/// (màn hình chính 2560×1440 @150%, màn hình phụ xoay dọc 1600×2560 @125% có tọa độ âm),
/// không phụ thuộc vào việc liệt kê máy thật, chạy được ngoại tuyến.
/// </summary>
/// <remarks>
/// Phần "cửa sổ WPF thực sự vào đúng vị trí theo kết quả tính" trên máy thật được kiểm thử hồi quy của <c>--phase4-acceptance</c> bao phủ;
/// ở đây khóa chặt tính chất hình học của bản thân hàm thuần túy.
/// </remarks>
public class DockPlacementCalculatorTests
{
    /// <summary>Vùng làm việc màn hình chính: 2560×1440 @150%, thanh tác vụ chiếm 48 pixel vật lý ở đáy.</summary>
    private static readonly PixelRect PrimaryWorkArea = new(0, 0, 2560, 1392);

    private const uint PrimaryDpi = 144;

    /// <summary>Vùng làm việc màn hình phụ: xoay dọc 1600×2560 @125%, tọa độ X âm (bên trái màn hình chính).</summary>
    private static readonly PixelRect SecondaryWorkArea = new(-1600, 0, 1600, 2512);

    private const uint SecondaryDpi = 120;

    /// <summary>Kích thước vật lý của một cửa sổ Dock điển hình (khoảng 900×90 DIP ở mức 150%).</summary>
    private const int DockWidth = 1350;

    private const int DockHeight = 130;

    [Fact]
    public void BottomCenter_AlignsToPrimaryWorkAreaBottomAndCentersHorizontally()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Bottom, rect.Bottom);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void BottomLeftAndRight_AlignToWorkAreaEdges()
    {
        var left = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.X, left.X);
        Assert.Equal(PrimaryWorkArea.Bottom, left.Bottom);

        var right = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomRight, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.Right, right.Right);
        Assert.Equal(PrimaryWorkArea.Bottom, right.Bottom);
    }

    [Fact]
    public void TopCenter_AlignsToWorkAreaTopAndCentersHorizontally()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.TopCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Y, rect.Y);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void AllFourPlacements_StayWithinSecondaryWorkArea()
    {
        // Tọa độ âm của màn hình phụ + tỷ lệ thu phóng khác nhau — tình huống điển hình dễ sai dấu.
        foreach (var placement in new[] { DockPlacement.BottomCenter, DockPlacement.BottomLeft, DockPlacement.BottomRight, DockPlacement.TopCenter })
        {
            var rect = DockPlacementCalculator.Calculate(
                SecondaryWorkArea, placement, 0, 0, DockWidth, DockHeight, SecondaryDpi);

            Assert.True(rect.X >= SecondaryWorkArea.X, $"{placement}: X {rect.X} < {SecondaryWorkArea.X}");
            Assert.True(rect.Y >= SecondaryWorkArea.Y, $"{placement}: Y {rect.Y} < {SecondaryWorkArea.Y}");
            Assert.True(rect.Right <= SecondaryWorkArea.Right, $"{placement}: vượt mép phải");
            Assert.True(rect.Bottom <= SecondaryWorkArea.Bottom, $"{placement}: vượt mép dưới");
        }
    }

    [Fact]
    public void Offsets_ScaleByTargetMonitorDpi()
    {
        // 100 DIP trên màn hình chính 150% là 150 pixel vật lý, trên màn hình phụ 125% là 125 —
        // dùng DPI hệ thống (144) để tính độ lệch màn hình phụ chính là lỗi đã kiểm chứng ở giai đoạn 2.
        var baseline = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, SecondaryDpi);
        var shifted = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 100, 0, DockWidth, DockHeight, SecondaryDpi);

        Assert.Equal(125, shifted.X - baseline.X);

        var vertical = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, -40, DockWidth, DockHeight, SecondaryDpi);
        Assert.Equal(baseline.Bottom - 50, vertical.Bottom);
    }

    [Fact]
    public void OffsetsPushingWindowOutside_AreClampedToWorkArea()
    {
        var pushed = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, -100_000, 100_000, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.X, pushed.X);
        Assert.True(pushed.Bottom <= PrimaryWorkArea.Bottom);
        Assert.True(pushed.Right <= PrimaryWorkArea.Right);
    }

    [Fact]
    public void WindowWiderThanWorkArea_IsClampedToWorkAreaWidth()
    {
        var huge = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomCenter, 0, 0, 4_000, 130, SecondaryDpi);

        Assert.Equal(SecondaryWorkArea.Width, huge.Width);
        Assert.Equal(SecondaryWorkArea.X, huge.X);
    }

    [Fact]
    public void BottomPlacement_SitsAbovePrimaryTaskbar()
    {
        // Vùng làm việc đã trừ đi thanh tác vụ (1392 = 1440 - 48),
        // nên "bám sát đáy vùng làm việc" đồng nghĩa với "nằm phía trên thanh tác vụ".
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(1392, rect.Bottom);
        Assert.Equal(1440 - 48, PrimaryWorkArea.Bottom);
    }
}
