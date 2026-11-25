using System.Text;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

public sealed class LocalizedImportNameTests
{
    [Theory]
    [InlineData("EnglishTool.lnk", "Công cụ tiếng Việt", AppItemKind.Program)]
    [InlineData("EnglishSite.url", "Trang web tiếng Việt", AppItemKind.Url)]
    [InlineData("EnglishFolder", "Tài liệu tiếng Việt v2.0", AppItemKind.Folder)]
    [InlineData("EnglishProgram.cmd", "Chương trình tiếng Việt v2.0", AppItemKind.Program)]
    public void ImportsUseTheShellLabelInsteadOfThePhysicalFileName(string fileName, string label, AppItemKind kind)
    {
        using var workspace = new TempWorkspace();
        var source = CreateLocalizedItem(workspace, fileName, label);

        var (entry, _, _) = AppEntryFactory.TryCreate(new ImportRequest { Source = source });

        Assert.NotNull(entry);
        Assert.Equal(label, entry.DisplayName);
        Assert.Equal(kind, entry.Kind);
    }

    [Fact]
    public void AnExplicitNameStillOverridesTheShellLabel()
    {
        using var workspace = new TempWorkspace();
        var source = CreateLocalizedItem(workspace, "EnglishTool.lnk", "Công cụ tiếng Việt");

        var (entry, _, _) = AppEntryFactory.TryCreate(new ImportRequest { Source = source, DisplayName = " Tên của tôi " });

        Assert.Equal("Tên của tôi", entry!.DisplayName);
    }

    internal static string CreateLocalizedItem(TempWorkspace workspace, string fileName, string label)
    {
        var root = workspace.CreateDirectory("Bản địa hóa " + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, fileName);
        switch (Path.GetExtension(fileName))
        {
            case ".lnk":
                var program = workspace.CreateProgram("Mục tiêu thực tế.cmd");
                Assert.True(ShortcutWriter.Write(path, new ShortcutDefinition(program)));
                break;
            case ".url":
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.net/\r\n");
                break;
            case ".cmd":
                File.WriteAllText(path, "@echo off\r\n");
                break;
            default:
                Directory.CreateDirectory(path);
                break;
        }
        var ini = Path.Combine(root, "desktop.ini");
        File.WriteAllText(ini, $"[LocalizedFileNames]\r\n{fileName}={label}\r\n", Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(root, FileAttributes.Directory | FileAttributes.ReadOnly);
        return path;
    }
}
