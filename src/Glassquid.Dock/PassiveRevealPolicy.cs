namespace Glassquid.Dock;
public static class PassiveRevealPolicy
{
    public static bool CanReveal(bool inHotzone,bool edgeEnabled,bool fullscreen,bool suppressFullscreen,bool covered,bool blockCovered)
        => inHotzone && edgeEnabled && !(fullscreen && suppressFullscreen) && !(covered && blockCovered);

    /// <summary>Trong lúc bị che khuất hoặc toàn màn hình, sau khi tắt khóa đánh thức sẽ cho phép di chuột đến vị trí gốc của Dock để hiển thị lại.</summary>
    public static bool CanRevealDuringSuppression(bool pointerOverDock, bool revealLocked)
        => !revealLocked && pointerOverDock;
}
