using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

/// <summary>
/// Nhập bốn loại mục và trang web từ đầu đến cuối.
/// </summary>
/// <remarks>
/// Các ca kiểm thử này gọi trực tiếp COM và Shell API <strong>thật</strong>, không thay thế bằng giả lập: hai lỗi giá trị nhất
/// ở giai đoạn 3 (<c>[GeneratedComInterface]</c> thiếu <c>[PreserveSig]</c> khiến giá trị trả về bị rác,
/// và <c>SHELLEXECUTEINFO</c> dư một trường khiến việc khởi chạy bị từ chối) đều chỉ lộ ra trên thành phần thật,
/// nếu thay bằng bản giả thì tất cả đều sẽ vượt qua.
/// </remarks>
public class ImportServiceTests
{
    [Fact]
    public void ImportFolder()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var folder = workspace.CreateDirectory("Thư mục tài liệu của tôi");

        var result = services.Import.ImportItems([new ImportRequest { Source = folder }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Folder, entry.Kind);
        Assert.Equal(folder, entry.Target);
        Assert.Equal("Thư mục tài liệu của tôi", entry.DisplayName);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void ImportFile()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("Báo cáo quý 2026.txt");

        var result = services.Import.ImportItems([new ImportRequest { Source = file }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.File, entry.Kind);

        // Tệp tài liệu giữ lại phần mở rộng: nếu bỏ đi thì "Báo cáo quý 2026" sẽ không nhận ra là loại gì.
        Assert.Equal("Báo cáo quý 2026.txt", entry.DisplayName);
    }

    [Fact]
    public void ImportProgram()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("Trình giả lập Notepad.cmd");

        var result = services.Import.ImportItems([new ImportRequest { Source = program }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Program, entry.Kind);

        // Chương trình bỏ phần mở rộng.
        Assert.Equal("Trình giả lập Notepad", entry.DisplayName);
    }

    [Fact]
    public void ImportUrl()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.net/duong-dan", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Url, entry.Kind);
        Assert.Equal("https://example.net/duong-dan", entry.Target);

        // Tên mặc định của trang web lấy theo tên máy chủ: cả đường dẫn đầy đủ không hiển thị hết trên giao diện.
        Assert.Equal("example.net", entry.DisplayName);
    }

    [Fact]
    public void ImportUrlRejectsNonWebProtocols()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "steam://run/1", IsUrl = true }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ImportFailureReason.UnsupportedType, failure.Reason);
    }

    [Fact]
    public void ImportShortcutWithArgumentsPreservesArgumentsAndIconIndex()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("Chương trình đích.cmd");
        var shortcut = Path.Combine(workspace.Root, "Lối tắt có tham số.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--flag \"giá trị có khoảng trắng\"",
            WorkingDirectory: workspace.Root,
            IconLocation: @"C:\Windows\System32\shell32.dll,3")));

        var result = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Program, entry.Kind);
        Assert.Equal(program, entry.Target);
        Assert.Equal("--flag \"giá trị có khoảng trắng\"", entry.Arguments);
        Assert.Equal(workspace.Root, entry.WorkingDirectory);

        // Chỉ số biểu tượng phải được giữ nguyên cùng với vị trí biểu tượng: nếu chỉ giữ shell32.dll thì sẽ lấy nhầm biểu tượng số 0,
        // hoàn toàn không phải biểu tượng mà người dùng thấy trên menu.
        Assert.Equal(@"C:\Windows\System32\shell32.dll,3", entry.IconSource);
    }

    [Fact]
    public void ImportShortcutPointingToUrlRecognizesAsUrl()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var shortcut = Path.Combine(workspace.Root, "Lối tắt trang web.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition("https://example.com/duong-dan co khoang trang")));

        var result = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        var entry = Assert.Single(result.Added);

        // Trường hợp này trước đây từng bị sai: GetPath trả về chuỗi rỗng đối với URL, nên nó bị coi là "không đọc được đích"
        // và bị hạ xuống thành một bản ghi lối tắt giữ nguyên .lnk.
        Assert.Equal(AppItemKind.Url, entry.Kind);
        Assert.StartsWith("https://example.com/", entry.Target, StringComparison.Ordinal);
        Assert.NotEqual(shortcut, entry.Target);
    }

    [Fact]
    public void ImportMissingPathReportsPerItemWithoutThrowing()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var missing = Path.Combine(workspace.Root, "Hoàn toàn không tồn tại.txt");

        var result = services.Import.ImportItems([new ImportRequest { Source = missing }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ImportFailureReason.NotFound, failure.Reason);
        Assert.Contains("Không tìm thấy", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportBatchWithOneBadSourceDoesNotAffectOthers()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var good = workspace.CreateProgram("Chương trình hợp lệ.cmd");
        var missing = Path.Combine(workspace.Root, "Lỗi.txt");

        var result = services.Import.ImportItems(
        [
            new ImportRequest { Source = good },
            new ImportRequest { Source = missing },
        ]);

        Assert.Single(result.Added);
        Assert.Single(result.Failures);

        // Mục đã được thêm vào thư viện phải được giữ lại: hoàn tác cả lô sẽ làm người dùng mất những mục vốn dùng được.
        Assert.NotNull(services.Library.Find(result.Added[0].Id));
    }

    [Fact]
    public void ImportSameTargetTwiceDetectsDuplicate()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("Chương trình.cmd");
        var request = new ImportRequest { Source = program };

        var first = services.Import.ImportItems([request]);
        Assert.Single(first.Added);

        var second = services.Import.ImportItems([request]);

        Assert.Empty(second.Added);
        var duplicate = Assert.Single(second.Duplicates);
        Assert.Equal(first.Added[0].Id, duplicate.Id);
    }

    [Fact]
    public void ImportSamePathDifferentCaseDetectsDuplicate()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("Chương trình.cmd");

        services.Import.ImportItems([new ImportRequest { Source = program }]);

        var second = services.Import.ImportItems([new ImportRequest { Source = program.ToUpperInvariant() }]);

        Assert.Empty(second.Added);
        Assert.Single(second.Duplicates);
    }

    [Fact]
    public void ImportSameTargetDifferentArgumentsIsNotDuplicate()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("Chương trình.cmd");
        var shortcut = Path.Combine(workspace.Root, "Có tham số.lnk");
        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(program, Arguments: "--second")));

        var direct = services.Import.ImportItems([new ImportRequest { Source = program }]);
        var withArguments = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        // Cùng một tệp thực thi nhưng kèm tham số khác nhau là hai hành động khởi chạy riêng biệt, gộp lại sẽ làm mất mục.
        Assert.Single(direct.Added);
        Assert.Single(withArguments.Added);
    }

    [Fact]
    public void ImportEmptyShortcutFileReportsUnreadable()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var empty = Path.Combine(workspace.Root, "Lối tắt rỗng.lnk");
        File.WriteAllBytes(empty, []);

        var result = services.Import.ImportItems([new ImportRequest { Source = empty }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);

        // Tệp rỗng không trỏ đến đường dẫn hay bất kỳ mục shell nào; nếu nhận vào sẽ chỉ tạo ra
        // một bản ghi bấm vào không có phản hồi.
        Assert.Equal(ImportFailureReason.ShortcutUnreadable, failure.Reason);
    }

    [Fact]
    public void ImportPreservesExistingLibraryEntries()
    {
        using var workspace = new TempWorkspace();
        var store = workspace.CreateStore();
        store.LoadAll();

        // Thêm thủ công một mục trước để mô phỏng "thư viện vốn đã có sẵn dữ liệu".
        var original = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Folder,
            Target = workspace.CreateDirectory("Thư mục sẵn có"),
            DisplayName = "Sẵn có",
        };

        Assert.Equal(AddEntryResult.Added, store.Library.Add(original));
        store.Library.Save();

        using var services = new AppServiceHost(store);
        var missing = Path.Combine(workspace.Root, "Không tồn tại.txt");

        services.Import.ImportItems(
        [
            new ImportRequest { Source = workspace.CreateProgram("Mới.cmd") },
            new ImportRequest { Source = missing },
        ]);

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();

        Assert.NotNull(reloaded.Find(original.Id));
        Assert.Equal(2, reloaded.Items.Count);
    }

    [Fact]
    public void ImportUrlDoesNotFailWhenNetworkUnavailable()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // Việc nhập URL chỉ lưu chuỗi, không truy cập mạng. Sản phẩm không thực hiện bất kỳ lời gọi mạng nào,
        // ca kiểm thử này khóa chặt điều đó: ngay cả khi địa chỉ không hề tồn tại vẫn có thể nhập.
        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://ten-mien-nay-chac-chan-khong-ton-tai.example/", IsUrl = true }]);

        Assert.Single(result.Added);
        Assert.Empty(result.Failures);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }
}