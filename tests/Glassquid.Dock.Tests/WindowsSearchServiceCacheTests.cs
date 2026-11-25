using System.Text.Json;
using Glassquid.Dock;
using Xunit;

namespace Glassquid.Dock.Tests;

public sealed class WindowsSearchServiceCacheTests
{
    [Fact]
    public void SearchDockAppsCacheData_SerializesAndDeserializesCorrectly()
    {
        var data = new WindowsSearchService.SearchDockAppsCacheData
        {
            Version = 1,
            CachedAtUtc = DateTime.UtcNow,
            Records =
            [
                new() { Name = "Visual Studio Code", TargetPath = @"C:\VSCode\Code.exe", ShortcutPath = @"C:\Desktop\Code.lnk" },
                new() { Name = "Terminal", TargetPath = @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", ShortcutPath = null }
            ]
        };

        var json = JsonSerializer.Serialize(data);
        var deserialized = JsonSerializer.Deserialize<WindowsSearchService.SearchDockAppsCacheData>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal(2, deserialized.Records.Count);
        Assert.Equal("Visual Studio Code", deserialized.Records[0].Name);
        Assert.Equal(@"C:\VSCode\Code.exe", deserialized.Records[0].TargetPath);
        Assert.Equal(@"C:\Desktop\Code.lnk", deserialized.Records[0].ShortcutPath);
        Assert.Equal("Terminal", deserialized.Records[1].Name);
    }

    [Fact]
    public async Task SearchAsync_WithPopulatedCache_FindsMatchingApp()
    {
        WindowsSearchService.PopulateAppsCacheForTesting([
            new("Notepad Test", @"C:\Windows\notepad.exe"),
            new("Calculator Test", @"C:\Windows\calc.exe"),
            new("Visual Studio Test", @"C:\VS\devenv.exe")
        ]);

        var results = await WindowsSearchService.SearchAsync("Notepad", CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.DisplayName == "Notepad Test");
    }

    [Fact]
    public async Task SearchAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        WindowsSearchService.PopulateAppsCacheForTesting([
            new("App1", @"C:\app1.exe"),
            new("App2", @"C:\app2.exe")
        ]);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await WindowsSearchService.SearchAsync("App", cts.Token);
        });
    }

    [Fact]
    public void GetIconCacheForTesting_ReturnsBoundedLruCache()
    {
        var cache = WindowsSearchService.GetIconCacheForTesting();
        Assert.NotNull(cache);
        Assert.Equal(16L * 1024 * 1024, cache.MaxBytes);
        Assert.Equal(512, cache.MaxCount);
    }
}
