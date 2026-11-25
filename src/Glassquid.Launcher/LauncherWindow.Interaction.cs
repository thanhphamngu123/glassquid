using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.Launcher;

public partial class LauncherWindow
{
    private DispatcherTimer? _noticeTimer;

    private void ActivateEntry(string id)
    {
        if (_services is null) return;
        var result = _services.Launch.ActivateOrLaunch(id);
        if (result.Outcome == ActivationOutcome.MultipleWindowsFound)
        {
            var menu = CreateTrackedContextMenu();
            foreach (var candidate in result.Candidates)
            {
                var item = new MenuItem { Header = candidate.Title };
                item.Click += (_, _) =>
                {
                    var activation = LaunchService.ActivateWindow(candidate.Handle);
                    if (activation.Outcome == ActivationOutcome.Failed) ShowNotice(activation.Message ?? "Không thể kích hoạt cửa sổ.");
                    else CloseWithTransition();
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = TileGrid;
            menu.IsOpen = true;
        }
        else if (result.Outcome == ActivationOutcome.Failed)
            ShowNotice(result.Message ?? "Không thể mở ứng dụng.");
        else
            CloseWithTransition();
    }

    private void ShowNotice(string notice, bool autoDismiss = false)
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
        _notice = notice;
        RefreshDiagnostics();
        if (!autoDismiss) return;

        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_noticeTimer, timer)) return;
            _noticeTimer = null;
            // A library refresh may have replaced this success with a persistent status.
            if (_notice != notice) return;
            _notice = null;
            RefreshDiagnostics();
        };
        _noticeTimer = timer;
        timer.Start();
    }

    private void CommitEdit(Func<LauncherLayout, LauncherLayout> transform)
    {
        if (_workspace is null || _view.IsSearching)
        {
            ShowNotice("Không thể sắp xếp ứng dụng trong kết quả tìm kiếm; hãy xóa nội dung tìm kiếm trước.");
            return;
        }
        var edit = _workspace.BeginEdit();
        try
        {
            edit.Draft = transform(edit.Draft);
            if (!_workspace.Commit(edit)) ShowNotice("Thư viện ứng dụng hoặc bố cục đã thay đổi; hãy thử sắp xếp lại.");
            else _notice = null;
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        {
            ShowNotice("Không thể lưu thay đổi; bố cục ban đầu đã được khôi phục: " + ex.Message);
        }
        RenderTiles();
    }

    private ContextMenu BuildTileMenu(LauncherSlot slot)
    {
        var menu = CreateTrackedContextMenu();
        var id = slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;
        if (id is null) return menu;
        if (slot.Kind == LauncherSlotKind.Group)
        {
            AddMenu(menu, "Mở thư mục", () => { _view.OpenGroupId = id; RenderGroup(); });
            AddMenu(menu, "Đổi tên thư mục", () => RenameGroup(id));
            AddMenu(menu, "Xóa nhóm ứng dụng", () => CommitEdit(layout => LauncherLayoutEditor.Dissolve(layout, id, _view.Capacity)));
        }
        else
        {
            AddMenu(menu, "Mở", () => ActivateEntry(id));
            AddMenu(menu, "Sửa tên, biểu tượng và từ khóa tìm kiếm", () => EditEntry(id));
            if (_services?.Library.Find(id)?.IsTargetMissing == true)
                AddMenu(menu, "Tìm lại vị trí…", () => RelocateEntry(id));
            var group = _workspace?.Layout.Pages.SelectMany(page => page.Slots)
                .SingleOrDefault(item => item.Group?.ItemIds.Contains(id) == true)?.Group;
            if (group is not null)
            {
                var index = group.ItemIds.IndexOf(id);
                if (index > 0)
                    AddMenu(menu, "Di chuyển lên trong thư mục", () => CommitEdit(layout => LauncherLayoutEditor.MoveGroupMember(layout, group.Id, id, index - 1)));
                if (index < group.ItemIds.Count - 1)
                    AddMenu(menu, "Di chuyển xuống trong thư mục", () => CommitEdit(layout => LauncherLayoutEditor.MoveGroupMember(layout, group.Id, id, index + 1)));
                AddMenu(menu, "Đưa ra khỏi thư mục", () => CommitEdit(layout => LauncherLayoutEditor.RemoveFromGroup(layout, id, group.Id, _view.Page, 0, _view.Capacity)));
            }
            AddMenu(menu, "Xóa khỏi Launchpad", () => RemoveEntryFromLaunchpad(id));
            AddMenu(menu, "Chọn nhiều để xóa...", () => EnterSelectionMode(id));
        }
        return menu;
    }

    private void RemoveEntryFromLaunchpad(string id)
    {
        if (_services is null) return;
        if (_services.Maintenance.RemoveEntry(id))
            RefreshLibrary();
    }

    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

}
