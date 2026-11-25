using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glassquid.AppServices.Updates;

namespace Glassquid.App.Updates;

/// <summary>A single release prompt whose download lifetime belongs to the open window.</summary>
internal sealed class UpdateWindow : Window, IDisposable
{
    private readonly Func<IProgress<int>, CancellationToken, Task> _install;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _update;
    private readonly Button _later;
    private bool _busy;
    private bool _handedOff;
    private bool _disposed;

    public UpdateWindow(UpdateRelease release, bool canInstall, Func<IProgress<int>, CancellationToken, Task> install)
    {
        _install = install;
        Title = "Cập nhật Glassquid";
        Width = 520; Height = 480; MinWidth = 430; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(23, 26, 33));
        Foreground = new SolidColorBrush(Color.FromRgb(239, 243, 249));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var grid = new Grid { Margin = new Thickness(26) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Add(new TextBlock { Text = $"Đã có phiên bản mới {release.Version}", FontSize = 24, FontWeight = FontWeights.SemiBold }, 0);
        Add(new TextBlock { Text = "Sau khi tải xong, Glassquid sẽ đóng để cài đặt bản cập nhật rồi tự mở lại.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) }, 1);
        var notes = new TextBlock { Text = string.IsNullOrWhiteSpace(release.Notes) ? "Phiên bản này không có ghi chú cập nhật." : release.Notes, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
        AutomationProperties.SetName(notes, "Ghi chú cập nhật");
        Add(new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 16) }, 2);
        _status = new TextBlock { Text = canInstall ? "Nhấn “Cập nhật ngay” khi bạn sẵn sàng." : "Ứng dụng đang chạy ở chế độ portable hoặc thông tin cài đặt không khớp. Hãy cài đặt gói chính thức theo cách thủ công.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(_status, "Trạng thái tải bản cập nhật");
        Add(_status, 3);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 18) };
        AutomationProperties.SetName(_progress, "Tiến trình tải bản cập nhật");
        Add(_progress, 4);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _later = Button("Để sau", "Cập nhật Glassquid sau");
        _update = Button("Cập nhật ngay", "Cập nhật ngay Glassquid");
        _update.IsEnabled = canInstall;
        _later.Click += (_, _) => Close();
        _update.Click += OnUpdateClick;
        buttons.Children.Add(_later); buttons.Children.Add(_update);
        Add(buttons, 5);
        Content = grid;
        Closing += (_, e) => { if (_handedOff) return; _cancellation.Cancel(); };
        Closed += (_, _) => Dispose();

        void Add(UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_handedOff) _cancellation.Cancel();
        if (!_busy) _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    public void MarkInstalling()
    {
        _status.Text = "Đang khởi chạy trình cài đặt. Glassquid sẽ tự mở lại sau khi cập nhật xong.";
        _later.IsEnabled = false;
        _handedOff = true;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _update.IsEnabled = false;
        _later.Content = "Hủy tải xuống";
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Đang tải bản cập nhật 0%";
        try
        {
            await _install(new Progress<int>(value =>
            {
                if (_handedOff || _cancellation.IsCancellationRequested) return;
                _progress.Value = value;
                _status.Text = value == 100 ? "Đã tải xong; đang xác minh thông tin cài đặt…" : $"Đang tải bản cập nhật {value}%";
            }), _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsVisible) _status.Text = "Tải xuống bị hủy hoặc quá thời gian chờ. Hãy thử lại.";
        }
        catch (Exception ex)
        {
            if (IsVisible) _status.Text = "Cập nhật thất bại: " + ex.Message;
        }
        finally
        {
            _busy = false;
            if (!IsVisible) _cancellation.Dispose();
            else if (!_handedOff)
            {
                _update.IsEnabled = true;
                _update.Content = "Thử cập nhật lại";
                _later.Content = "Để sau";
            }
        }
    }

    private static Button Button(string text, string automationName)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(42, 52, 67)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(70, 83, 104)), MinWidth = 100,
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }
}
