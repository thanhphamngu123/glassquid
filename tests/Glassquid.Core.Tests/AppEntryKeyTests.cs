using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class AppEntryKeyTests
{
    private static AppEntry Entry(string target, AppItemKind kind = AppItemKind.Program, string? arguments = null) =>
        new()
        {
            Id = "x",
            DisplayName = "x",
            Kind = kind,
            Target = target,
            Arguments = arguments,
        };

    [Fact]
    public void Compute_IsCaseInsensitive_ForPaths()
    {
        // Đường dẫn Windows không phân biệt chữ hoa/chữ thường, nên phải phát hiện được khi nhập trùng cùng một chương trình,
        // bất kể đường dẫn được nhập theo cách nào.
        var a = AppEntryKey.Compute(Entry(@"C:\Windows\notepad.exe"));
        var b = AppEntryKey.Compute(Entry(@"c:\WINDOWS\NOTEPAD.EXE"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_NormalizesForwardAndBackSlashes()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\Editor\app.exe"));
        var b = AppEntryKey.Compute(Entry("C:/Tools/Editor/app.exe"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_CollapsesRedundantSegments()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\sub\..\app.exe"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_IgnoresATrailingSeparator()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools", AppItemKind.Folder));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\", AppItemKind.Folder));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DiffersWhenArgumentsDiffer()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "--work"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "--home"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_TreatsAbsentAndEmptyArgumentsAlike()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "   "));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_FoldsUrlSchemeAndHostButKeepsPathCasing()
    {
        var a = AppEntryKey.Compute(Entry("HTTPS://Example.COM/Docs/Page", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com/Docs/Page", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DistinguishesUrlsByPort()
    {
        // Hồi quy (kiểm tra lại Giai đoạn 3): trong khóa chỉ có giao thức và máy chủ, cổng đã bị bỏ sót,
        // nên hai dịch vụ khác nhau là localhost:8001 và localhost:8002 bị coi là cùng một mục.
        var a = AppEntryKey.Compute(Entry("http://localhost:8001/app", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("http://localhost:8002/app", AppItemKind.Url));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_TreatsAnExplicitDefaultPortAsOmitted()
    {
        // Bỏ qua cổng và ghi rõ cổng mặc định đều là cùng một địa chỉ, không nên coi là hai mục.
        var a = AppEntryKey.Compute(Entry("https://example.com/", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com:443/", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_TreatsAnExplicitHttpDefaultPortAsOmitted()
    {
        var a = AppEntryKey.Compute(Entry("http://example.com/", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("http://example.com:80/", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_KeepsUrlPathsDistinctByCase()
    {
        // Khác với đường dẫn tệp Windows, đường dẫn URL có phân biệt chữ hoa/chữ thường.
        var a = AppEntryKey.Compute(Entry("https://example.com/docs", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com/Docs", AppItemKind.Url));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DoesNotThrow_ForAnUnusableTarget()
    {
        var key = AppEntryKey.Compute(Entry("\0invalid\0"));

        Assert.NotNull(key);
    }

    [Fact]
    public void Compute_IsEmpty_ForAnEmptyTarget()
    {
        Assert.Equal(string.Empty, AppEntryKey.Compute(Entry(string.Empty)));
    }
}