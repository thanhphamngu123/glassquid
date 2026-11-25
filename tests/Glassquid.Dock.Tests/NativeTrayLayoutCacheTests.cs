using System.Windows;

namespace Glassquid.Dock.Tests;

public sealed class NativeTrayLayoutCacheTests
{
    private static readonly Rect NativeWindow = new(100, 200, 200, 100);

    private static (string Name, Rect Cell, Rect Glyph)[] SettledLayout() =>
    [
        ("NVIDIA Settings", new Rect(120, 220, 40, 40), new Rect(132, 232, 16, 16)),
        ("Copilot", new Rect(160, 220, 40, 40), new Rect(172, 232, 16, 16)),
    ];

    [Fact]
    public void MovingTheNativeWindowTranslatesTheSettledPhysicalLayout()
    {
        var cache = new NativeTrayLayoutCache();
        cache.Remember(SettledLayout(), NativeWindow, 1.25);
        var movedWindow = new Rect(500, 600, NativeWindow.Width, NativeWindow.Height);

        Assert.True(cache.TryGet(SettledLayout(), movedWindow, 1.25, out var result));
        Assert.Equal(new Rect(520, 620, 40, 40), result[0].Cell);
        Assert.Equal(new Rect(532, 632, 16, 16), result[0].Glyph);
    }

    [Fact]
    public void EntranceTranslationAndScalingDoNotReplaceSettledCells()
    {
        var cache = new NativeTrayLayoutCache();
        var settled = SettledLayout();
        cache.Remember(settled, NativeWindow, 1.25);
        (string Name, Rect Cell, Rect Glyph)[] animating =
        [
            ("NVIDIA Settings", new Rect(125, 235, 32, 32), new Rect(135, 245, 12, 12)),
            ("Copilot", new Rect(157, 235, 32, 32), new Rect(167, 245, 12, 12)),
        ];

        Assert.True(cache.TryGet(animating, NativeWindow, 1.25, out var result));
        Assert.Equal(settled[0].Cell, result[0].Cell);
        Assert.Equal(settled[1].Glyph, result[1].Glyph);
    }

    [Fact]
    public void StatusChangesKeepTheLayoutButReturnTheCurrentNativeName()
    {
        var cache = new NativeTrayLayoutCache();
        (string Name, Rect Cell, Rect Glyph)[] previous =
            [("Windows Security - No actions needed.", new Rect(120, 220, 40, 40), new Rect(132, 232, 16, 16))];
        cache.Remember(previous, NativeWindow, 1.25);
        var current = new[] { (Name: "Windows Security — Actions recommended.", previous[0].Cell, previous[0].Glyph) };

        Assert.True(cache.TryGet(current, NativeWindow, 1.25, out var result));
        Assert.Equal(current[0].Name, result[0].Name);
    }

    [Fact]
    public void UiaEnumerationOrderDoesNotChangeVisualLayoutOrder()
    {
        var cache = new NativeTrayLayoutCache();
        var settled = SettledLayout();
        cache.Remember(settled, NativeWindow, 1.25);

        Assert.True(cache.TryGet(settled.Reverse().ToArray(), NativeWindow, 1.25, out var result));
        Assert.Equal("NVIDIA Settings", result[0].Name);
        Assert.Equal("Copilot", result[1].Name);
    }

    [Fact]
    public void ReorderedIconsInvalidateTheRememberedLayout()
    {
        var cache = new NativeTrayLayoutCache();
        var settled = SettledLayout();
        cache.Remember(settled, NativeWindow, 1.25);
        var reordered = new[]
        {
            (Name: "Copilot", settled[0].Cell, settled[0].Glyph),
            (Name: "NVIDIA Settings", settled[1].Cell, settled[1].Glyph),
        };

        Assert.False(cache.TryGet(reordered, NativeWindow, 1.25, out _));
    }

    [Fact]
    public void UnknownIconsAndChangedCountsInvalidateTheRememberedLayout()
    {
        var cache = new NativeTrayLayoutCache();
        var settled = SettledLayout();
        cache.Remember(settled, NativeWindow, 1.25);
        var changed = new[]
        {
            (Name: "New application", settled[0].Cell, settled[0].Glyph),
            settled[1],
        };

        Assert.False(cache.TryGet(changed, NativeWindow, 1.25, out _));
        Assert.False(cache.TryGet(settled[..1], NativeWindow, 1.25, out _));
    }

    [Theory]
    [InlineData(201, 100, 1.25)]
    [InlineData(200, 101, 1.25)]
    [InlineData(200, 100, 1.5)]
    public void DifferentWindowSizeOrDpiInvalidatesTheRememberedLayout(double width, double height, double scale)
    {
        var cache = new NativeTrayLayoutCache();
        cache.Remember(SettledLayout(), NativeWindow, 1.25);

        Assert.False(cache.TryGet(SettledLayout(), new Rect(100, 200, width, height), scale, out _));
    }

    [Fact]
    public void ReturnedLayoutCannotMutateTheSnapshot()
    {
        var cache = new NativeTrayLayoutCache();
        cache.Remember(SettledLayout(), NativeWindow, 1.25);
        Assert.True(cache.TryGet(SettledLayout(), NativeWindow, 1.25, out var first));
        first.Clear();

        Assert.True(cache.TryGet(SettledLayout(), NativeWindow, 1.25, out var second));
        Assert.Equal(2, second.Count);
    }
}
