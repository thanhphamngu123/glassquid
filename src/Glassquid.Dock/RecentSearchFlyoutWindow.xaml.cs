using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>
/// Cửa sổ tìm kiếm và hiển thị mục gần đây với hiệu ứng Liquid Glass đồng bộ thanh Dock.
/// </summary>
public partial class RecentSearchFlyoutWindow : GlassWindow
{
    private readonly SearchDockWindow _dock;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _iconCts;
    private IReadOnlyList<SearchItemModel> _currentItems = [];
    private bool _glassRevealPending;
    private bool _isClosing;
    private bool _closeQueued;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    public RecentSearchFlyoutWindow(SearchDockWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;

        InitializeComponent();

        PanelCornerRadius = new CornerRadius(28);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        MeasureContentAndPosition();
        GlassSurface.PrepareRevealForWindow(this);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(28, appearance.DockCornerRadius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PrimaryDisplaySession is { IsAvailable: false }) Hide();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        AnimatePopupOpen();

        SearchInputBox.Focus();
        _ = LoadRecentItemsAsync();
    }

    private void MeasureContentAndPosition()
    {
        if (Content is not UIElement content) return;

        var workArea = WorkAreaDip;
        content.Measure(new Size(workArea.Width, workArea.Height));
        var desired = content.DesiredSize;

        Width = Math.Clamp(Math.Ceiling(desired.Width), 440, 500);
        Height = Math.Clamp(Math.Ceiling(desired.Height), 340, 560);
        SizeToContent = SizeToContent.Manual;

        var anchor = _dock.GetSearchAnchorPoint();
        Left = Math.Clamp(anchor.X, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - Width - 8));
        Top = _dock.IsTopPlacement
            ? Math.Clamp(anchor.Y + 12, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - Height - 8))
            : Math.Clamp(anchor.Y - Height - 12, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - Height - 8));
    }

    private void AnimatePopupOpen()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fromY = _dock.IsTopPlacement ? -16 : 16;
        PopupSlideTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelSearch()
    {
        if (_searchCts is not null)
        {
            try
            {
                _searchCts.Cancel();
                _searchCts.Dispose();
            }
            catch (ObjectDisposedException) { }
            _searchCts = null;
        }
    }

    private void CancelIconLoading()
    {
        if (_iconCts is not null)
        {
            try
            {
                _iconCts.Cancel();
                _iconCts.Dispose();
            }
            catch (ObjectDisposedException) { }
            _iconCts = null;
        }
    }

    private async Task LoadRecentItemsAsync()
    {
        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        SearchProgressBar.Visibility = Visibility.Visible;
        try
        {
            var items = await WindowsSearchService.GetRecentItemsAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(_searchCts, cts)) return;

            UpdateList(items, "Tìm kiếm gần đây");
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation, ignore
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentSearchFlyout] Error loading recent items: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                SearchProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var rawQuery = SearchInputBox.Text;
        PlaceholderText.Visibility = string.IsNullOrEmpty(rawQuery) ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = string.IsNullOrEmpty(rawQuery) ? Visibility.Collapsed : Visibility.Visible;

        // Cancel previous pending search or debounce wait
        CancelSearch();

        var query = rawQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _ = LoadRecentItemsAsync();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = PerformSearchDebouncedAsync(query, cts);
    }

    private async Task PerformSearchDebouncedAsync(string query, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            // Search Debounce with Task.Delay(100, token) to eliminate typing jitter
            await Task.Delay(100, token);

            if (token.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
            {
                return;
            }

            SearchProgressBar.Visibility = Visibility.Visible;
            try
            {
                var items = await WindowsSearchService.SearchAsync(query, token);

                if (token.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
                {
                    return;
                }

                UpdateList(items, $"Kết quả tìm kiếm ({items.Count})");
            }
            finally
            {
                if (ReferenceEquals(_searchCts, cts))
                {
                    SearchProgressBar.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation when user continues typing or closes flyout
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentSearchFlyout] Error searching: {ex.Message}");
        }
    }

    private void UpdateList(IReadOnlyList<SearchItemModel> items, string sectionTitle)
    {
        _currentItems = items;
        SectionTitleText.Text = sectionTitle;
        ItemCountText.Text = items.Count > 0 ? $"{items.Count} mục" : string.Empty;

        ResultsList.ItemsSource = items;
        EmptyResultsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Tải icon bất đồng bộ với cancellation token
        CancelIconLoading();
        var iconCts = new CancellationTokenSource();
        _iconCts = iconCts;
        _ = WindowsSearchService.LoadIconsAsync(items, (item, icon) => { }, iconCts.Token);
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchItemModel item })
        {
            LaunchItem(item);
        }
    }

    private void LaunchItem(SearchItemModel item)
    {
        try
        {
            WindowsSearchService.LaunchAppOrFile(item);
            CloseFlyout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecentSearchFlyout] Launch failed for {item.TargetPath}: {ex.Message}");
        }
    }

    private void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        CancelSearch();
        SearchInputBox.Text = string.Empty;
        SearchInputBox.Focus();
        _ = LoadRecentItemsAsync();
    }

    private void OnSearchInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_currentItems.Count > 0)
            {
                LaunchItem(_currentItems[0]);
            }
        }
        else if (e.Key == Key.Down)
        {
            if (ResultsList.Items.Count > 0)
            {
                e.Handled = true;
                ResultsList.SelectedIndex = 0;
                if (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem firstItem)
                {
                    firstItem.Focus();
                }
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFlyout();
        }
    }

    private void OnWindowsSearchFallbackClick(object sender, RoutedEventArgs e)
    {
        CloseFlyout();
        SearchDockWindow.OpenWindowsSearch();
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosing || _closeQueued) return;
        _closeQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _closeQueued = false;
            if (!_isClosing && IsVisible && !IsActive)
            {
                CloseFlyout();
            }
        }));
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFlyout();
        }
    }

    private void CloseFlyout()
    {
        if (_isClosing) return;
        _isClosing = true;
        CancelSearch();
        CancelIconLoading();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelSearch();
        CancelIconLoading();
        base.OnClosed(e);
    }
}
