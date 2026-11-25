using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

public sealed class NativeTrayChevronPolicyTests
{
    [Theory]
    [InlineData("SystemTrayIcon", "Copilot")]
    [InlineData("SystemTrayIcon", "ChatGPT")]
    [InlineData("SystemTrayIcon", "More tools")]
    [InlineData("SystemTrayIcon", "Hidden application")]
    public void GenericTrayIconIdNeverIdentifiesAnApplicationAsChevron(string automationId, string name)
    {
        Assert.False(TrayIconContextMenuRelay.IsNativeChevronCandidate(automationId, name));
    }

    [Theory]
    [InlineData("SystemTrayIcon", "Show Hidden Icons")]
    [InlineData("SystemTrayIcon", "Show Hidden Icons Hide")]
    [InlineData("SystemTrayIcon", "Hiển thị các biểu tượng ẩn")]
    [InlineData("SystemTrayIcon", "Hiển thị các biểu tượng bị ẩn")]
    [InlineData("ShowMoreIcons", "")]
    public void WindowsChevronRemainsRecognizedInOpenAndClosedStates(string automationId, string name)
    {
        Assert.True(TrayIconContextMenuRelay.IsNativeChevronCandidate(automationId, name));
    }
}
