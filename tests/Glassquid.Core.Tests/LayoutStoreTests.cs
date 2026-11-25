using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class LayoutStoreTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private LayoutStore CreateStore() =>
        new(_temp.Paths.DockLayoutFile, _temp.Paths.LauncherLayoutFile);

    /// <summary>Khởi tạo một kho lưu trữ: gồm một mục ghim trên Dock, một ô đơn lẻ và một nhóm chỉ chứa một mục.</summary>
    private static LayoutStore Populate(LayoutStore store, out string groupId)
    {
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b", "app-c"]);

        groupId = "group-1";

        store.Launcher.Pages.Add(new LauncherPage
        {
            Id = "page-1",
            Slots =
            [
                new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = "app-b" },
                new LauncherSlot
                {
                    Kind = LauncherSlotKind.Group,
                    Group = new LauncherGroup
                    {
                        Id = groupId,
                        Name = "Công cụ",
                        ItemIds = ["app-c", "app-d"],
                    },
                },
                new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = "app-e" },
            ],
        });

        return store;
    }

    [Fact]
    public void UnpinFromDock_RemovesOnlyTheDockPin()
    {
        var store = Populate(CreateStore(), out _);

        Assert.True(store.UnpinFromDock("app-b"));

        // Tham chiếu trong Launchpad phải được giữ lại: bỏ ghim khỏi Dock không đồng nghĩa với xóa.
        Assert.DoesNotContain("app-b", store.Dock.PinnedItemIds);
        Assert.Contains(store.Launcher.Pages[0].Slots,
            slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == "app-b");
    }

    [Fact]
    public void UnpinFromDock_UnknownId_ReportsNoChange()
    {
        var store = Populate(CreateStore(), out _);

        Assert.False(store.UnpinFromDock("not-present"));
    }

    [Fact]
    public void PurgeItem_RemovesDockPinLooseSlotAndGroupMembership()
    {
        var store = Populate(CreateStore(), out _);

        var removed = store.PurgeItem("app-c");

        // Một mục ghim trên Dock, cộng thêm một quan hệ thành viên trong nhóm.
        Assert.Equal(2, removed);
        Assert.DoesNotContain("app-c", store.Dock.PinnedItemIds);

        var group = store.Launcher.Pages[0].Slots
            .Single(slot => slot.Kind == LauncherSlotKind.Group).Group!;
        Assert.DoesNotContain("app-c", group.ItemIds);
    }

    [Fact]
    public void PurgeItem_RemovesTheLooseSlotAndClosesTheGap()
    {
        var store = Populate(CreateStore(), out _);

        store.PurgeItem("app-b");

        // Vị trí chính là chỉ mục, do đó lưới sẽ tự động sắp xếp lại mà không cần bước nén bổ sung.
        var slots = store.Launcher.Pages[0].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(LauncherSlotKind.Group, slots[0].Kind);
        Assert.Equal("app-e", slots[1].ItemId);
    }

    [Fact]
    public void PurgeItem_RemovesAGroupThatBecomesEmpty()
    {
        var store = Populate(CreateStore(), out var groupId);

        // app-d là thành viên duy nhất trong nhóm này không đồng thời xuất hiện ở nơi khác.
        store.PurgeItem("app-c");
        store.PurgeItem("app-d");

        var slots = store.Launcher.Pages[0].Slots;
        Assert.DoesNotContain(slots, slot => slot.Group?.Id == groupId);
    }

    [Fact]
    public void PurgeItem_KeepsGroupsThatStillHaveMembers()
    {
        var store = Populate(CreateStore(), out var groupId);

        store.PurgeItem("app-c");

        var slots = store.Launcher.Pages[0].Slots;
        var group = slots.Single(slot => slot.Group?.Id == groupId).Group!;
        Assert.Equal(["app-d"], group.ItemIds);
    }

    [Fact]
    public void PurgeItem_LeavesUnrelatedEntriesAlone()
    {
        var store = Populate(CreateStore(), out _);

        store.PurgeItem("app-b");

        // Dock giữ lại toàn bộ các mục không bị dọn sạch.
        Assert.Equal(["app-a", "app-c"], store.Dock.PinnedItemIds);

        // Nhóm vẫn giữ hai thành viên và ô đơn lẻ không liên quan cũng vẫn tồn tại.
        // Chỉ ô của riêng app-b bị xóa, nên còn lại hai ô thay vì ba.
        var slots = store.Launcher.Pages[0].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(["app-c", "app-d"], slots[0].Group!.ItemIds);
        Assert.Equal("app-e", slots[1].ItemId);
    }

    [Fact]
    public void PurgeItem_UnknownId_ChangesNothing()
    {
        var store = Populate(CreateStore(), out _);

        Assert.Equal(0, store.PurgeItem("never-existed"));
        Assert.Equal(3, store.Dock.PinnedItemIds.Count);
        Assert.Equal(3, store.Launcher.Pages[0].Slots.Count);
    }

    [Fact]
    public void RemoveEmptyPages_DropsTrailingBlankPages()
    {
        var store = Populate(CreateStore(), out _);
        store.Launcher.Pages.Add(new LauncherPage { Id = "page-2" });

        store.RemoveEmptyPages();

        Assert.Single(store.Launcher.Pages);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBothLayouts()
    {
        var store = Populate(CreateStore(), out var groupId);
        store.SaveDock();
        store.SaveLauncher();

        var reloaded = CreateStore();
        reloaded.LoadDock();
        reloaded.LoadLauncher();

        Assert.Equal(["app-a", "app-b", "app-c"], reloaded.Dock.PinnedItemIds);

        var group = reloaded.Launcher.Pages[0].Slots
            .Single(slot => slot.Group?.Id == groupId).Group!;
        Assert.Equal("Công cụ", group.Name);
        Assert.Equal(["app-c", "app-d"], group.ItemIds);
    }

    [Fact]
    public void SaveDock_WhenSaveFails_RestoresSharedLayoutToLastSavedOrder()
    {
        var store = CreateStore();
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b"]);
        store.SaveDock();

        // Giả lập một tiến trình khác đang giữ tệp tạm ghi nguyên tử; thao tác lưu xuống đĩa thực sự phải thất bại.
        using (var blocker = new FileStream(
            store.DockFilePath + AtomicFile.TempSuffix,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            store.Dock.PinnedItemIds = ["app-b", "app-a"];
            Assert.Throws<StorageException>(() => store.SaveDock());
        }

        Assert.Equal(["app-a", "app-b"], store.Dock.PinnedItemIds);
        var reloaded = CreateStore();
        reloaded.LoadDock();
        Assert.Equal(store.Dock.PinnedItemIds, reloaded.Dock.PinnedItemIds);
    }

    [Fact]
    public void UnpinFromDock_WhenSaveFails_KeepsOriginalPinnedItems()
    {
        var store = CreateStore();
        store.Dock.PinnedItemIds.AddRange(["app-a", "app-b"]);
        store.SaveDock();

        using (var blocker = new FileStream(
            store.DockFilePath + AtomicFile.TempSuffix,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(store.UnpinFromDock("app-a"));
            Assert.Throws<StorageException>(() => store.SaveDock());
        }

        Assert.Equal(["app-a", "app-b"], store.Dock.PinnedItemIds);
    }

    [Fact]
    public void PurgeItem_SurvivesAReload()
    {
        var store = Populate(CreateStore(), out _);
        store.PurgeItem("app-c");
        store.SaveDock();
        store.SaveLauncher();

        var reloaded = CreateStore();
        reloaded.LoadDock();
        reloaded.LoadLauncher();

        Assert.DoesNotContain("app-c", reloaded.Dock.PinnedItemIds);
        var group = reloaded.Launcher.Pages[0].Slots
            .Single(slot => slot.Kind == LauncherSlotKind.Group).Group!;
        Assert.Equal(["app-d"], group.ItemIds);
    }

    [Fact]
    public void DeleteFromLibrary_RemovesTheRecordEverywhere()
    {
        // Bất biến ở cấp độ toàn tài liệu, được kiểm chứng thông qua
        // facade dùng chung cho cả Dock và Launchpad.
        var data = _temp.CreateStore();
        data.LoadAll();

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            DisplayName = "Notepad",
            Kind = AppItemKind.Program,
            Target = @"C:\Windows\notepad.exe",
        };

        data.Library.Add(entry);
        data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
        data.Layouts.Launcher.Pages.Add(new LauncherPage
        {
            Id = "page-1",
            Slots = [new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = entry.Id }],
        });

        Assert.True(data.DeleteFromLibrary(entry.Id));

        var reloaded = _temp.CreateStore();
        reloaded.LoadAll();

        Assert.Null(reloaded.Library.Find(entry.Id));
        Assert.Empty(reloaded.Layouts.Dock.PinnedItemIds);
        Assert.Empty(reloaded.Layouts.Launcher.Pages[0].Slots);
    }
}
