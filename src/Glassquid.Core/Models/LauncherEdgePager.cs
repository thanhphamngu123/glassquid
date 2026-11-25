namespace Glassquid.Core.Models;

/// <summary>Tính thời gian giữ chuột ở cạnh theo đồng hồ; mỗi lần giữ liên tục chuyển tối đa một trang.</summary>
public sealed class LauncherEdgePager(TimeSpan threshold)
{
    private int _direction;
    private DateTimeOffset _enteredAt;
    private bool _fired;

    public bool Tick(int direction, DateTimeOffset now)
    {
        if (direction is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (direction != _direction)
        {
            _direction = direction;
            _enteredAt = now;
            _fired = false;
        }
        if (direction == 0 || _fired || now - _enteredAt < threshold) return false;
        _fired = true;
        return true;
    }

    public void Reset() => _direction = 0;
}
