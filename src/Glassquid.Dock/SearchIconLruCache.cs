using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glassquid.Dock;

/// <summary>
/// Bộ nhớ đệm LRU cho icon tìm kiếm với giới hạn dung lượng bộ nhớ (mặc định 16MB) và số lượng phần tử.
/// </summary>
public sealed class SearchIconLruCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, ImageSource Image, long Bytes)> _cache;
    private readonly LinkedList<string> _lru = new();
    private readonly long _maxBytes;
    private readonly int _maxCount;
    private long _currentBytes;

    public SearchIconLruCache(long maxBytes = 16L * 1024 * 1024, int maxCount = 512)
    {
        _maxBytes = Math.Max(1L, maxBytes);
        _maxCount = Math.Max(1, maxCount);
        _cache = new Dictionary<string, (LinkedListNode<string>, ImageSource, long)>(StringComparer.OrdinalIgnoreCase);
    }

    public long CurrentBytes
    {
        get { lock (_lock) return _currentBytes; }
    }

    public int Count
    {
        get { lock (_lock) return _cache.Count; }
    }

    public long MaxBytes => _maxBytes;
    public int MaxCount => _maxCount;

    public bool TryGetValue(string key, out ImageSource? image)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            image = null;
            return false;
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddLast(entry.Node);
                image = entry.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    public void Set(string key, ImageSource image)
    {
        if (string.IsNullOrWhiteSpace(key) || image is null) return;

        var bytes = EstimateBytes(image);

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Bytes;
                _lru.Remove(existing.Node);
            }

            var node = _lru.AddLast(key);
            _cache[key] = (node, image, bytes);
            _currentBytes += bytes;

            Trim();
        }
    }

    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_lock)
        {
            if (_cache.Remove(key, out var entry))
            {
                _currentBytes -= entry.Bytes;
                _lru.Remove(entry.Node);
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private void Trim()
    {
        while ((_currentBytes > _maxBytes || _cache.Count > _maxCount) && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            if (_cache.Remove(oldest.Value, out var removed))
            {
                _currentBytes -= removed.Bytes;
            }
        }
    }

    public static long EstimateBytes(ImageSource image)
    {
        if (image is BitmapSource bmp)
        {
            var w = bmp.PixelWidth;
            var h = bmp.PixelHeight;
            if (w > 0 && h > 0)
            {
                return (long)w * h * 4;
            }
        }
        return 4096; // 32x32 RGBA ước tính
    }
}
