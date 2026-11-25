using System.Windows;
using System.Windows.Media;

namespace Glassquid.Launcher;

public partial class LauncherWindow
{
    private static readonly Brush SelectedTileBackground = CreateFrozenBrush(Color.FromArgb(48, 56, 189, 248));
    private static readonly Brush SelectedTileBorder = CreateFrozenBrush(Color.FromArgb(220, 56, 189, 248));

    private bool _selectionMode;
    private readonly HashSet<string> _selectedItemIds = new(StringComparer.Ordinal);

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void EnterSelectionMode(string? initialItemId = null)
    {
        _selectionMode = true;
        if (!string.IsNullOrWhiteSpace(initialItemId))
            _selectedItemIds.Add(initialItemId);
        UpdateSelectionUi();
    }

    private void ExitSelectionMode()
    {
        if (!_selectionMode && _selectedItemIds.Count == 0) return;
        _selectionMode = false;
        _selectedItemIds.Clear();
        UpdateSelectionUi();
    }

    private void ToggleItemSelection(string itemId)
    {
        if (!_selectedItemIds.Add(itemId))
            _selectedItemIds.Remove(itemId);
        UpdateSelectionUi();
    }

    private void OnBatchSelectClick(object sender, RoutedEventArgs e)
    {
        if (_selectionMode) ExitSelectionMode();
        else EnterSelectionMode();
    }

    private void OnBatchSelectPageClick(object sender, RoutedEventArgs e)
    {
        if (!_selectionMode || _services is null) return;
        var container = GroupPanel.Visibility == Visibility.Visible ? GroupItems : TileGrid;
        var pageIds = container.Children.OfType<LauncherAppTileButton>()
            .Select(t => t.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id) && _services.Library.Find(id) is not null)
            .Cast<string>()
            .ToArray();
        if (pageIds.Length == 0) return;

        var allSelected = pageIds.All(_selectedItemIds.Contains);
        foreach (var id in pageIds)
        {
            if (allSelected) _selectedItemIds.Remove(id);
            else _selectedItemIds.Add(id);
        }
        UpdateSelectionUi();
    }

    private void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _selectedItemIds.Count == 0) return;
        var count = _selectedItemIds.Count;
        bool confirmed;
        _keepOpenWhileModalCount++;
        try
        {
            confirmed = MessageBox.Show(
                this,
                $"Bạn có chắc muốn xóa {count} ứng dụng đã chọn khỏi Launchpad?",
                "Xóa khỏi Launchpad",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        finally
        {
            _keepOpenWhileModalCount = Math.Max(0, _keepOpenWhileModalCount - 1);
        }
        if (!confirmed) return;

        var removed = _services.Maintenance.RemoveEntries(_selectedItemIds);
        ExitSelectionMode();
        if (removed > 0)
        {
            RefreshLibrary();
            ShowNotice($"Đã xóa {removed} ứng dụng khỏi Launchpad.", autoDismiss: true);
        }
    }

    private void ApplyTileSelectionVisual(LauncherAppTileButton tile, string? itemId, bool isGroup)
    {
        var selected = _selectionMode && !isGroup && itemId is not null && _selectedItemIds.Contains(itemId);
        tile.Background = selected ? SelectedTileBackground : Brushes.Transparent;
        tile.BorderBrush = selected ? SelectedTileBorder : Brushes.Transparent;
    }

    private void UpdateSelectionUi()
    {
        SelectModeButton.Content = _selectionMode ? "Xong" : "Chọn";
        AddButton.Visibility = _selectionMode ? Visibility.Collapsed : Visibility.Visible;
        BatchSelectPageButton.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        BatchDeleteButton.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        BatchDeleteButton.Content = $"Xóa ({_selectedItemIds.Count})";
        BatchDeleteButton.IsEnabled = _selectedItemIds.Count > 0;

        foreach (var tile in TileGrid.Children.OfType<LauncherAppTileButton>())
            ApplyTileSelectionVisual(tile, tile.Tag as string, _services?.Library.Find(tile.Tag as string) is null);
        foreach (var tile in GroupItems.Children.OfType<LauncherAppTileButton>())
            ApplyTileSelectionVisual(tile, tile.Tag as string, isGroup: false);
        foreach (var pageTiles in _retainedTilePages.Values)
            foreach (var tile in pageTiles)
                ApplyTileSelectionVisual(tile, tile.Tag as string, _services?.Library.Find(tile.Tag as string) is null);
    }
}
