namespace Glassquid.Core.Imaging;

/// <summary>
/// Cắt bỏ phần lề trong suốt dư thừa trong bitmap biểu tượng, giúp hình ảnh thực tế chiếm diện tích phù hợp trong Launcher và Dock.
/// </summary>
public static class IconCanvasNormalizer
{
    private const byte MinimumContentAlpha = 16;
    private const long MinimumProjectionAlpha = 510;
    private const int PaddingDivisor = 14;
    private const int EdgeComponentAreaDivisor = 8;
    private const int EdgeCoverageDivisor = 20;

    /// <summary>
    /// Cắt pixel dựa theo đường biên nội dung alpha và giữ lại một ít lề trong suốt xung quanh.
    /// Khi không thể nhận diện chắc chắn đường biên nội dung hoặc hình ảnh đã sát mép khung vẽ, trả về nguyên trạng.
    /// </summary>
    /// <param name="bgra">Mỗi pixel 4 byte, dòng đầu tiên ở trên cùng.</param>
    /// <param name="width">Chiều rộng ảnh.</param>
    /// <param name="height">Chiều cao ảnh.</param>
    /// <returns>Dữ liệu pixel, chiều rộng và chiều cao ảnh.</returns>
    public static (byte[] Bgra, int Width, int Height) Normalize(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expectedLength = checked(width * height * 4);
        if (bgra.Length != expectedLength)
        {
            throw new ArgumentException($"Độ dài dữ liệu pixel phải là {expectedLength} byte, thực tế là {bgra.Length} byte.", nameof(bgra));
        }

        var pixelCount = checked(width * height);
        var visible = new bool[pixelCount];

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            visible[pixel] = bgra[(pixel * 4) + 3] >= MinimumContentAlpha;
        }

        // Shell đôi khi gắn thêm một đường viền mảnh khép kín ở mép ngoài cùng của khung vẽ biểu tượng trong suốt.
        // Nếu không loại trừ nó trước, đường biên alpha sẽ bao trùm nhầm toàn bộ ảnh và hình ở giữa sẽ không được phóng to.
        var ignoredFrame = FindThinFrameComponents(visible, width, height);
        var rowAlpha = new long[height];
        var columnAlpha = new long[width];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            if (!visible[pixel] || ignoredFrame[pixel])
            {
                continue;
            }

            var alpha = bgra[(pixel * 4) + 3];
            var x = pixel % width;
            var y = pixel / width;
            rowAlpha[y] += alpha;
            columnAlpha[x] += alpha;
        }

        // Các điểm nhiễu rải rác cường độ thấp không được phép mở rộng đường biên nội dung ra toàn bộ bitmap.
        var top = Array.FindIndex(rowAlpha, mass => mass >= MinimumProjectionAlpha);
        var bottom = Array.FindLastIndex(rowAlpha, mass => mass >= MinimumProjectionAlpha);
        var left = Array.FindIndex(columnAlpha, mass => mass >= MinimumProjectionAlpha);
        var right = Array.FindLastIndex(columnAlpha, mass => mass >= MinimumProjectionAlpha);

        if (top < 0 || bottom < top || left < 0 || right < left)
        {
            return (bgra, width, height);
        }

        var contentWidth = right - left + 1;
        var contentHeight = bottom - top + 1;
        var horizontalPadding = Math.Max(1, (contentWidth + PaddingDivisor - 1) / PaddingDivisor);
        var verticalPadding = Math.Max(1, (contentHeight + PaddingDivisor - 1) / PaddingDivisor);
        left = Math.Max(0, left - horizontalPadding);
        right = Math.Min(width - 1, right + horizontalPadding);
        top = Math.Max(0, top - verticalPadding);
        bottom = Math.Min(height - 1, bottom + verticalPadding);

        var croppedWidth = right - left + 1;
        var croppedHeight = bottom - top + 1;
        if (croppedWidth == width && croppedHeight == height)
        {
            return (bgra, width, height);
        }

        var cropped = new byte[checked(croppedWidth * croppedHeight * 4)];
        var rowByteCount = croppedWidth * 4;
        for (var y = 0; y < croppedHeight; y++)
        {
            var sourceOffset = ((top + y) * width + left) * 4;
            Buffer.BlockCopy(bgra, sourceOffset, cropped, y * rowByteCount, rowByteCount);
        }

        return (cropped, croppedWidth, croppedHeight);
    }

    private static bool[] FindThinFrameComponents(bool[] visible, int width, int height)
    {
        var pixelCount = visible.Length;
        var visited = new bool[pixelCount];
        var ignored = new bool[pixelCount];
        var queue = new int[pixelCount];

        void InspectEdgeComponent(int start)
        {
            if (!visible[start] || visited[start])
            {
                return;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            var touchesTop = false;
            var touchesBottom = false;
            var touchesLeft = false;
            var touchesRight = false;
            var topPixels = 0;
            var bottomPixels = 0;
            var leftPixels = 0;
            var rightPixels = 0;

            while (head < tail)
            {
                var pixel = queue[head++];
                var x = pixel % width;
                var y = pixel / width;

                if (y == 0) { touchesTop = true; topPixels++; }
                if (y == height - 1) { touchesBottom = true; bottomPixels++; }
                if (x == 0) { touchesLeft = true; leftPixels++; }
                if (x == width - 1) { touchesRight = true; rightPixels++; }

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var neighborX = x + offsetX;
                        var neighborY = y + offsetY;
                        if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height)
                        {
                            continue;
                        }

                        var neighbor = neighborY * width + neighborX;
                        if (!visible[neighbor] || visited[neighbor])
                        {
                            continue;
                        }

                        visited[neighbor] = true;
                        queue[tail++] = neighbor;
                    }
                }
            }

            var minimumTopBottomCoverage = Math.Max(2, width / EdgeCoverageDivisor);
            var minimumLeftRightCoverage = Math.Max(2, height / EdgeCoverageDivisor);
            var isThinFrame = touchesTop && touchesBottom && touchesLeft && touchesRight
                && tail <= pixelCount / EdgeComponentAreaDivisor
                && topPixels >= minimumTopBottomCoverage && bottomPixels >= minimumTopBottomCoverage
                && leftPixels >= minimumLeftRightCoverage && rightPixels >= minimumLeftRightCoverage;

            if (isThinFrame)
            {
                for (var index = 0; index < tail; index++)
                {
                    ignored[queue[index]] = true;
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            InspectEdgeComponent(x);
            if (height > 1)
            {
                InspectEdgeComponent(((height - 1) * width) + x);
            }
        }

        for (var y = 1; y < height - 1; y++)
        {
            InspectEdgeComponent(y * width);
            if (width > 1)
            {
                InspectEdgeComponent((y * width) + width - 1);
            }
        }

        return ignored;
    }
}
