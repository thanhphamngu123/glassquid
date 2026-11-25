using System.Threading;

namespace Glassquid.Core.Input;

/// <summary>Khi đọc trạng thái toàn màn hình trong callback nhập liệu, không thực hiện truy vấn cửa sổ hoặc gọi COM.</summary>
public sealed class FullscreenStateCache
{
    private const long StateMask = 3;
    private const int Unknown = 0;
    private const int Windowed = 1;
    private const int Fullscreen = 2;
    private long _state;

    public long Generation => Volatile.Read(ref _state) >> 2;

    public long MarkUnknown()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            var generation = (current >> 2) + 1;
            var next = generation << 2;
            if (Interlocked.CompareExchange(ref _state, next, current) == current) return generation;
        }
    }

    /// <summary>Tương thích với lần nạp đầu tiên; sau khi mất hiệu lực, phải gửi kết quả bằng thế hệ đã chụp khi bắt đầu truy vấn.</summary>
    public void Update(bool isFullscreen) => Update(generation: 0, isFullscreen);

    public void Update(long generation, bool isFullscreen)
    {
        var state = isFullscreen ? Fullscreen : Windowed;
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if ((current >> 2) != generation) return;
            var next = (generation << 2) | (long)state;
            if (Interlocked.CompareExchange(ref _state, next, current) == current) return;
        }
    }

    public bool CanRunPassive(bool suppressWhenFullscreen, bool monitorAvailable = true)
    {
        if (!suppressWhenFullscreen) return true;
        return monitorAvailable && (Volatile.Read(ref _state) & StateMask) == Windowed;
    }
}
