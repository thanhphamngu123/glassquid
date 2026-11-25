using System.Text.Json;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Serialization;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();
    private readonly TempDataDirectory _archive = new();
    private readonly TempDataDirectory _newHome = new();

    public void Dispose()
    {
        _temp.Dispose();
        _archive.Dispose();
        _newHome.Dispose();
    }

    /// <summary>Điền dữ liệu dễ nhận biết vào thư mục nguồn và trả về kho lưu trữ đó.</summary>
    private GlassquidDataStore SeedSource()
    {
        var store = _temp.CreateStore();
        store.LoadAll();

        store.Settings.Current.Appearance.AccentColor = "#123456";
        store.Library.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Ghi chú",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
        });
        store.Layouts.Dock.PinnedItemIds.Add("dock-item");

        store.Settings.Save();
        store.Library.Save();
        store.Layouts.SaveDock();
        store.Layouts.SaveLauncher();

        return store;
    }

    [Fact]
    public void BackupTo_WritesManifestAndDocuments()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);

        var result = service.BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.Contains("settings.json", result.Files);
        Assert.Contains("apps.json", result.Files);
        Assert.True(File.Exists(Path.Combine(_archive.Root, "manifest.json")));

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_archive.Root, "manifest.json")),
            GlassquidJsonContext.Default.BackupManifest);

        Assert.NotNull(manifest);
        Assert.Equal("Glassquid", manifest.Product);
        Assert.Equal(GlassquidSettings.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    [Fact]
    public void BackupTo_SkipsDocumentsThatWereNeverSaved()
    {
        // Khi người dùng chưa tùy chỉnh gì mà tạo bản sao lưu, việc có ít tệp hơn là hợp lý.
        var service = new BackupService(_temp.Paths);

        var result = service.BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void BackupAndRestore_IncludeCustomIconFiles()
    {
        // Hồi quy (kiểm tra lại giai đoạn 3): biểu tượng tùy chỉnh không phải bộ nhớ đệm có thể tái tạo, mà được sao chép từ tệp do người dùng chọn.
        // Bản sao lưu phiên bản cũ chỉ mang theo bốn tệp JSON; sau khi khôi phục, tệp mà CustomIconPath trỏ tới không tồn tại,
        // nên mục mà người dùng nhìn thấy thực sự bị mất biểu tượng — vậy mà lúc sao lưu lại báo «thành công».
        var store = SeedSource();

        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        // Trên mục lưu đường dẫn tuyệt đối, trỏ tới tệp có tiền tố custom- trong thư mục bộ nhớ đệm.
        var customIcon = Path.Combine(iconsDirectory, "custom-abc123.png");
        File.WriteAllBytes(customIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);

        var entry = store.Library.Items[0];
        entry.CustomIconPath = customIcon;
        store.Library.Update(entry);
        store.Library.Save();

        var service = new BackupService(_temp.Paths);
        Assert.True(service.BackupTo(_archive.Root).Succeeded);

        // Mô phỏng khôi phục sau khi «bộ nhớ đệm bị xóa sạch»: tệp biểu tượng không còn nữa.
        Directory.Delete(iconsDirectory, recursive: true);
        Assert.False(File.Exists(customIcon));

        var restored = service.RestoreFrom(_archive.Root);

        Assert.True(restored.Succeeded, restored.Error);
        Assert.Contains(Path.Combine("icons", "custom-abc123.png"), restored.Files);

        // Phải quay lại đúng vị trí cũ thì đường dẫn tuyệt đối trên mục mới tiếp tục trỏ tới tệp hợp lệ.
        Assert.True(File.Exists(customIcon), "Sau khi khôi phục, biểu tượng tùy chỉnh không quay lại");
        Assert.Equal(
            File.ReadAllBytes(customIcon),
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01 });
    }

    [Fact]
    public void BackupTo_LeavesUnrelatedFilesInTheIconsDirectoryAlone()
    {
        // Bản sao lưu chỉ mang đi các tệp custom-* của riêng chúng ta; những thứ khác người dùng đặt vào icons/
        // không được sao chép theo, càng không được xóa.
        var store = SeedSource();
        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        var unrelated = Path.Combine(iconsDirectory, "ghi chú của tôi.txt");
        File.WriteAllText(unrelated, "không phải biểu tượng");

        var result = new BackupService(_temp.Paths).BackupTo(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(Path.Combine("icons", "ghi chú của tôi.txt"), result.Files);
        Assert.False(File.Exists(Path.Combine(_archive.Root, "icons", "ghi chú của tôi.txt")));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void RestoreIntoADifferentDirectory_RepointsManagedIconReferences()
    {
        // Hồi quy (vòng kiểm tra lại thứ hai): khôi phục sang một thư mục dữ liệu khác. Phiên bản cũ đã sao chép tệp biểu tượng sang,
        // nhưng CustomIconPath trong apps.json vẫn trỏ về thư mục cũ — xóa ảnh ở thư mục cũ đi là
        // tham chiếu vừa khôi phục sẽ mất hiệu lực. Điều này ảnh hưởng tới việc đổi máy tính và di chuyển thư mục dữ liệu.
        var source = SeedSource();
        var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x07 };

        Directory.CreateDirectory(_temp.Paths.IconCacheDirectory);
        var oldIcon = Path.Combine(_temp.Paths.IconCacheDirectory, "custom-moved.png");
        File.WriteAllBytes(oldIcon, iconBytes);

        var entry = source.Library.Items[0];
        entry.CustomIconPath = oldIcon;
        source.Library.Update(entry);
        source.Library.Save();

        Assert.True(new BackupService(_temp.Paths).BackupTo(_archive.Root).Succeeded);

        // Khôi phục sang một thư mục dữ liệu **khác**, rồi xóa toàn bộ thư mục cũ (đổi máy tính).
        var restore = new BackupService(_newHome.Paths).RestoreFrom(_archive.Root);

        Assert.True(restore.Succeeded, restore.Error);

        var iconInNewHome = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-moved.png");
        Assert.True(File.Exists(iconInNewHome), "Tệp biểu tượng không được đưa sang thư mục mới khi khôi phục");

        // Tệp apps.json được ghi xuống đĩa phải trỏ về thư mục mới, chứ không phải thư mục cũ.
        Assert.Equal(iconInNewHome, ReadStoredCustomIconPath(_newHome.Paths.AppLibraryFile));
        Assert.Equal(1, restore.RepairedIconReferences);

        // Sau khi xóa thư mục cũ, tham chiếu ở thư mục mới vẫn đọc được biểu tượng.
        Directory.Delete(_temp.Root, recursive: true);
        Assert.False(File.Exists(oldIcon));
        Assert.Equal(iconBytes, File.ReadAllBytes(iconInNewHome));

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();
        Assert.Equal(iconInNewHome, reloaded.Find(entry.Id)!.CustomIconPath);
    }

    [Fact]
    public void Load_RepointsManagedIconReferences_WhenTheyNoLongerResolve()
    {
        // Toàn bộ thư mục dữ liệu bị dời đi (không qua sao lưu) cũng là tình huống tương tự: khi tải lên thấy đường dẫn
        // biểu tượng được quản lý không còn ở chỗ cũ, nhưng tệp cùng tên lại nằm ngay dưới icons/ hiện tại thì trỏ sang đó.
        var staleIcon = Path.Combine(_archive.Root, "icons", "custom-abc.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Mẫu",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = staleIcon,
        };
        Assert.Equal(AddEntryResult.Added, store.Add(entry));
        store.Save();

        // Biểu tượng thực ra nằm ngay dưới thư mục dữ liệu hiện tại — trạng thái sau khi thư mục được chuyển sang.
        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        var iconHere = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-abc.png");
        File.WriteAllBytes(iconHere, [0x89, 0x50, 0x4E, 0x47]);

        Assert.False(File.Exists(staleIcon), "Tiền đề kiểm thử không thỏa: vị trí cũ không được có tệp");

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(iconHere, reloaded.Find(entry.Id)!.CustomIconPath);
        Assert.Equal(1, reloaded.RepairedIconReferenceCount);
        Assert.True(File.Exists(reloaded.Find(entry.Id)!.CustomIconPath));
    }

    [Fact]
    public void Load_ReportsRepairedIconReferencesThroughTheDataStore()
    {
        // Tự phục hồi có sửa dữ liệu người dùng, phải cho người dùng thấy rõ chứ không âm thầm sửa.
        var staleIcon = Path.Combine(_archive.Root, "icons", "custom-xyz.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Mẫu",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = staleIcon,
        }));
        store.Save();

        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        File.WriteAllBytes(Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-xyz.png"), [0x89, 0x50]);

        var report = _newHome.CreateStore();
        var loadReport = report.LoadAll();

        Assert.Equal(1, report.Library.RepairedIconReferenceCount);
        Assert.Contains(loadReport.Notices, notice => notice.Contains("1 mục", StringComparison.Ordinal) && notice.Contains("biểu tượng", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_LeavesAPathOutsideTheManagedDirectoryAlone()
    {
        // Đường dẫn trỏ ra ngoài icons/ không do chúng ta quản lý; chúng ta không có cơ sở để biết nó phải trỏ đi đâu,
        // sửa lại là làm hỏng dữ liệu người dùng. Giữ nguyên trạng và cũng không tính vào số lượng đã sửa.
        var external = Path.Combine(_archive.Root, "do người dùng tự đặt.png");

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Mẫu",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = external,
        }));
        store.Save();

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(external, reloaded.Find(store.Items[0]!.Id)!.CustomIconPath);
        Assert.Equal(0, reloaded.RepairedIconReferenceCount);
    }

    [Fact]
    public void Load_DoesNotTouchAReferenceThatAlreadyResolves()
    {
        // Tham chiếu trỏ tới thư mục hiện tại và tệp có tồn tại là bình thường; ghi đè lại chỉ tốn công ghi tệp vô ích.
        Directory.CreateDirectory(_newHome.Paths.IconCacheDirectory);
        var iconHere = Path.Combine(_newHome.Paths.IconCacheDirectory, "custom-ok.png");
        File.WriteAllBytes(iconHere, [0x89, 0x50]);

        var store = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        store.Load();
        Assert.Equal(AddEntryResult.Added, store.Add(new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Mẫu",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
            CustomIconPath = iconHere,
        }));
        store.Save();

        var reloaded = new AppLibraryStore(_newHome.Paths.AppLibraryFile);
        reloaded.Load();

        Assert.Equal(iconHere, reloaded.Find(store.Items[0]!.Id)!.CustomIconPath);
        Assert.Equal(0, reloaded.RepairedIconReferenceCount);
    }

    /// <summary>Đọc đường dẫn biểu tượng tùy chỉnh của bản ghi đầu tiên trong tệp apps.json đã lưu trên đĩa.</summary>
    private static string? ReadStoredCustomIconPath(string appLibraryFile)
    {
        var document = JsonSerializer.Deserialize(
            File.ReadAllText(appLibraryFile),
            GlassquidJsonContext.Default.AppLibraryData);

        return document?.Items?.FirstOrDefault()?.CustomIconPath;
    }

    [Fact]
    public void RestoreFrom_LeavesUnrelatedFilesInTheArchiveAlone()
    {
        // Ngoài dữ liệu của riêng chúng ta, kho lưu trữ có thể còn chứa các tệp khác. Khi khôi phục chỉ lấy
        // bốn tài liệu và icons/custom-*, mọi thứ còn lại đều bỏ qua.
        SeedSource();
        Assert.True(new BackupService(_temp.Paths).BackupTo(_archive.Root).Succeeded);

        // Sau khi sao lưu xong, nhét thêm vài thứ không thuộc bản sao lưu vào kho lưu trữ.
        var archiveIcons = Path.Combine(_archive.Root, "icons");
        Directory.CreateDirectory(archiveIcons);
        File.WriteAllText(Path.Combine(archiveIcons, "custom-biểu tượng thật.png"), "nội dung");
        File.WriteAllText(Path.Combine(archiveIcons, "tệp của người khác.png"), "nội dung");
        File.WriteAllText(Path.Combine(_archive.Root, "đặt tạm.txt"), "nội dung");

        var result = new BackupService(_temp.Paths).RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(Path.Combine("icons", "custom-biểu tượng thật.png"), result.Files);
        Assert.DoesNotContain(Path.Combine("icons", "tệp của người khác.png"), result.Files);
        Assert.DoesNotContain("đặt tạm.txt", result.Files);
        Assert.False(File.Exists(Path.Combine(_temp.Paths.IconCacheDirectory, "tệp của người khác.png")));
        Assert.False(File.Exists(Path.Combine(_temp.Paths.RootDirectory, "đặt tạm.txt")));
    }

    [Fact]
    public void RestoreFrom_TakesTheSafetyCopyOfCustomIconsToo()
    {
        // Ảnh chụp nhanh trước khi khôi phục cũng phải mang theo biểu tượng tùy chỉnh, nếu không thao tác «khôi phục nhầm» sẽ không thể hoàn tác đối với biểu tượng.
        var store = SeedSource();
        var iconsDirectory = _temp.Paths.IconCacheDirectory;
        Directory.CreateDirectory(iconsDirectory);

        var customIcon = Path.Combine(iconsDirectory, "custom-safety.png");
        File.WriteAllBytes(customIcon, [0x89, 0x50, 0x4E, 0x47]);
        store.Library.Items[0].CustomIconPath = customIcon;
        store.Library.Update(store.Library.Items[0]);
        store.Library.Save();

        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);
        var restored = service.RestoreFrom(_archive.Root);

        Assert.True(restored.Succeeded);
        Assert.NotNull(restored.SafetyCopyDirectory);
        Assert.True(File.Exists(Path.Combine(restored.SafetyCopyDirectory!, "icons", "custom-safety.png")));
    }

    [Fact]
    public void RestoreFrom_ReturnsTheOriginalData()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        // Thay dữ liệu đang dùng bằng một bản khác biệt rõ rệt.
        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#FFFFFF";
        live.Library.Remove(live.Library.Items[0].Id);
        live.Settings.Save();
        live.Library.Save();

        var result = service.RestoreFrom(_archive.Root);
        Assert.True(result.Succeeded);

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();

        Assert.Equal("#123456", reloaded.Settings.Current.Appearance.AccentColor);
        Assert.Single(reloaded.Library.Items);
        Assert.Equal(["dock-item"], reloaded.Layouts.Dock.PinnedItemIds);
    }

    [Fact]
    public void RestoreFrom_TakesASafetyCopyFirst()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        var result = service.RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.SafetyCopyDirectory);
        Assert.True(Directory.Exists(result.SafetyCopyDirectory));
    }

    [Fact]
    public void RestoreFrom_RejectsAFolderWithoutAManifest()
    {
        var service = new BackupService(_temp.Paths);

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("tệp kê khai bản sao lưu", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsAForeignManifest()
    {
        File.WriteAllText(
            Path.Combine(_archive.Root, "manifest.json"),
            """{"product":"SomeOtherApp","schemaVersion":1}""");

        var service = new BackupService(_temp.Paths);
        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("không thuộc Glassquid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupFromANewerSchema()
    {
        File.WriteAllText(
            Path.Combine(_archive.Root, "manifest.json"),
            """{"product":"Glassquid","schemaVersion":99}""");

        var service = new BackupService(_temp.Paths);
        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.Contains("phiên bản mới hơn", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsAMissingDirectory()
    {
        var service = new BackupService(_temp.Paths);

        var result = service.RestoreFrom(Path.Combine(_archive.Root, "does-not-exist"));

        Assert.False(result.Succeeded);
        Assert.Contains("không tồn tại", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupWhoseDocumentIsUnreadable()
    {
        // Bản thân tệp kê khai bản sao lưu bình thường, nhưng tệp cài đặt bên trong là JSON hỏng. Bản sao lưu kiểu này phải bị từ chối toàn bộ:
        // một khi bắt đầu sao chép, dữ liệu hỏng sẽ ghi đè lên cấu hình hợp lệ hiện tại của người dùng.
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        File.WriteAllText(Path.Combine(_archive.Root, "settings.json"), "{ đây không phải JSON hợp lệ");

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RestoreFrom_LeavesCurrentDataUntouched_WhenTheBackupIsUnreadable()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        // Sửa dữ liệu hiện tại thành nội dung khác biệt rõ rệt với bản sao lưu.
        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#ABCDEF";
        live.Settings.Save();

        File.WriteAllText(Path.Combine(_archive.Root, "settings.json"), "{ bản sao lưu bị hỏng");

        var result = service.RestoreFrom(_archive.Root);
        Assert.False(result.Succeeded);

        // Khẳng định then chốt: sau khi thất bại, dữ liệu hiện có phải giữ nguyên vẹn.
        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();
        Assert.Equal("#ABCDEF", reloaded.Settings.Current.Appearance.AccentColor);
    }

    [Fact]
    public void RestoreFrom_RejectsABackupWithAnUnreadableAppLibrary()
    {
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        File.WriteAllText(Path.Combine(_archive.Root, "apps.json"), "[[[ danh sách bị hỏng");

        var result = service.RestoreFrom(_archive.Root);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void RestoreFrom_AcceptsABackupThatIsEntirelyValid()
    {
        // Kiểm tra không được quá tay: bản sao lưu hoàn toàn bình thường vẫn phải khôi phục được.
        SeedSource();
        var service = new BackupService(_temp.Paths);
        service.BackupTo(_archive.Root);

        var live = _temp.CreateStore();
        live.LoadAll();
        live.Settings.Current.Appearance.AccentColor = "#000000";
        live.Settings.Save();

        var result = service.RestoreFrom(_archive.Root);

        Assert.True(result.Succeeded);

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();
        Assert.Equal("#123456", reloaded.Settings.Current.Appearance.AccentColor);
    }
}