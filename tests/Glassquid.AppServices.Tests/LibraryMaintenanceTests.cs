using Glassquid.Core.Contracts;
using Glassquid.Core.Imaging;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices.Tests;

/// <summary>
/// Giữ lại và định vị lại mục tiêu bị mất (R-07).
/// </summary>
/// <remarks>
/// R-07 yêu cầu khi đường dẫn bị lỗi thì <strong>giữ lại</strong> mục và cho phép định vị lại, thay vì để nó biến mất.
/// Do đó trọng tâm các ca kiểm thử ở đây không nằm ở "có phát hiện được lỗi hay không", mà ở
/// "sau khi phát hiện thì mục có còn hay không, bố cục có còn tham chiếu đến nó không, và người dùng có khôi phục được nó hay không".
/// </remarks>
public class LibraryMaintenanceTests
{
    [Fact]
    public void EntryIsPreservedAndMarkedMissingWhenTargetIsDeleted()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("tệp sẽ biến mất.txt");
        var entry = ImportOne(services, file);

        File.Delete(file);

        var report = services.Maintenance.RefreshMissingTargets();

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.Missing);
        Assert.True(report.Changed);

        // Điểm mấu chốt là "giữ lại": mục phải còn đó, chỉ là được đánh dấu.
        var after = services.Library.Find(entry.Id);
        Assert.NotNull(after);
        Assert.True(after.IsTargetMissing);
        Assert.Equal(file, after.Target);
    }

    [Fact]
    public void MissingFlagIsPersistedToFile()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("tệp sẽ biến mất.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);

        services.Maintenance.RefreshMissingTargets();

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();

        // Chỉ đánh dấu trong bộ nhớ là chưa đủ: sau khi khởi động lại mục sẽ hiển thị bình thường trở lại,
        // người dùng bấm vào mới phát hiện không mở được.
        Assert.True(reloaded.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void MissingFlagIsClearedWhenTargetReturns()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("tệp sẽ biến mất.txt");
        var entry = ImportOne(services, file);

        File.Delete(file);
        services.Maintenance.RefreshMissingTargets();

        File.WriteAllText(file, "Đã quay trở lại");

        var report = services.Maintenance.RefreshMissingTargets();

        Assert.Single(report.Recovered);
        Assert.False(services.Library.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void DoesNotWriteFileWhenStatusIsUnchanged()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        ImportOne(services, workspace.CreateFile("tệp.txt"));

        var first = services.Maintenance.RefreshMissingTargets();
        var second = services.Maintenance.RefreshMissingTargets();

        // Ghi không điều kiện mỗi khi khởi động sẽ làm thời gian sửa đổi của tệp thay đổi mỗi lần mở máy,
        // đồng thời làm mất ý nghĩa của thông tin "lần thay đổi cuối là khi nào".
        Assert.False(first.Changed);
        Assert.False(second.Changed);
    }

    [Fact]
    public void UrlsAreNeverMarkedMissing()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.com/", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        var report = services.Maintenance.RefreshMissingTargets();

        // Địa chỉ web có mở được hay không phụ thuộc vào mạng, không phụ thuộc vào tệp trên máy.
        Assert.Equal(0, report.Missing);
        Assert.False(services.Library.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void RelocateUpdatesTargetClearsMissingFlagAndUpdatesIconSource()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("tệp sẽ biến mất.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);
        services.Maintenance.RefreshMissingTargets();

        var replacement = workspace.CreateFile("tệp thay thế.txt");
        var outcome = services.Maintenance.Relocate(entry.Id, replacement);

        Assert.Equal(RelocateResult.Relocated, outcome.Result);

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();
        var after = reloaded.Find(entry.Id);

        Assert.Equal(replacement, after?.Target);
        Assert.False(after?.IsTargetMissing);

        // Nguồn biểu tượng phải được cập nhật theo. Nếu không đổi, bản ghi trên giao diện sẽ vẫn giữ biểu tượng cũ
        // và tiếp tục hiển thị là bị lỗi, khiến người dùng tưởng rằng chưa đổi thành công.
        Assert.Equal(replacement, after?.IconSource);
    }

    [Fact]
    public void RelocateToNonExistentLocationIsRejected()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));

        var outcome = services.Maintenance.Relocate(entry.Id, Path.Combine(workspace.Root, "không-tồn-tại.txt"));

        Assert.Equal(RelocateResult.TargetNotFound, outcome.Result);
        Assert.Equal(workspace.Root, Path.GetDirectoryName(services.Library.Find(entry.Id)?.Target));
    }

    [Fact]
    public void RelocateToOccupiedLocationIsRejectedWithoutChanges()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var first = ImportOne(services, workspace.CreateFile("thứ-nhất.txt"));
        var second = ImportOne(services, workspace.CreateFile("thứ-nhì.txt"));

        var outcome = services.Maintenance.Relocate(second.Id, first.Target);

        // Cố tình ghi đè sẽ làm hai bản ghi có cùng khóa chống trùng lặp — từ đó thư viện ứng dụng sẽ có một cặp
        // mục trùng lặp không thể phân biệt, đây chính là loại lỗi mà giai đoạn 2 đã phải viết 10 bài kiểm thử để ngăn chặn.
        Assert.Equal(RelocateResult.ConflictsWithExisting, outcome.Result);
        Assert.Equal(first.Id, outcome.ItemId);
        Assert.Equal(workspace.CreateFile("thứ-nhì.txt"), services.Library.Find(second.Id)?.Target);
    }

    [Fact]
    public void UrlsCannotBeRelocated()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.com/", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        var outcome = services.Maintenance.Relocate(entry.Id, workspace.CreateFile("tùy-ý.txt"));

        Assert.Equal(RelocateResult.NotRelocatable, outcome.Result);
    }

    [Fact]
    public void NonExistentEntryReportsUnknownInsteadOfThrowing()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var outcome = services.Maintenance.Relocate("id-hoàn-toàn-không-tồn-tại", workspace.CreateFile("tệp.txt"));

        Assert.Equal(RelocateResult.UnknownItem, outcome.Result);
    }

    [Fact]
    public void RelocateToDirectoryIsRecognizedAsFolder()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        var folder = workspace.CreateDirectory("thư mục mới");

        var outcome = services.Maintenance.Relocate(entry.Id, folder);

        Assert.Equal(RelocateResult.Relocated, outcome.Result);
        Assert.Equal(AppItemKind.Folder, services.Library.Find(entry.Id)?.Kind);
    }

    [Fact]
    public void CustomIconIsCopiedToCacheDirectoryAndAttachedToEntry()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));

        var userIcon = Path.Combine(workspace.Root, "biểu tượng của tôi.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));

        var after = services.Library.Find(entry.Id);

        // Sao chép vào thay vì chỉ lưu đường dẫn bên ngoài: nếu sau này người dùng xóa hoặc di chuyển tệp gốc,
        // chỉ lưu đường dẫn bên ngoài đồng nghĩa với việc mất biểu tượng.
        Assert.NotNull(after?.CustomIconPath);
        Assert.NotEqual(userIcon, after.CustomIconPath);
        Assert.StartsWith(workspace.DataPaths.IconCacheDirectory, after.CustomIconPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(after.CustomIconPath));

        // Nguồn biểu tượng trên mục giữ nguyên, sau khi xóa bộ nhớ đệm vẫn có thể trích xuất lại.
        Assert.Equal(entry.Target, after.IconSource);
    }

    [Fact]
    public void UnrecognizedCustomIconFormatIsRejected()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        var notAnImage = workspace.CreateFile("trông-giống-biểu-tượng.png", "%PDF-1.7 thực ra là PDF");

        Assert.False(services.Maintenance.SetCustomIcon(entry.Id, notAnImage));
        Assert.Null(services.Library.Find(entry.Id)?.CustomIconPath);
    }

    [Fact]
    public void RemovingCustomIconDeletesCachedFile()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        var userIcon = Path.Combine(workspace.Root, "biểu tượng của tôi.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        services.Maintenance.SetCustomIcon(entry.Id, userIcon);
        var cached = services.Library.Find(entry.Id)!.CustomIconPath!;

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, null));

        Assert.Null(services.Library.Find(entry.Id)?.CustomIconPath);
        Assert.False(File.Exists(cached));
    }

    [Fact]
    public void OldLocationCanBeReimportedAfterRelocate()
    {
        // Hồi quy (rà soát giai đoạn 3): sau khi đổi A sang B, trong _byKey vẫn còn giữ khóa của A,
        // dẫn đến khi nhập lại A nhận được kết quả "thêm mới 0, trùng lặp 1" — một lần nhập hợp lệ bị từ chối.
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var original = workspace.CreateFile("vị trí cũ.txt");
        var entry = ImportOne(services, original);
        var replacement = workspace.CreateFile("vị trí mới.txt");

        var relocated = services.Maintenance.Relocate(entry.Id, replacement);
        Assert.Equal(RelocateResult.Relocated, relocated.Result);

        // Vị trí cũ giờ đã trống, đáng lẽ phải có thể tiếp nhận lại như một bản ghi mới.
        var again = services.Import.ImportItems([new ImportRequest { Source = original }]);

        Assert.Empty(again.Failures);
        Assert.Single(again.Added);
        Assert.Empty(again.Duplicates);
        Assert.Equal(original, again.Added[0].Target);
    }

    [Fact]
    public void BothOldLocationsCanBeReimportedAfterRoundTripRelocate()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var a = workspace.CreateFile("vị trí A.txt");
        var b = workspace.CreateFile("vị trí B.txt");
        var entry = ImportOne(services, a);

        Assert.Equal(RelocateResult.Relocated, services.Maintenance.Relocate(entry.Id, b).Result);
        Assert.Equal(RelocateResult.Relocated, services.Maintenance.Relocate(entry.Id, a).Result);

        var importB = services.Import.ImportItems([new ImportRequest { Source = b }]);

        Assert.Single(importB.Added);
        Assert.Empty(importB.Duplicates);
    }

    [Fact]
    public void CustomIconRemainsAvailableAfterClearingCache()
    {
        // Hồi quy (rà soát giai đoạn 3): biểu tượng tùy chỉnh không phải là bộ nhớ đệm có thể tái tạo — nó được sao chép
        // từ tệp do người dùng chọn, tệp gốc có thể đã không còn tồn tại từ lâu. Việc dọn bộ nhớ đệm không được xóa nó.
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        var userIcon = Path.Combine(workspace.Root, "biểu tượng của tôi.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));
        var cachedPath = services.Library.Find(entry.Id)!.CustomIconPath!;
        Assert.True(File.Exists(cachedPath));

        services.Icons.Clear();

        Assert.True(File.Exists(cachedPath), $"Biểu tượng tùy chỉnh đã bị xóa: {cachedPath}");
        Assert.Equal(cachedPath, services.Library.Find(entry.Id)!.CustomIconPath);
    }

    private static AppEntry ImportOne(AppServiceHost services, string source)
    {
        var result = services.Import.ImportItems([new ImportRequest { Source = source }]);
        return Assert.Single(result.Added);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }

    [Fact]
    public void RejectedRelocateDoesNotLeavePartiallyUpdatedEntry()
    {
        // Hồi quy: phát hiện khi rà soát. Khi mục tiêu đổi từ tệp sang thư mục và việc cập nhật bị từ chối,
        // phiên bản cũ chỉ khôi phục Target / IconSource / IsTargetMissing mà bỏ sót Kind,
        // dẫn đến loại của bản ghi vẫn là thư mục, nhưng lại trỏ về tệp ban đầu.
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("một tệp.txt");
        var entry = ImportOne(services, file);
        var previousKind = entry.Kind;
        var previousTarget = entry.Target;
        var previousIconSource = entry.IconSource;

        // Đặt trước một bản ghi trỏ đến thư mục vào thư viện để tạo xung đột khóa.
        var folder = ImportOne(services, workspace.CreateDirectory("một thư mục"));
        Assert.Equal(RelocateResult.ConflictsWithExisting, services.Maintenance.Relocate(entry.Id, folder.Target).Result);

        var after = services.Library.Find(entry.Id)!;

        Assert.Equal(previousTarget, after.Target);
        Assert.Equal(previousKind, after.Kind);
        Assert.Equal(previousIconSource, after.IconSource);
        Assert.False(after.IsTargetMissing);

        // Chỉ mục cũng phải toàn vẹn: hai khóa vẫn thuộc về riêng từng mục, không bên nào đè mất bên nào.
        Assert.NotNull(services.Library.FindByKey(Glassquid.Core.Storage.AppEntryKey.Compute(new AppEntry
        {
            Kind = previousKind,
            Target = previousTarget,
        })));
        Assert.NotNull(services.Library.FindByKey(Glassquid.Core.Storage.AppEntryKey.Compute(new AppEntry
        {
            Kind = folder.Kind,
            Target = folder.Target,
        })));
    }
}

/// <summary>Bộ nhớ đệm biểu tượng.</summary>
public class IconCacheTests
{
    [Fact]
    public void ExtractedIconIsWrittenAsValidPng()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // Dùng tệp thực thi chắc chắn tồn tại trong hệ thống, như vậy không phụ thuộc vào thiết lập liên kết của shell.
        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");

        Assert.NotNull(cached);
        Assert.True(cached.CreatedFile);
        Assert.True(File.Exists(cached.Path));

        // Không chỉ là "tệp có tồn tại": phần đầu phải là chữ ký PNG, kích thước phải đọc được từ bên trong tệp.
        var header = File.ReadAllBytes(cached.Path).Take(8).ToArray();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header);
        Assert.True(cached.Width > 0);
        Assert.True(cached.Height > 0);
    }

    [Fact]
    public void SameSourceReusesExistingCachedFile()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var first = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        var second = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(second.CreatedFile);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, services.Icons.FileCount);
    }

    [Fact]
    public void DifferentSourcesMapToDifferentFiles()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll,3");

        Assert.Equal(2, services.Icons.FileCount);
    }

    [Fact]
    public void IconSourceRemainsUsableAfterClearingCache()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        Assert.NotNull(services.Library.Find(entry.Id)?.IconSource);

        var removed = services.Icons.Clear();

        Assert.True(removed.Removed > 0);
        Assert.Equal(0, services.Icons.FileCount);

        // Đây là lý do vì sao trên mục lưu "nguồn" chứ không lưu đường dẫn bộ nhớ đệm:
        // bộ nhớ đệm có thể bị xóa, còn nguồn thì không.
        var again = services.Icons.EnsureFor(services.Library.Find(entry.Id)!.IconSource);
        Assert.NotNull(again);
    }

    [Fact]
public void EmptySourceReturnsNullInsteadOfThrowing()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        Assert.Null(services.Icons.EnsureFor(null));
        Assert.Null(services.Icons.EnsureFor(""));
        Assert.Null(services.Icons.EnsureFor("   "));
    }

    [Fact]
    public void MissingTargetFallsBackToFileTypeIcon()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // Đây là chủ ý thiết kế: sau khi mục bị lỗi vẫn phải có một hình ảnh để hiển thị, thay vì để trống hoàn toàn.
        // Cách làm trong shell cũng như vậy (SHGFI_USEFILEATTRIBUTES sinh ra là để làm việc này),
        // "bị lỗi" được thể hiện qua dấu hiệu trên mục, không phải qua việc thiếu hình ảnh.
        var cached = services.Icons.EnsureFor(Path.Combine(workspace.Root, "không-tồn-tại.exe"));

        Assert.NotNull(cached);
        Assert.True(cached.Width > 0);
    }

[Fact]
public void MalformedSourcesReturnWithoutThrowing()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // Không lấy được biểu tượng chỉ là thiếu một hình, tuyệt đối không được biến thành ngoại lệ — nếu không nó sẽ lan ngược lên bước nhập
        // và trở thành một lỗi nhập dữ liệu. Ở đây kiểm tra qua một số dạng đầu vào dị dạng để xác nhận bề mặt gọi hàm ổn định.
        string?[] hostile =
        [
            null,
            "",
            "   ",
            ",",
            ",5",
            @"ổ-đĩa-không-tồn-tại:\x.exe",
            @"C:\Windows\System32\shell32.dll,99999",
            Path.Combine(workspace.Root, "không phần mở rộng cũng không có tệp"),
            new string('x', 3000),
        ];

        foreach (var source in hostile)
        {
            var exception = Record.Exception(() => services.Icons.EnsureFor(source));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void IndexedIconDiffersFromDefaultIconOfSameFile()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var byIndex = services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll,3");
        var byDefault = services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll");

        Assert.NotNull(byIndex);
        Assert.NotNull(byDefault);

        // Nội dung phải thực sự khác nhau, nếu không thì việc "giữ lại chỉ mục" chỉ là nói suông.
        Assert.NotEqual(
            File.ReadAllBytes(byIndex.Path),
            File.ReadAllBytes(byDefault.Path));
    }

    [Fact]
    public void CachedFileDimensionsMatchExtractedResult()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(cached);

        // Đọc kích thước từ chính IHDR của PNG, thay vì đọc từ giá trị trả về của bộ trích xuất —
        // giá trị trả về không chứng minh được mấy byte đã ghi xuống là đúng.
        var png = File.ReadAllBytes(cached.Path);
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

        Assert.Equal(cached.Width, width);
        Assert.Equal(cached.Height, height);
    }

    [Fact]
    public void ClearDeletesOnlyRegenerableCacheAndPreservesCustomIconsAndOtherFiles()
    {
        // Hồi quy (rà soát giai đoạn 3): bản cũ gọi File.Delete trước rồi mới kiểm tra "có phải bộ nhớ đệm của mình không",
        // thế nên tệp không liên quan do người dùng bỏ vào thư mục cũng bị xóa theo; biểu tượng tùy chỉnh cũng bị xóa nốt.
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("tệp.txt"));
        Assert.NotNull(services.Icons.EnsureFor(entry.IconSource));

        // Biểu tượng tùy chỉnh: được sao chép từ tệp của người dùng, không thể tái tạo.
        var userIcon = Path.Combine(workspace.Root, "biểu tượng của tôi.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));
        var customPath = services.Library.Find(entry.Id)!.CustomIconPath!;

        // Tệp do người dùng tự đặt vào thư mục bộ nhớ đệm: không phải đồ của mình, không được đụng vào.
        var userNote = Path.Combine(workspace.DataPaths.IconCacheDirectory, "user-note.txt");
        File.WriteAllText(userNote, "Đây là tệp do người dùng tự đặt vào");

        var result = services.Icons.Clear();

        Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(customPath), "Biểu tượng tùy chỉnh đã bị xóa");
        Assert.True(File.Exists(userNote), "Tệp không liên quan do người dùng đặt vào đã bị xóa");
    }

    [Fact]
    public void RegenerableCacheCanBeReextractedAfterClear()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(cached);

        services.Icons.Clear();

        var again = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(again);
        Assert.True(again.CreatedFile);
        Assert.Equal(cached.Path, again.Path);
    }

    [Fact]
    public void ClearCountsLeftAloneFiles()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var directory = workspace.DataPaths.IconCacheDirectory;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.txt"), "Hướng dẫn");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "x");
        File.WriteAllText(Path.Combine(directory, "b.txt"), "y");

        var result = services.Icons.Clear();

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, result.LeftAlone);
    }

    private static AppEntry ImportOne(AppServiceHost services, string source)
    {
        var result = services.Import.ImportItems([new ImportRequest { Source = source }]);
        return Assert.Single(result.Added);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }
}