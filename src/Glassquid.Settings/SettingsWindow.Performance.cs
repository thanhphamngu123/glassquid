using System.Windows;
using Glassquid.Core.Models.Settings;

namespace Glassquid.Settings;

public partial class SettingsWindow
{
    private bool _loadingPerformanceControls = true;
    public event EventHandler? PerformanceSettingsChanged;

    private void LoadPerformanceControls()
    {
        _loadingPerformanceControls = true;
        try
        {
            AutoSuspendWhenFullscreenCheck.IsChecked = _dataStore?.Settings.Current.Performance.AutoSuspendWhenFullscreen ?? true;
            AutoSuspendWhenFullscreenCheck.IsEnabled = _dataStore is not null;
        }
        finally { _loadingPerformanceControls = false; }
    }

    private void OnPerformanceControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPerformanceControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var previous = current.Performance;
        try
        {
            current.Performance = new PerformanceSettings { AutoSuspendWhenFullscreen = AutoSuspendWhenFullscreenCheck.IsChecked == true };
            _dataStore.Settings.Save();
        }
        catch (Exception error)
        {
            current.Performance = previous;
            LoadPerformanceControls();
            PerformanceSettingsStatus.Text = "Lưu thất bại; đã khôi phục thiết lập trước đó: " + error.Message;
            return;
        }
        PerformanceSettingsChanged?.Invoke(this, EventArgs.Empty);
        PerformanceSettingsStatus.Text = "Đã lưu và áp dụng. Với trò chơi chạy ở chế độ cửa sổ, bạn có thể bật chế độ tạm dừng khi chơi game từ khay hệ thống.";
    }
}
