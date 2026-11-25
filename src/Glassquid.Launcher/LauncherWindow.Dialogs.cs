using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.UI.Controls;
using Microsoft.Win32;

namespace Glassquid.Launcher;

public partial class LauncherWindow
{
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateTrackedContextMenu();
        menu.PlacementTarget = sender as Button;
        AddMenu(menu, "Thêm hàng loạt từ màn hình nền…", AddFromDesktop);
        AddMenu(menu, "Tệp, ứng dụng hoặc lối tắt…", () =>
        {
            var picker = CreateImportFilePicker();
            if (ShowModalDialog(() => picker.ShowDialog(this))) ImportPaths(picker.FileNames);
        });
        AddMenu(menu, "Thư mục…", () =>
        {
            var picker = new OpenFolderDialog();
            if (ShowModalDialog(() => picker.ShowDialog(this))) ImportPaths([picker.FolderName]);
        });
        AddMenu(menu, "Trang web…", AddWebsite);
        menu.IsOpen = true;
    }

    // Giữ nguyên nguồn .lnk để tránh hộp thoại chọn tệp phân giải trước đích đến và làm mất tên, tham số cũng như biểu tượng tùy chỉnh.
    private static OpenFileDialog CreateImportFilePicker() => new()
    { Multiselect = true, DereferenceLinks = false, Filter = "Tất cả tệp|*.*" };

    private void AddFromDesktop()
    {
        if (_services is null) return;
        var dialog = new DesktopImportWindow(Glassquid.Platform.Shell.DesktopScanner.Scan()) { Owner = this };
        if (ShowModalDialog(dialog.ShowDialog)) ImportPaths(dialog.SelectedPaths);
    }

    private void ImportPaths(IReadOnlyList<string> paths)
    {
        if (_services is null) return;
        try
        {
            var requests = paths.Select(path => new ImportRequest { Source = path }).ToList();
            var before = LauncherLayoutRules.AppIds(_workspace?.Layout ?? new LauncherLayout()).ToHashSet(StringComparer.Ordinal);
            var result = _services.Import.ImportItems(requests);
            var imported = result.Added.Concat(result.Duplicates).Select(item => item.Id).Distinct().ToList();
            var placed = imported.Count(id => !before.Contains(id) && _workspace is not null
                && LauncherLayoutRules.AppIds(_workspace.Layout).Contains(id));
            var summary = $"Đã thêm {placed} mục vào Launchpad; {imported.Count - placed} mục đã có sẵn, {result.Failures.Count} mục bị lỗi.";
            if (result.Failures.Count > 0) summary += " " + result.Failures[0].Message;
            ShowNotice(summary, autoDismiss: result.Failures.Count == 0);
            if (result.Failures.Count > 0)
                ShowModalDialog(() =>
                {
                    MessageBox.Show(this, summary + Environment.NewLine + string.Join(Environment.NewLine,
                        result.Failures.Select(failure => failure.Source + "：" + failure.Message)),
                        "Kết quả nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
            RefreshLibrary();
        }
        catch (StorageException ex)
        {
            ShowNotice("Không thể lưu thư viện ứng dụng. Hãy kiểm tra thư mục dữ liệu rồi thử lại: " + ex.Message);
        }
    }

    private void AddWebsite()
    {
        var values = AskFields("Thêm trang web", ("Địa chỉ trang web", "https://"), ("Tên hiển thị", ""));
        if (values is null || _services is null) return;
        try
        {
            var before = LauncherLayoutRules.AppIds(_workspace?.Layout ?? new LauncherLayout()).ToHashSet(StringComparer.Ordinal);
            var result = _services.Import.ImportItems([new ImportRequest
            { Source = values[0], DisplayName = values[1], IsUrl = true }]);
            ShowNotice(result.Failures.Count > 0 ? result.Failures[0].Message
                : result.Added.Concat(result.Duplicates).Any(item => !before.Contains(item.Id))
                    ? "Đã thêm trang web." : "Trang web này đã có trong Launchpad.", autoDismiss: result.Failures.Count == 0);
            RefreshLibrary();
        }
        catch (StorageException ex) { ShowNotice("Không thể lưu thông tin trang web: " + ex.Message); }
    }

    private void RenameGroup(string groupId)
    {
        var group = _workspace?.Layout.Pages.SelectMany(page => page.Slots)
            .SingleOrDefault(slot => slot.Group?.Id == groupId)?.Group;
        if (group is null) return;
        var values = AskFields("Đổi tên thư mục", ("Tên", group.Name));
        if (values is not null) CommitEdit(layout => LauncherLayoutEditor.RenameGroup(layout, groupId, values[0]));
    }

    private void EditEntry(string id)
    {
        var entry = _services?.Library.Find(id);
        if (entry is null || _services is null) return;
        var values = AskFields("Sửa ứng dụng", ("Tên", entry.DisplayName),
            ("Từ khóa tìm kiếm (phân tách bằng dấu phẩy)", string.Join(", ", entry.SearchAliases)),
            ("Đường dẫn biểu tượng tùy chỉnh (để trống để giữ nguyên)", ""));
        if (values is null) return;
        try
        {
            var aliases = values[1].Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!_services.Editor.Edit(id, values[0], aliases, string.IsNullOrWhiteSpace(values[2]) ? null : values[2]))
                ShowNotice("Thông tin không hợp lệ. Tên phải dài từ 1 đến 120 ký tự và biểu tượng phải là tệp ảnh có thể đọc được.");
            else { ShowNotice("Đã cập nhật ứng dụng.", autoDismiss: true); RefreshLibrary(); }
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            ShowNotice("Không thể lưu thay đổi; thông tin ban đầu đã được khôi phục: " + ex.Message);
        }
    }

    private void RelocateEntry(string id)
    {
        var entry = _services?.Library.Find(id);
        if (entry is null || _services is null || entry.Kind == AppItemKind.Url) return;
        string? target = null;
        if (entry.Kind == AppItemKind.Folder)
        {
            var folder = new OpenFolderDialog();
            if (ShowModalDialog(() => folder.ShowDialog(this))) target = folder.FolderName;
        }
        else
        {
            var picker = new OpenFileDialog { Filter = "Tất cả tệp|*.*" };
            if (ShowModalDialog(() => picker.ShowDialog(this))) target = picker.FileName;
        }
        if (target is null) return;
        var outcome = _services.Maintenance.Relocate(id, target);
        ShowNotice(outcome.Message, autoDismiss: outcome.Result == RelocateResult.Relocated);
        RefreshLibrary();
    }

    private string[]? AskFields(string title, params (string Label, string Value)[] fields)
    {
        var dialog = new GlassPromptWindow
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 430, SizeToContent = SizeToContent.Height, MinHeight = 150,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 6, 0, 3) });
            var box = new TextBox { Text = field.Value, MinHeight = 28 };
            System.Windows.Automation.AutomationProperties.SetName(box, field.Label);
            if (field.Label.Contains("biểu tượng", StringComparison.OrdinalIgnoreCase))
            {
                var row = new DockPanel();
                var browse = new Button { Content = "Chọn…", MinWidth = 72, Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var picker = new OpenFileDialog { Filter = "Tệp ảnh|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|Tất cả tệp|*.*" };
                    if (ShowModalDialog(() => picker.ShowDialog(dialog))) box.Text = picker.FileName;
                };
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                row.Children.Add(box);
                panel.Children.Add(row);
            }
            else panel.Children.Add(box);
            inputs.Add(box);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Hủy", MinWidth = 70, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Lưu", MinWidth = 70, IsDefault = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        var surface = new Grid();
        surface.Children.Add(new LiquidGlassSurface { UseLauncherTransparency = true });
        surface.Children.Add(panel);
        dialog.Content = surface;
        dialog.Loaded += (_, _) => inputs[0].Focus();
        return dialog.ShowDialog() == true ? inputs.Select(box => box.Text).ToArray() : null;
    }

    private sealed class GlassPromptWindow : GlassWindow
    {
        protected override double ResolveCornerRadius(AppearanceSettings appearance) =>
            40;
    }
}
