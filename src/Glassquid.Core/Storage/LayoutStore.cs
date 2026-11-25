using Glassquid.Core.Models;
using Glassquid.Core.Serialization;

namespace Glassquid.Core.Storage;

/// <summary>
/// Quản lý hai tài liệu bố cục: <c>dock-layout.json</c> và <c>launcher-layout.json</c>.
/// </summary>
/// <remarks>
/// Cả hai đều độc lập với thư viện ứng dụng và độc lập lẫn nhau, nên một khi bố cục Launcher bị hỏng, người dùng sẽ không
/// bị mất đồng thời cả Dock hoặc thư viện ứng dụng.
///
/// Các ô (slot) được lưu dưới dạng danh sách có thứ tự dày đặc thay vì tọa độ lưới cố định. Vị trí
/// chính là chỉ mục, nên việc xóa một nhóm tự nhiên sẽ khép khoảng trống lại, không cần
/// lượt nén ô trống như cách Launcher tham chiếu phải làm.
/// </remarks>
public sealed class LayoutStore
{
    private readonly JsonFileStore<DockLayout> _dockStore;
    private readonly JsonFileStore<LauncherLayout> _launcherStore;
    private List<string> _lastSavedDockPins = [];
    private LauncherLayout _lastSavedLauncher = new();

    public LayoutStore(string dockLayoutPath, string launcherLayoutPath, TimeProvider? timeProvider = null)
    {
        _dockStore = new JsonFileStore<DockLayout>(
            dockLayoutPath,
            GlassquidJsonContext.Default.DockLayout,
            timeProvider);

        _launcherStore = new JsonFileStore<LauncherLayout>(
            launcherLayoutPath,
            GlassquidJsonContext.Default.LauncherLayout,
            timeProvider);
    }

    public DockLayout Dock { get; private set; } = new();

    public LauncherLayout Launcher { get; private set; } = new();

    public long LauncherVersion { get; private set; }

    public string DockFilePath => _dockStore.Path;

    public string LauncherFilePath => _launcherStore.Path;

    public LoadOutcome<DockLayout> LoadDock()
    {
        var outcome = _dockStore.Load(static () => new DockLayout());
        Dock = outcome.Value;
        Dock.PinnedItemIds ??= [];
        _lastSavedDockPins = [.. Dock.PinnedItemIds];
        return outcome;
    }

    public LoadOutcome<LauncherLayout> LoadLauncher()
    {
        var outcome = _launcherStore.Load(static () => new LauncherLayout());
        Launcher = outcome.Value;
        Launcher.Pages ??= [];
        _lastSavedLauncher = LauncherLayoutRules.DeepClone(Launcher);
        LauncherVersion++;
        return outcome;
    }

    /// <exception cref="StorageException">Không thể ghi tệp.</exception>
    public void SaveDock()
    {
        try
        {
            _dockStore.Save(Dock);
            _lastSavedDockPins = [.. Dock.PinnedItemIds];
        }
        catch
        {
            // Callers mutate the shared Dock object before saving. If the write fails,
            // leave every later reader with the last known persisted order.
            Dock.PinnedItemIds = [.. _lastSavedDockPins];
            throw;
        }
    }

    /// <exception cref="StorageException">Không thể ghi tệp.</exception>
    public void SaveLauncher()
    {
        try
        {
            _launcherStore.Save(Launcher);
            _lastSavedLauncher = LauncherLayoutRules.DeepClone(Launcher);
            LauncherVersion++;
        }
        catch
        {
            var lastSaved = LauncherLayoutRules.DeepClone(_lastSavedLauncher);
            Launcher.Pages = lastSaved.Pages;
            Launcher.ExcludedAppIds = lastSaved.ExcludedAppIds;
            Launcher.SchemaVersion = lastSaved.SchemaVersion;
            throw;
        }
    }

    /// <summary>Commits a detached preview once, refusing a stale layout revision.</summary>
    public bool CommitLauncher(LauncherLayout draft, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (expectedVersion != LauncherVersion) return false;
        if (LauncherLayoutRules.Equivalent(Launcher, draft)) return true;
        var committed = LauncherLayoutRules.DeepClone(draft);
        Launcher.Pages = committed.Pages;
        Launcher.ExcludedAppIds = committed.ExcludedAppIds;
        Launcher.SchemaVersion = committed.SchemaVersion;
        SaveLauncher();
        return true;
    }

    /// <summary>
    /// Chỉ gỡ ứng dụng khỏi thanh Dock. Bản ghi trong thư viện ứng dụng vẫn được giữ nguyên——đây là
    /// thao tác “gỡ khỏi Dock” chứ không phải “xóa”.
    /// </summary>
    public bool UnpinFromDock(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        return Dock.PinnedItemIds.Remove(itemId);
    }

    /// <summary>
    /// Xóa mọi tham chiếu đến id này trong cả hai tệp bố cục. Chỉ gọi phương thức này khi bản ghi bị xóa
    /// khỏi thư viện ứng dụng và chỉ trong trường hợp đó.
    /// </summary>
    /// <returns>Số lượng tham chiếu đã bị xóa.</returns>
    public int PurgeItem(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return 0;
        }

        var removed = 0;

        if (Dock.PinnedItemIds.Remove(itemId))
        {
            removed++;
        }

        foreach (var page in Launcher.Pages)
        {
            foreach (var slot in page.Slots)
            {
                if (slot.Kind != LauncherSlotKind.Group || slot.Group is null)
                {
                    continue;
                }

                removed += slot.Group.ItemIds.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
            }

            // Các trang giữ nguyên thứ tự cũ; lưới sẽ tự sắp xếp lại để lấp khoảng trống.
            removed += page.Slots.RemoveAll(slot =>
                slot.Kind == LauncherSlotKind.App &&
                string.Equals(slot.ItemId, itemId, StringComparison.Ordinal));
        }

        // Nhóm đã trống rỗng thì không có lý do gì tiếp tục giữ lại trên lưới.
        removed += RemoveEmptyGroups();

        return removed;
    }

    /// <summary>Loại bỏ các nhóm không còn chứa ứng dụng nào.</summary>
    public int RemoveEmptyGroups()
    {
        var removed = 0;

        foreach (var page in Launcher.Pages)
        {
            removed += page.Slots.RemoveAll(static slot =>
                slot.Kind == LauncherSlotKind.Group &&
                (slot.Group is null || slot.Group.ItemIds.Count == 0));
        }

        return removed;
    }

    /// <summary>Xóa các trang trống để phần cuối lưới không bao giờ dừng lại ở một trang trắng.</summary>
    public int RemoveEmptyPages()
    {
        return Launcher.Pages.RemoveAll(static page => page.Slots.Count == 0);
    }
}
