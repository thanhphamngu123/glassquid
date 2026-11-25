using System.Windows;

namespace Glassquid.Dock.Tests;

public sealed class NativeTrayArtworkMatcherTests
{
    private static readonly Rect NativeCell = new(100, 200, 40, 40);

    [Fact]
    public void NativeNameWinsOverAnotherAppsStaleRectangle()
    {
        (string Name, Rect? Bounds)[] candidates =
        [
            ("Copilot", NativeCell),
            ("NVIDIA Settings", new Rect(300, 200, 40, 40)),
        ];

        Assert.Equal(1, NativeTrayArtworkMatcher.FindMatch("NVIDIA Settings", NativeCell, candidates));
    }

    [Theory]
    [InlineData("Windows Security - No actions needed.", "Windows Security notification icon")]
    [InlineData("Windows Security — Actions recommended.", "Windows Security tray icon")]
    [InlineData("  Windows  Security  – No actions needed.", "Windows Security")]
    public void StatusTextDoesNotChangeTheApplicationIdentity(string nativeName, string artworkName)
    {
        (string Name, Rect? Bounds)[] candidates = [(artworkName, null)];

        Assert.Equal(0, NativeTrayArtworkMatcher.FindMatch(nativeName, NativeCell, candidates));
    }

    [Fact]
    public void UnknownKnownIdentityDoesNotUseAnotherAppsRectangle()
    {
        (string Name, Rect? Bounds)[] candidates = [("Copilot", NativeCell)];

        Assert.Equal(-1, NativeTrayArtworkMatcher.FindMatch("Unrecognized application", NativeCell, candidates));
    }

    [Fact]
    public void ExactNameWinsOverPartialBrandMatch()
    {
        (string Name, Rect? Bounds)[] candidates = [("Copilot", NativeCell), ("Microsoft 365 Copilot", null)];

        Assert.Equal(1, NativeTrayArtworkMatcher.FindMatch("Microsoft 365 Copilot", NativeCell, candidates));
    }

    [Fact]
    public void StrongWholeNameAliasCanSupplyArtwork()
    {
        (string Name, Rect? Bounds)[] candidates = [("Copilot", null)];

        Assert.Equal(0, NativeTrayArtworkMatcher.FindMatch("Microsoft 365 Copilot", NativeCell, candidates));
    }

    [Fact]
    public void PartialCharactersInsideAnotherNameAreNotAnIdentity()
    {
        (string Name, Rect? Bounds)[] candidates = [("OtherCopilotApp", NativeCell)];

        Assert.Equal(-1, NativeTrayArtworkMatcher.FindMatch("Copilot", NativeCell, candidates));
    }

    [Fact]
    public void NamedMatchWinsOverAnonymousArtworkAtTheCell()
    {
        (string Name, Rect? Bounds)[] candidates = [(string.Empty, NativeCell), ("NVIDIA Settings", null)];

        Assert.Equal(1, NativeTrayArtworkMatcher.FindMatch("NVIDIA Settings", NativeCell, candidates));
    }

    [Theory]
    [InlineData("", "Copilot")]
    [InlineData("NVIDIA Settings", "")]
    public void GeometryCanFillOneMissingIdentity(string nativeName, string artworkName)
    {
        (string Name, Rect? Bounds)[] candidates = [(artworkName, NativeCell)];

        Assert.Equal(0, NativeTrayArtworkMatcher.FindMatch(nativeName, NativeCell, candidates));
    }

    [Fact]
    public void AmbiguousAnonymousGeometryFailsSafely()
    {
        (string Name, Rect? Bounds)[] candidates = [("Copilot", NativeCell), ("NVIDIA Settings", NativeCell)];

        Assert.Equal(-1, NativeTrayArtworkMatcher.FindMatch(string.Empty, NativeCell, candidates));
    }
}
