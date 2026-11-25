using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

public sealed class NativeTrayKeyboardPolicyTests
{
    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x20)]
    public void OpeningDoesNotAuthorizeTheRememberedApp(int key)
    {
        var policy = new NativeTrayKeyboardPolicy();
        Assert.True(policy.ShouldConsume(key, down: true, trayHasFocus: true));
        Assert.True(policy.ShouldConsume(key, down: false, trayHasFocus: true));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x23)]
    [InlineData(0x24)]
    [InlineData(0x25)]
    [InlineData(0x26)]
    [InlineData(0x27)]
    [InlineData(0x28)]
    public void ExplicitKeyboardNavigationAllowsActivation(int navigationKey)
    {
        var policy = new NativeTrayKeyboardPolicy();
        Assert.False(policy.ShouldConsume(navigationKey, down: true, trayHasFocus: true));
        Assert.False(policy.ShouldConsume(0x0D, down: true, trayHasFocus: true));
    }

    [Fact]
    public void EnterInAnotherApplicationOrItsContextMenuPassesThrough()
    {
        var policy = new NativeTrayKeyboardPolicy();
        Assert.False(policy.ShouldConsume(0x0D, down: true, trayHasFocus: false));
        Assert.False(policy.ShouldConsume(0x0D, down: false, trayHasFocus: false));
    }

    [Fact]
    public void RepeatedEnterAndItsReleaseStayWithTheConsumedGesture()
    {
        var policy = new NativeTrayKeyboardPolicy();
        Assert.True(policy.ShouldConsume(0x0D, down: true, trayHasFocus: true));
        Assert.True(policy.ShouldConsume(0x0D, down: true, trayHasFocus: false));
        Assert.True(policy.ShouldConsume(0x0D, down: false, trayHasFocus: false));
        Assert.False(policy.ShouldConsume(0x0D, down: true, trayHasFocus: false));
    }
}
