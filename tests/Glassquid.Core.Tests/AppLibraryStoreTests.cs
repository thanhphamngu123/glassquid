using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class AppLibraryStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AppLibraryStore CreateStore() => new(_temp.Paths.AppLibraryFile);

    private static AppEntry Entry(string target, string? arguments = null, string? name = null) => new()
    {
        Id = AppLibraryStore.NewId(),
        DisplayName = name ?? Path.GetFileName(target),
        Kind = AppItemKind.Program,
        Target = target,
        Arguments = arguments,
    };

    [Fact]
    public void Add_NewEntry_Succeeds()
    {
        var store = CreateStore();
        store.Load();

        var result = store.Add(Entry(@"C:\Windows\notepad.exe"));

        Assert.Equal(AddEntryResult.Added, result);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Add_SameTargetTwice_IsReportedAsDuplicate()
    {
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Windows\notepad.exe"));
        var second = store.Add(Entry(@"C:\Windows\notepad.exe"));

        Assert.Equal(AddEntryResult.Duplicate, second);
        Assert.Single(store.Items);
    }

    [Fact]
    public void Add_SameTargetDifferentCase_IsDuplicate()
    {
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Windows\notepad.exe"));
        var second = store.Add(Entry(@"c:\windows\NOTEPAD.EXE"));

        Assert.Equal(AddEntryResult.Duplicate, second);
    }

    [Fact]
    public void Add_SameTargetDifferentArguments_IsADistinctEntry()
    {
        // Hai lối tắt trỏ đến cùng một chương trình nhưng khác tham số dòng lệnh là hai thao tác khác nhau,
        // gộp chúng lại sẽ khiến người dùng bị mất một mục.
        var store = CreateStore();
        store.Load();

        store.Add(Entry(@"C:\Tools\app.exe", "--profile work"));
        var second = store.Add(Entry(@"C:\Tools\app.exe", "--profile personal"));

        Assert.Equal(AddEntryResult.Added, second);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Add_WithoutTarget_IsRejected()
    {
        var store = CreateStore();
        store.Load();

        var result = store.Add(new AppEntry { DisplayName = "Không có mục tiêu" });

        Assert.Equal(AddEntryResult.Rejected, result);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Add_WithoutId_AssignsOne()
    {
        var store = CreateStore();
        store.Load();

        var entry = new AppEntry { DisplayName = "notepad", Target = @"C:\Windows\notepad.exe" };
        store.Add(entry);

        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.NotNull(store.Find(entry.Id));
    }

    [Fact]
    public void Remove_DropsTheRecord()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Windows\notepad.exe");
        store.Add(entry);

        Assert.True(store.Remove(entry.Id));
        Assert.Null(store.Find(entry.Id));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Remove_UnknownId_ReportsNoChange()
    {
        var store = CreateStore();
        store.Load();

        Assert.False(store.Remove("no-such-id"));
    }

    [Fact]
    public void SaveThenLoad_KeepsEntriesAndLookups()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\Trình soạn thảo.exe", name: "Trình soạn thảo");
        store.Add(entry);
        store.Save();

        var reloaded = CreateStore();
        var outcome = reloaded.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Single(reloaded.Items);
        Assert.NotNull(reloaded.Find(entry.Id));
        Assert.Equal("Trình soạn thảo", reloaded.Find(entry.Id)!.DisplayName);
    }

    [Fact]
    public void Load_SkipsUnusableRecordsForLookupButKeepsThemOnDisk()
    {
        // Tệp bị chỉnh sửa thủ công và xuất hiện dòng hỏng không nên làm hỏng cả thư viện ứng dụng,
        // dòng này cũng cần được giữ lại để người dùng sửa ở giai đoạn 3.
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                { "id": "good", "displayName": "Bình thường", "kind": "Program", "target": "C:\\Windows\\notepad.exe" },
                { "id": "broken", "displayName": "Thiếu mục tiêu", "kind": "Program", "target": "" }
              ]
            }
            """);

        var store = CreateStore();
        store.Load();

        Assert.NotNull(store.Find("good"));
        Assert.Null(store.Find("broken"));
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Update_ReplacesInPlaceAndKeepsPosition()
    {
        var store = CreateStore();
        store.Load();
        var first = Entry(@"C:\Tools\a.exe");
        var second = Entry(@"C:\Tools\b.exe");
        store.Add(first);
        store.Add(second);

        first.DisplayName = "Tên sau khi đổi";
        Assert.True(store.Update(first));

        // Vị trí giữ nguyên không đổi: bản ghi sau khi cập nhật vẫn là mục đầu tiên.
        Assert.Equal(first.Id, store.Items[0].Id);
        Assert.Equal("Tên sau khi đổi", store.Find(first.Id)!.DisplayName);
        Assert.Equal(2, store.Items.Count);
    }

    [Fact]
    public void Update_ClearsTheOldDedupKey_EvenWhenTheEntryWasMutatedFirst()
    {
        // Hồi quy (rà soát giai đoạn 3): bên gọi sửa mục tiêu tại chỗ trước rồi mới gọi Update — đây chính là cách định vị lại.
        // Phiên bản cũ lấy "bản ghi hiện có" rồi mới tính khóa, mà bản ghi hiện có lại chính là đối tượng đã bị sửa kia,
        // thế nên khóa tính ra là khóa mới, khóa bị xóa cũng là khóa mới, còn khóa cũ vẫn nằm lại trong chỉ mục,
        // sau đó nếu nhập lại đường dẫn cũ sẽ bị nhận nhầm là trùng lặp.
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\Vị trí cũ.exe");
        store.Add(entry);

        entry.Target = @"C:\Tools\Vị trí mới.exe";
        Assert.True(store.Update(entry));

        var oldKey = AppEntryKey.Compute(Entry(@"C:\Tools\Vị trí cũ.exe"));
        var newKey = AppEntryKey.Compute(Entry(@"C:\Tools\Vị trí mới.exe"));

        Assert.Null(store.FindByKey(oldKey));
        Assert.NotNull(store.FindByKey(newKey));
    }

    [Fact]
    public void Remove_ClearsTheOldDedupKey_EvenWhenTheEntryWasMutatedFirst()
    {
        // Cùng một cái bẫy như mục trên, chỉ khác là xảy ra ở Remove.
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\Vị trí cũ.exe");
        store.Add(entry);

        entry.Target = @"C:\Tools\Vị trí mới.exe";
        Assert.True(store.Remove(entry.Id));

        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\Vị trí cũ.exe"))));
        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\Vị trí mới.exe"))));
    }

    [Fact]
    public void Update_KeepsWorking_WhenTheCallerPassesADifferentObjectWithTheSameId()
    {
        var store = CreateStore();
        store.Load();
        var entry = Entry(@"C:\Tools\a.exe");
        store.Add(entry);

        var replacement = Entry(@"C:\Tools\b.exe");
        replacement.Id = entry.Id;
        replacement.DisplayName = "Đối tượng thay thế";

        Assert.True(store.Update(replacement));

        Assert.Equal("Đối tượng thay thế", store.Find(entry.Id)!.DisplayName);
        Assert.Null(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\a.exe"))));
        Assert.NotNull(store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\b.exe"))));
    }

    [Fact]
    public void Update_RejectsATargetThatAnotherEntryAlreadyOccupies()
    {
        // Nếu hai bản ghi dùng chung một khóa chống trùng, thư viện ứng dụng sẽ có một cặp mục trùng lặp không thể phân biệt,
        // sau này dù xóa bản ghi nào cũng sẽ xóa luôn khóa dùng chung, khiến bản ghi còn lại từ đó không tham gia chống trùng nữa.
        var store = CreateStore();
        store.Load();
        var first = Entry(@"C:\Tools\a.exe");
        var second = Entry(@"C:\Tools\b.exe");
        store.Add(first);
        store.Add(second);

        second.Target = @"C:\Tools\a.exe";

        Assert.False(store.Update(second));

        // Chỉ mục phải còn nguyên vẹn: hai khóa vẫn trỏ đúng về bản ghi tương ứng,
        // không bị thiếu hay thừa đi mục nào chỉ vì lần cập nhật bị từ chối này.
        // (Đối tượng mà bên gọi giữ đã tự bị nó sửa rồi, nó phải tự sửa lại trường đó —
        // xem cách xử lý của LibraryMaintenance.Relocate.)
        var firstByKey = store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\a.exe")));
        var secondByKey = store.FindByKey(AppEntryKey.Compute(Entry(@"C:\Tools\b.exe")));

        Assert.NotNull(firstByKey);
        Assert.NotNull(secondByKey);
        Assert.Equal(first.Id, firstByKey.Id);
        Assert.Equal(second.Id, secondByKey.Id);
    }

    [Fact]
    public void Add_BatchThenSingleSave_ProducesOneDocument()
    {
        // Nhập hàng loạt không nên mỗi lần thêm một mục lại ghi đè toàn bộ tệp một lần.
        var store = CreateStore();
        store.Load();

        for (var i = 0; i < 20; i++)
        {
            store.Add(Entry($@"C:\Tools\app{i}.exe"));
        }

        store.Save();

        Assert.True(File.Exists(_temp.Paths.AppLibraryFile));

        var reloaded = CreateStore();
        reloaded.Load();
        Assert.Equal(20, reloaded.Items.Count);
    }

    [Fact]
    public void Load_ToleratesNullEntries()
    {
        // Tệp đã bị chỉnh sửa thủ công có thể xuất hiện mục null. Nó phải được dung thứ và báo cáo,
        // chứ không được ném NullReferenceException khiến chương trình không khởi động được.
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                null,
                { "id": "good", "displayName": "Bình thường", "kind": "Program", "target": "C:\\Windows\\notepad.exe" }
              ]
            }
            """);

        var store = CreateStore();
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.NotNull(store.Find("good"));
    }

    [Fact]
    public void Load_ReportsAnomalousEntries_WithoutDroppingThem()
    {
        // Các mục bất thường cần được báo ra để người dùng sửa, nhưng không được chặn quá trình khởi động.
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                null,
                { "id": "no-target", "displayName": "Thiếu mục tiêu", "kind": "Program", "target": "" },
                { "id": "good", "displayName": "Bình thường", "kind": "Program", "target": "C:\\Windows\\notepad.exe" }
              ]
            }
            """);

        var store = CreateStore();
        store.Load();

        Assert.Equal(2, store.AnomalousEntryCount);
        Assert.NotNull(store.Find("good"));
    }

    [Fact]
    public void Load_ToleratesACompletelyNullItemsArray()
    {
        File.WriteAllText(_temp.Paths.AppLibraryFile, """{"schemaVersion":1,"items":null}""");

        var store = CreateStore();
        var outcome = store.Load();

        Assert.Equal(LoadStatus.Loaded, outcome.Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public void LoadAll_SurvivesANullLibraryEntry_AndReportsIt()
    {
        // Chạy thông suốt một lượt luồng tải khi khởi động. Một thư viện ứng dụng có chứa mục null không được làm quá trình tải ném ngoại lệ,
        // mà phải ghi rõ trong báo cáo có bao nhiêu bản ghi không thể sử dụng — người dùng cần biết trong tệp có thứ bị hỏng.
        File.WriteAllText(
            _temp.Paths.AppLibraryFile,
            """{"schemaVersion":1,"items":[null]}""");

        var data = _temp.CreateStore();
        var report = data.LoadAll();

        Assert.True(report.HasNotices);
        Assert.Contains(report.Notices, notice => notice.Contains("không hợp lệ", StringComparison.Ordinal));
        Assert.Equal(1, data.Library.AnomalousEntryCount);
    }

    [Fact]
    public void LoadAll_OnAHealthyLibrary_ReportsNothing()
    {
        // Dung thứ lỗi không thể biến thành báo động giả: thư viện bình thường không nên sinh ra bất kỳ thông báo nào.
        var store = CreateStore();
        store.Load();
        store.Add(Entry(@"C:\Windows\notepad.exe"));
        store.Save();

        var report = _temp.CreateStore().LoadAll();

        Assert.False(report.HasNotices);
    }
}