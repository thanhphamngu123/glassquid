using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

public sealed class DesktopScannerTests
{
    [Fact]
    public void CombinesBothDesktopsWithUserOverridesAndOmitsHiddenFilesWithoutRecursing()
    {
        using var workspace = new TempWorkspace();
        var user = workspace.CreateDirectory("Desktop người dùng");
        var shared = workspace.CreateDirectory("Desktop công cộng");
        workspace.CreateFile("Desktop người dùng/Trùng tên.txt");
        workspace.CreateFile("Desktop công cộng/Trùng tên.txt");
        workspace.CreateFile("Desktop công cộng/Công cộng.txt");
        var folder = workspace.CreateDirectory("Desktop người dùng/Tài liệu");
        workspace.CreateFile("Desktop người dùng/Tài liệu/Không mở rộng.txt");
        var hidden = workspace.CreateFile("Desktop người dùng/Ẩn.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = workspace.CreateFile("Desktop công cộng/desktop.ini");
        File.SetAttributes(system, FileAttributes.System);

        var scan = DesktopScanner.Scan(user, shared);

        Assert.Empty(scan.Warnings);
        Assert.Equal(3, scan.Items.Count);
        Assert.Contains(scan.Items, item => item.Path == Path.Combine(user, "Trùng tên.txt") && item.FromCurrentUser);
        Assert.Contains(scan.Items, item => item.Path == Path.Combine(shared, "Công cộng.txt") && !item.FromCurrentUser);
        Assert.Contains(scan.Items, item => item.Path == Path.GetFullPath(folder) && item.IsDirectory);
    }

    [Fact]
    public void UsesLocalizedLabelsAndKeepsDifferentItemsWithTheSameLabel()
    {
        using var workspace = new TempWorkspace();
        var path = LocalizedImportNameTests.CreateLocalizedItem(workspace, "EnglishTool.lnk", "Công cụ tiếng Việt");
        var user = Path.GetDirectoryName(path)!;
        var other = Path.Combine(user, "OtherTool.lnk");
        Assert.True(ShortcutWriter.Write(other, new ShortcutDefinition(workspace.CreateProgram("Mục tiêu khác.cmd"))));
        var ini = Path.Combine(user, "desktop.ini");
        File.SetAttributes(ini, FileAttributes.Normal);
        File.AppendAllText(ini, "OtherTool.lnk=Công cụ tiếng Việt\r\n", System.Text.Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        var scan = DesktopScanner.Scan(user, null);

        Assert.Equal(2, scan.Items.Count);
        Assert.Equal("Công cụ tiếng Việt", scan.Items.Single(item => item.Path == path).DisplayName);
        Assert.Equal("Công cụ tiếng Việt", scan.Items.Single(item => item.Path == other).DisplayName);
        Assert.DoesNotContain(scan.Items, item => Path.GetFileName(item.Path) == "desktop.ini");
    }

    [Fact]
    public void MissingDesktopsAreEmptyAndTheSameRootIsNotEnumeratedTwice()
    {
        using var workspace = new TempWorkspace();
        var desktop = workspace.CreateDirectory("Desktop");
        workspace.CreateFile("Desktop/Tệp.txt");

        Assert.Empty(DesktopScanner.Scan(null, Path.Combine(workspace.Root, "missing")).Items);
        var item = Assert.Single(DesktopScanner.Scan(desktop, desktop).Items);
        Assert.True(item.FromCurrentUser);
    }

    [Fact]
    public void ImportingASelectionAddsAllGoodItemsAndPreservesExistingNamesWhenOneSourceDisappears()
    {
        using var workspace = new TempWorkspace();
        var desktop = workspace.CreateDirectory("Desktop");
        var first = workspace.CreateFile("Desktop/Một.txt");
        workspace.CreateFile("Desktop/Hai.txt");
        var removed = workspace.CreateFile("Desktop/Ba.txt");
        var data = workspace.CreateStore();
        data.LoadAll();
        using var services = new AppServiceHost(data);
        var existing = Assert.Single(services.Import.ImportItems([new ImportRequest { Source = first, DisplayName = "Tên tùy chỉnh của tôi" }]).Added);
        var selected = DesktopScanner.Scan(desktop, null).Items;
        File.Delete(removed);

        var result = services.Import.ImportItems(selected.Select(item => new ImportRequest { Source = item.Path }).ToList());

        Assert.Single(result.Added);
        Assert.Equal(existing.Id, Assert.Single(result.Duplicates).Id);
        Assert.Equal(ImportFailureReason.NotFound, Assert.Single(result.Failures).Reason);
        Assert.Equal("Tên tùy chỉnh của tôi", services.Library.Find(existing.Id)!.DisplayName);
        Assert.Equal(2, LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count());
    }
}
