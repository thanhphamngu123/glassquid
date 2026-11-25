using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

/// <summary>Xác định loại mục và tên hiển thị theo đường dẫn.</summary>
public class ItemKindDetectorTests
{
    [Theory]
    [InlineData("app.exe", AppItemKind.Program)]
    [InlineData("app.EXE", AppItemKind.Program)]
    [InlineData("kịch bản.cmd", AppItemKind.Program)]
    [InlineData("kịch bản.bat", AppItemKind.Program)]
    [InlineData("lối tắt.lnk", AppItemKind.Shortcut)]
    [InlineData("trang web.url", AppItemKind.Url)]
    [InlineData("báo cáo.docx", AppItemKind.File)]
    [InlineData("không có phần mở rộng", AppItemKind.File)]
    public void DetectsKindByExtension(string fileName, AppItemKind expected)
        => Assert.Equal(expected, ItemKindDetector.Detect(fileName, isDirectory: false));

    [Fact]
    public void DirectoryTakesPrecedenceOverExtension()
    {
        // Một thư mục tên là «công cụ.exe» vẫn là thư mục — nếu xem phần mở rộng trước sẽ nhận nhầm thành chương trình,
        // rồi khi khởi chạy lại đi thực thi một thư mục, kết quả hoàn toàn khó hiểu.
        Assert.Equal(AppItemKind.Folder, ItemKindDetector.Detect(@"D:\công cụ.exe", isDirectory: true));
    }

    [Fact]
    public void InstallerPackageIsNotTreatedAsProgram()
    {
        // .msi khởi chạy trình cài đặt; đánh dấu nó là chương trình sẽ khiến cơ chế «đang chạy thì kích hoạt»
        // đi khớp với tiến trình cài đặt.
        Assert.Equal(AppItemKind.File, ItemKindDetector.Detect("setup.msi", isDirectory: false));
    }

    [Theory]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("ghi chú.cmd", "ghi chú")]
    [InlineData("lối tắt có dấu cách.lnk", "lối tắt có dấu cách")]
    public void ProgramAndShortcutStripExtension(string path, string expected)
        => Assert.Equal(expected, ItemKindDetector.DeriveDisplayName(path, ItemKindDetector.Detect(path, false)));

    [Theory]
    [InlineData("báo cáo quý 2026.txt")]
    [InlineData("lưu trữ.zip")]
    public void DocumentKeepsExtension(string path)
        => Assert.Equal(Path.GetFileName(path), ItemKindDetector.DeriveDisplayName(path, AppItemKind.File));

    [Fact]
    public void FolderNameUsesLastSegment()
    {
        Assert.Equal("tài liệu của tôi", ItemKindDetector.DeriveDisplayName(@"D:\a\tài liệu của tôi", AppItemKind.Folder));
    }
}

/// <summary>Đọc tệp <c>.url</c> (lối tắt Internet).</summary>
public class InternetShortcutReaderTests
{
    [Fact]
    public void ReadsUrlLine()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile(
            "trang web.url",
            "[InternetShortcut]" + Environment.NewLine + "URL=https://example.org/a b" + Environment.NewLine);

        Assert.Equal("https://example.org/a b", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void IgnoresCommentsAndBlankLines()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile(
            "trang web.url",
            "; Đây là chú thích" + Environment.NewLine
            + Environment.NewLine
            + "[InternetShortcut]" + Environment.NewLine
            + "IconFile=whatever.ico" + Environment.NewLine
            + "URL=http://example.net/" + Environment.NewLine);

        Assert.Equal("http://example.net/", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("trang web.url", "url=https://example.com/" + Environment.NewLine);

        Assert.Equal("https://example.com/", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void ReturnsNullWhenUrlLineIsMissing()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("hỏng.url", "Đây không phải là một lối tắt Internet" + Environment.NewLine);

        Assert.Null(InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void ReturnsNullInsteadOfThrowingWhenFileDoesNotExist()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(InternetShortcutReader.ReadUrl(Path.Combine(workspace.Root, "không tồn tại.url")));
    }

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("file:///C:/x.txt", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("steam://run/1", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyWebProtocols(string? value, bool expected)
        => Assert.Equal(expected, InternetShortcutReader.IsSupportedUrl(value));

    [Fact]
    public void ComposedContentCanBeReadBack()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("khứ hồi.url", InternetShortcutReader.Compose("https://example.com/đường dẫn có dấu cách"));

        Assert.Equal("https://example.com/đường dẫn có dấu cách", InternetShortcutReader.ReadUrl(path));
    }
}

/// <summary>Xác định định dạng ảnh theo phần đầu tệp.</summary>
public class ImageFormatSnifferTests
{
    [Theory]
    [InlineData("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ImageFormat.Png)]
    [InlineData("a.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageFormat.Jpeg)]
    [InlineData("a.bmp", new byte[] { 0x42, 0x4D, 0x00, 0x00 }, ImageFormat.Bmp)]
    [InlineData("a.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ImageFormat.Gif)]
    [InlineData("a.ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, ImageFormat.Ico)]
    public void DetectsFormatByContent(string name, byte[] content, ImageFormat expected)
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, name);
        File.WriteAllBytes(path, content);

        Assert.Equal(expected, ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void InspectsContentInsteadOfExtension()
    {
        // Người dùng hoàn toàn có thể lưu một tệp PNG dưới đuôi .jpg. Nếu phán đoán theo phần mở rộng thì sẽ chọn sai bộ giải mã.
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "thực ra là png.jpg");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.Equal(ImageFormat.Png, ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void ReturnsNullForUnrecognizedContent()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("tài liệu.pdf", "%PDF-1.7 Đây là một tệp PDF");

        Assert.Null(ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void ReturnsNullForEmptyFile()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "trống.png");
        File.WriteAllBytes(path, []);

        Assert.Null(ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void ReturnsNullInsteadOfThrowingWhenImageFileDoesNotExist()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(ImageFormatSniffer.Detect(Path.Combine(workspace.Root, "không tồn tại.png")));
    }

    [Theory]
    [InlineData(ImageFormat.Png, ".png")]
    [InlineData(ImageFormat.Jpeg, ".jpg")]
    [InlineData(ImageFormat.Ico, ".ico")]
    public void ExtensionMatchesFormat(ImageFormat format, string expected)
        => Assert.Equal(expected, ImageFormatSniffer.FileExtensionFor(format));
}

/// <summary>Tách vị trí biểu tượng dạng «đường dẫn,chỉ mục».</summary>
public class IconSourceSplitTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\shell32.dll,3", @"C:\Windows\System32\shell32.dll", 3)]
    [InlineData(@"C:\Windows\System32\shell32.dll,-1", @"C:\Windows\System32\shell32.dll", -1)]
    [InlineData(@"C:\app.exe,0", @"C:\app.exe", 0)]
    public void SplitsPathAndIndex(string source, string expectedPath, int expectedIndex)
    {
        var (path, index) = IconExtractor.SplitIconSource(source);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void CommaInDirectoryNameIsNotTreatedAsIndexSeparator()
    {
        // D:\a,b\app.exe là đường dẫn hợp lệ. Nếu tìm dấu phẩy từ đầu sẽ tách nó thành
        // «D:\a» và «b\app.exe», khiến nguồn biểu tượng bị sai hoàn toàn.
        var (path, index) = IconExtractor.SplitIconSource(@"D:\a,b\app.exe");

        Assert.Equal(@"D:\a,b\app.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void DoesNotTreatNonIntegerAfterCommaAsIndex()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"D:\tài liệu,sao lưu\app.exe");

        Assert.Equal(@"D:\tài liệu,sao lưu\app.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void ReturnsOriginalPathWhenIndexIsAbsent()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"C:\Windows\notepad.exe");

        Assert.Equal(@"C:\Windows\notepad.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void TrailingCommaIsNotTreatedAsIndex()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"C:\app.exe,");

        Assert.Equal(@"C:\app.exe,", path);
        Assert.Null(index);
    }

    [Fact]
    public void TrimsLeadingAndTrailingWhitespace()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"  C:\app.exe, 5  ");

        Assert.Equal(@"C:\app.exe", path);
        Assert.Equal(5, index);
    }
}