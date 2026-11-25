using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glassquid.Core.Models.Settings;
using Glassquid.UI.Controls;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>ViewModel for individual application audio sessions in the volume mixer.</summary>
internal sealed class AppSessionViewModel : INotifyPropertyChanged
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public BitmapSource? Icon { get; init; }
    public string FallbackGlyph { get; init; } = "\uE767";

    private double _volumePercent;
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_volumePercent - clamped) > 0.4)
            {
                _volumePercent = clamped;
                OnPropertyChanged(nameof(VolumePercent));
                OnPropertyChanged(nameof(VolumePercentText));
                _setVolumeAction?.Invoke((float)(_volumePercent / 100.0));
            }
        }
    }

    public string VolumePercentText => $"{Math.Round(_volumePercent):0}%";

    private readonly Action<float>? _setVolumeAction;

    public AppSessionViewModel(AudioSessionItem session)
    {
        ProcessId = session.ProcessId;
        ProcessName = session.ProcessName;
        DisplayName = session.DisplayName;
        Icon = session.Icon as BitmapSource;
        FallbackGlyph = session.FallbackGlyph;
        _volumePercent = session.Volume * 100.0;
        _setVolumeAction = session.SetVolumeAction;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>Centered flyout window replicating Windows 11 Sound output & Volume mixer.</summary>
public partial class SoundOutputFlyoutWindow : GlassWindow, IDisposable
{
    private readonly SystemTrayBarWindow _dock;
    private AudioEndpointVolumeController? _volumeController;
    private bool _glassRevealPending;
    private bool _isClosing;
    private bool _updatingMasterSlider;
    private bool _disposed;

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    internal SoundOutputFlyoutWindow(SystemTrayBarWindow dock)
    {
        ArgumentNullException.ThrowIfNull(dock);
        _dock = dock;
        Owner = dock;
        InitializeComponent();
        PanelCornerRadius = new CornerRadius(32);
        GlassSurface.IsBackdropActive = () => IsVisible && !_glassRevealPending;
        GlassSurface.SuppressAmbientFallback = true;

        try
        {
            _volumeController = new AudioEndpointVolumeController();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Failed to init AudioEndpointVolumeController: {ex.Message}");
        }

        Loaded += OnPanelLoaded;
    }

    public void PrepareForShow()
    {
        _glassRevealPending = true;
        Opacity = 0;
        RefreshSoundState();
        CenterInWorkArea();
        GlassSurface.PrepareRevealForWindow(this);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(32, appearance.DockCornerRadius);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_glassRevealPending) return;

        _glassRevealPending = false;
        GlassSurface.RequestBackdropRefresh(revealing: true);
        Opacity = 1;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupSlideTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
        PopupVisualRoot.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnPanelLoaded;
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volumeController?.Dispose();
        _volumeController = null;
        GC.SuppressFinalize(this);
    }

    private void CenterInWorkArea()
    {
        if (Content is not UIElement content) return;
        var workArea = WorkAreaDip;
        content.Measure(new Size(workArea.Width, workArea.Height));
        var desired = content.DesiredSize;
        Width = Math.Ceiling(desired.Width);
        Height = Math.Ceiling(desired.Height);
        SizeToContent = SizeToContent.Manual;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPanelLoaded;
        RefreshSoundState();
    }

    private void RefreshSoundState()
    {
        if (_isClosing) return;

        string? currentDefaultId = null;

        // 1. Output devices
        try
        {
            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            currentDefaultId = defaultId;
            OutputDevicesList.ItemsSource = devices;

            var current = devices.Find(d => d.IsDefault);
            if (current != null)
            {
                MasterDeviceIcon.Text = current.IconGlyph;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error refreshing devices: {ex.Message}");
        }

        // 2. Master volume
        try
        {
            if (_volumeController != null)
            {
                _updatingMasterSlider = true;
                var masterPercent = _volumeController.GetMasterVolumePercent();
                MasterVolumeSlider.Value = masterPercent;
                MasterVolumePercentText.Text = $"{masterPercent}%";
                _updatingMasterSlider = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error reading master volume: {ex.Message}");
            _updatingMasterSlider = false;
        }

        // 3. Application sessions
        try
        {
            var sessions = AudioDeviceManager.GetAppAudioSessions();
            var viewModels = new ObservableCollection<AppSessionViewModel>();
            foreach (var s in sessions)
            {
                viewModels.Add(new AppSessionViewModel(s));
            }
            AppSessionsList.ItemsSource = viewModels;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error reading app sessions: {ex.Message}");
        }

        // 4. Spatial sound state
        RefreshSpatialSoundState(currentDefaultId);
    }

    private void RefreshSpatialSoundState(string? deviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                SetSpatialSoundUi(isSupported: false, isSonicActive: false);
                return;
            }

            var config = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(deviceId);
            var isSupported = config.IsSpatialAudioSupported;
            var activeFormat = config.ActiveSpatialAudioFormat;
            var sonicSubtype = Windows.Media.Audio.SpatialAudioFormatSubtype.WindowsSonic;
            var isSonicActive = string.Equals(activeFormat, sonicSubtype, StringComparison.OrdinalIgnoreCase);

            SetSpatialSoundUi(isSupported, isSonicActive);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error checking spatial sound: {ex.Message}");
            SetSpatialSoundUi(isSupported: false, isSonicActive: false);
        }
    }

    private void SetSpatialSoundUi(bool isSupported, bool isSonicActive)
    {
        if (!isSupported)
        {
            SpatialOffIndicator.Visibility = Visibility.Visible;
            SpatialOffSurface.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

            SpatialSonicIndicator.Visibility = Visibility.Collapsed;
            SpatialSonicSurface.Background = Brushes.Transparent;
            SpatialSonicButton.Opacity = 0.45;
            SpatialSonicButton.ToolTip = "Thiết bị âm thanh này không hỗ trợ Windows Sonic for Headphones (chỉ hỗ trợ tai nghe tương thích). Bấm để mở cài đặt âm thanh Windows.";
            SpatialNotSupportedHint.Visibility = Visibility.Visible;
        }
        else
        {
            SpatialSonicButton.Opacity = 1.0;
            SpatialSonicButton.ToolTip = "Bật Windows Sonic for Headphones";
            SpatialNotSupportedHint.Visibility = Visibility.Collapsed;

            if (isSonicActive)
            {
                SpatialOffIndicator.Visibility = Visibility.Collapsed;
                SpatialOffSurface.Background = Brushes.Transparent;

                SpatialSonicIndicator.Visibility = Visibility.Visible;
                SpatialSonicSurface.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                SpatialOffIndicator.Visibility = Visibility.Visible;
                SpatialOffSurface.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

                SpatialSonicIndicator.Visibility = Visibility.Collapsed;
                SpatialSonicSurface.Background = Brushes.Transparent;
            }
        }
    }

    private void OnDeviceItemClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            try
            {
                if (AudioDeviceManager.SetDefaultPlaybackDevice(deviceId, out var hr))
                {
                    // Dispose and re-create controller on new default device
                    _volumeController?.Dispose();
                    _volumeController = new AudioEndpointVolumeController();
                    RefreshSoundState();
                    CenterInWorkArea();
                }
                else
                {
                    // If Windows rejected setting this as main playback device (e.g. ASUS AI Noise-cancelling virtual filter):
                    RefreshSoundState();
                    CenterInWorkArea();

                    // Open Windows Sound Settings so user can see device properties / configure
                    Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoundOutputFlyout] Error switching default device: {ex.Message}");
            }
        }
    }

    private void OnMasterVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMasterSlider || _volumeController == null) return;
        var percent = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        try
        {
            _volumeController.SetMasterVolumePercent(percent);
            MasterVolumePercentText.Text = $"{percent}%";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error setting master volume: {ex.Message}");
        }
    }

    private async void OnSpatialSoundOffClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            if (!string.IsNullOrEmpty(defaultId))
            {
                var config = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(defaultId);
                if (config.IsSpatialAudioSupported)
                {
                    var result = await config.SetDefaultSpatialAudioFormatAsync(string.Empty);
                    if (result.Status == Windows.Media.Audio.SetDefaultSpatialAudioFormatStatus.Succeeded)
                    {
                        SetSpatialSoundUi(isSupported: true, isSonicActive: false);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error disabling spatial sound: {ex.Message}");
        }

        SetSpatialSoundUi(isSupported: false, isSonicActive: false);
    }

    private async void OnSpatialSoundSonicClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            if (!string.IsNullOrEmpty(defaultId))
            {
                var config = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(defaultId);
                if (!config.IsSpatialAudioSupported)
                {
                    Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
                    return;
                }

                var sonic = Windows.Media.Audio.SpatialAudioFormatSubtype.WindowsSonic;
                var result = await config.SetDefaultSpatialAudioFormatAsync(sonic);
                if (result.Status == Windows.Media.Audio.SetDefaultSpatialAudioFormatStatus.Succeeded)
                {
                    SetSpatialSoundUi(isSupported: true, isSonicActive: true);
                    return;
                }
                else
                {
                    Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error activating spatial sound: {ex.Message}");
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
    }

    private void OnOpenMixerSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error opening mixer settings: {ex.Message}");
        }
    }

    private void OnMoreVolumeSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoundOutputFlyout] Error opening sound settings: {ex.Message}");
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ClosePanel();
        _dock.OpenQuickSettingsFlyout();
    }

    private void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClosePanel();
        e.Handled = true;
    }

    private void OnPopupDeactivated(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_isClosing && !IsActive) ClosePanel();
        }));
    }

    private void ClosePanel()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }
}
