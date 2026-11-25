using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glassquid.Core.Imaging;
using Glassquid.Platform.Shell;
using Glassquid.UI.Controls;

namespace Glassquid.Launcher;

/// <summary>Chọn mục trên màn hình nền; bên gọi chỉ thực sự nhập sau khi người dùng xác nhận.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF window resources are released in Closed, matching GlassWindow and LauncherWindow.")]
public sealed class DesktopImportWindow : GlassWindow
{
    private readonly List<(DesktopItem Item, CheckBox Choice, Image Icon)> _rows = [];
    private readonly TextBlock _selectionCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _add = new() { Content = "Thêm mục đã chọn", MinWidth = 100, IsDefault = true, IsEnabled = false };
    private LauncherIconLoader? _icons;
    private bool _changingSelection, _closed;

    public IReadOnlyList<string> SelectedPaths => _rows.Where(row => row.Choice.IsChecked == true)
        .Select(row => row.Item.Path).ToArray();

    public DesktopImportWindow(DesktopScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        Title = "Thêm hàng loạt từ màn hình nền";
        AutomationProperties.SetName(this, Title);
        Width = 600; Height = 560; MinWidth = 420; MinHeight = 320;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        NameScope.SetNameScope(this, new NameScope());

        var panel = new DockPanel { Margin = new Thickness(22) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Chọn các tệp, ứng dụng, lối tắt hoặc thư mục trên màn hình nền mà bạn muốn thêm.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14), Opacity = .8 });
        var selection = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_selectionCount, Dock.Right);
        selection.Children.Add(_selectionCount);
        var selectActions = new StackPanel { Orientation = Orientation.Horizontal };
        var selectAll = new Button { Content = "Chọn tất cả", MinWidth = 66, Margin = new Thickness(0, 0, 8, 0) };
        var clear = new Button { Content = "HủyChọn tất cả", MinWidth = 82 };
        RegisterName("SelectAllButton", selectAll); RegisterName("ClearSelectionButton", clear);
        selectAll.Click += (_, _) => SelectAll(true);
        clear.Click += (_, _) => SelectAll(false);
        selectActions.Children.Add(selectAll); selectActions.Children.Add(clear);
        selection.Children.Add(selectActions); heading.Children.Add(selection);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);

        var footer = new StackPanel();
        if (scan.Warnings.Count > 0)
            footer.Children.Add(new TextBlock { Text = "Không thể đọc một số mục trên màn hình nền; các mục còn lại được liệt kê bên dưới.",
                ToolTip = string.Join(Environment.NewLine, scan.Warnings), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Hủy", MinWidth = 76, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        RegisterName("AddSelectedButton", _add);
        _add.Click += (_, _) => { if (SelectedPaths.Count > 0) DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(_add); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);

        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(list, "Mục trên màn hình nền");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        RegisterName("DesktopItemsList", list);
        foreach (var item in scan.Items)
        {
            var icon = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0), Stretch = Stretch.Uniform };
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = item.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = (item.FromCurrentUser ? "Màn hình nền cá nhân" : "Màn hình nền công cộng")
                + (item.IsDirectory ? " · Thư mục" : ""), FontSize = 11, Opacity = .65, Margin = new Thickness(0, 3, 0, 0) });
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(label);
            var choice = new CheckBox { Content = row, ToolTip = item.Path,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 5, 4, 5) };
            AutomationProperties.SetName(choice, item.DisplayName);
            choice.Checked += (_, _) => UpdateSelection(); choice.Unchecked += (_, _) => UpdateSelection();
            _rows.Add((item, choice, icon)); list.Items.Add(choice);
        }
        if (_rows.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Không có tệp hoặc lối tắt nào trên màn hình nền để thêm.",
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        else panel.Children.Add(list);
        var surface = new Grid();
        surface.Children.Add(new LiquidGlassSurface { UseLauncherTransparency = true });
        surface.Children.Add(panel); Content = surface;
        UpdateSelection();
        Loaded += (_, _) => { list.Focus(); LoadIcons(); };
        Closed += (_, _) => { _closed = true; _icons?.Dispose(); };
    }

    private void SelectAll(bool selected)
    {
        _changingSelection = true;
        try { foreach (var row in _rows) row.Choice.IsChecked = selected; }
        finally { _changingSelection = false; UpdateSelection(); }
    }

    private void UpdateSelection()
    {
        if (_changingSelection) return;
        var count = _rows.Count(row => row.Choice.IsChecked == true);
        _selectionCount.Text = $"Đã chọn {count}/{_rows.Count} mục";
        _add.IsEnabled = count > 0;
    }

    private void LoadIcons()
    {
        if (_icons is not null || _closed || _rows.Count == 0) return;
        _icons = new LauncherIconLoader();
        foreach (var row in _rows) _ = LoadIconAsync(row.Item.Path, row.Icon);
    }

    private async Task LoadIconAsync(string path, Image host)
    {
        var image = await _icons!.LoadAsync(path, () =>
        {
            var extracted = IconExtractor.Extract(path);
            if (extracted is null) return null;
            using var stream = new MemoryStream(PngWriter.EncodeBgra(extracted.Bgra, extracted.Width, extracted.Height));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = 48; bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }).ConfigureAwait(false);
        if (image is null || host.Dispatcher.HasShutdownStarted) return;
        try
        {
            await host.Dispatcher.InvokeAsync(() => { if (!_closed) host.Source = image; },
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (TaskCanceledException) { }
    }
}
