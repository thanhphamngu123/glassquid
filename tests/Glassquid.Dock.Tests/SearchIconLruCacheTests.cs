using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Dock;
using Xunit;

namespace Glassquid.Dock.Tests;

public sealed class SearchIconLruCacheTests
{
    private static BitmapSource CreateTestBitmap(int width, int height)
    {
        var rawPixels = new byte[width * height * 4];
        var bmp = BitmapSource.Create(
            width, height, 96, 96,
            PixelFormats.Bgra32, null,
            rawPixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void SetAndGet_ReturnsStoredBitmap()
    {
        var cache = new SearchIconLruCache(maxBytes: 1024 * 1024, maxCount: 10);
        var bmp = CreateTestBitmap(16, 16);

        cache.Set("test_path_1", bmp);

        Assert.True(cache.TryGetValue("test_path_1", out var result));
        Assert.Same(bmp, result);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.CurrentBytes > 0);
    }

    [Fact]
    public void Eviction_ByMaxCount_EvictsOldestFirst()
    {
        var cache = new SearchIconLruCache(maxBytes: 10 * 1024 * 1024, maxCount: 3);
        var bmp1 = CreateTestBitmap(16, 16);
        var bmp2 = CreateTestBitmap(16, 16);
        var bmp3 = CreateTestBitmap(16, 16);
        var bmp4 = CreateTestBitmap(16, 16);

        cache.Set("key1", bmp1);
        cache.Set("key2", bmp2);
        cache.Set("key3", bmp3);

        Assert.Equal(3, cache.Count);

        // Add 4th item -> key1 should be evicted (LRU)
        cache.Set("key4", bmp4);

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGetValue("key1", out _));
        Assert.True(cache.TryGetValue("key2", out _));
        Assert.True(cache.TryGetValue("key3", out _));
        Assert.True(cache.TryGetValue("key4", out _));
    }

    [Fact]
    public void AccessingItem_RefreshesLruOrder()
    {
        var cache = new SearchIconLruCache(maxBytes: 10 * 1024 * 1024, maxCount: 3);
        var bmp1 = CreateTestBitmap(16, 16);
        var bmp2 = CreateTestBitmap(16, 16);
        var bmp3 = CreateTestBitmap(16, 16);
        var bmp4 = CreateTestBitmap(16, 16);

        cache.Set("key1", bmp1);
        cache.Set("key2", bmp2);
        cache.Set("key3", bmp3);

        // Access key1 so it becomes the most recently used
        Assert.True(cache.TryGetValue("key1", out _));

        // Now add key4 -> key2 should be evicted instead of key1!
        cache.Set("key4", bmp4);

        Assert.True(cache.TryGetValue("key1", out _));
        Assert.False(cache.TryGetValue("key2", out _));
        Assert.True(cache.TryGetValue("key3", out _));
        Assert.True(cache.TryGetValue("key4", out _));
    }

    [Fact]
    public void Eviction_ByByteBudget_EvictsOldestFirst()
    {
        // 16x16 Bgra32 = 16 * 16 * 4 = 1024 bytes
        var bmp1 = CreateTestBitmap(16, 16);
        var bmp2 = CreateTestBitmap(16, 16);
        var bmp3 = CreateTestBitmap(16, 16);

        var singleBitmapBytes = SearchIconLruCache.EstimateBytes(bmp1);
        Assert.Equal(1024L, singleBitmapBytes);

        // Set budget to hold at most 2 bitmaps
        var cache = new SearchIconLruCache(maxBytes: singleBitmapBytes * 2, maxCount: 100);

        cache.Set("k1", bmp1);
        cache.Set("k2", bmp2);
        Assert.Equal(2, cache.Count);
        Assert.Equal(singleBitmapBytes * 2, cache.CurrentBytes);

        // Add 3rd bitmap -> k1 should be evicted to stay within budget
        cache.Set("k3", bmp3);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetValue("k1", out _));
        Assert.True(cache.TryGetValue("k2", out _));
        Assert.True(cache.TryGetValue("k3", out _));
        Assert.True(cache.CurrentBytes <= singleBitmapBytes * 2);
    }

    [Fact]
    public void RemoveAndClear_WorkCorrectly()
    {
        var cache = new SearchIconLruCache(maxBytes: 1024 * 1024, maxCount: 10);
        var bmp1 = CreateTestBitmap(16, 16);
        var bmp2 = CreateTestBitmap(16, 16);

        cache.Set("a", bmp1);
        cache.Set("b", bmp2);
        Assert.Equal(2, cache.Count);

        Assert.True(cache.Remove("a"));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGetValue("a", out _));

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0L, cache.CurrentBytes);
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        var cache = new SearchIconLruCache(maxBytes: 50 * 1024, maxCount: 20);
        var bmp = CreateTestBitmap(8, 8); // 8*8*4 = 256 bytes

        Parallel.For(0, 100, i =>
        {
            var key = $"item_{i % 30}";
            cache.Set(key, bmp);
            cache.TryGetValue(key, out _);
            if (i % 5 == 0)
            {
                cache.Remove(key);
            }
        });

        Assert.True(cache.Count <= 20);
        Assert.True(cache.CurrentBytes <= 50 * 1024);
    }
}
