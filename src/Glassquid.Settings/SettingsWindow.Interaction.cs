using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Glassquid.Core.Input;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Platform.Installation;
using Glassquid.UI.Settings;
using Glassquid.UI.Theming;

namespace Glassquid.Settings;

public partial class SettingsWindow
{
    private bool _userDataDeletionPending;
    private InstallationInfo? _installationInfo;
    private Func<bool?>? _desktopIconsRead;
    private Func<bool, string?>? _desktopIconsSet;
    private bool _loadingWindowsTaskbarVisibility;

    /// <summary>Applies temporary taskbar visibility and repositions Glassquid bars.</summary>
    public Func<bool, string?>? WindowsTaskbarVisibilityApply { get; set; }

    /// <summary>Áp dụng cài đặt thao tác nhanh và màn hình, đồng thời lưu trữ lâu dài. Trả về mô tả lỗi; null nghĩa là thành công.</summary>
    public Func<ShortcutSettings, DisplaySettings, string?>? InteractionSettingsApply { get; set; }

    /// <summary>Thông báo máy chủ kết thúc tiến trình ngay sau khi khôi phục toàn bộ dữ liệu để tránh trạng thái runtime cũ ghi đè kết quả khôi phục.</summary>
    public event EventHandler? DataRestored;

    /// <summary>Yêu cầu máy chủ dừng mọi thao tác ghi và thực hiện bảo trì sau khi người dùng xác nhận xóa toàn bộ dữ liệu Glassquid.</summary>
    public event EventHandler? UserDataDeletionRequested;

    /// <summary>Thông báo máy chủ sau khi người dùng xác nhận khởi chạy trình gỡ cài đặt đã xác minh.</summary>
    public event EventHandler? UninstallRequested;

    public void ShowTaskbarVisibilityStatus(string message) => TaskbarVisibilityStatus.Text = message ?? string.Empty;

    /// <summary>Gắn kết quả xác minh chỉ đọc; bản thân cửa sổ cài đặt không đọc hoặc thực thi đăng ký gỡ cài đặt.</summary>
    public void AttachInstallation(InstallationInfo installationInfo)
    {
        ArgumentNullException.ThrowIfNull(installationInfo);
        _installationInfo = installationInfo;
        UninstallButton.IsEnabled = installationInfo.State == InstallationState.VerifiedInstalled;
        InstallationStatus.Text = installationInfo.State switch
        {
            InstallationState.Portable => "Glassquid đang chạy ở chế độ portable. Bạn có thể xóa thư mục chương trình sau khi thoát; hãy quản lý dữ liệu người dùng riêng trong mục “Sao lưu và khôi phục”.",
            InstallationState.VerifiedInstalled => $"Đã xác minh bản cài đặt và chương trình gỡ cài đặt: {installationInfo.UninstallerPath}\nGỡ cài đặt sẽ không xóa dữ liệu người dùng Glassquid.",
            _ => "Thông tin cài đặt không đầy đủ hoặc không khớp với ứng dụng hiện tại. Đã tắt chức năng gỡ cài đặt." + Environment.NewLine + installationInfo.Error,
        };
    }

    /// <summary>Đọc lại thông tin đăng ký cài đặt trước khi mở trang cài đặt để tránh dùng trạng thái cũ từ lúc khởi động.</summary>
    public void RefreshInstallationStatus(Func<InstallationInfo> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        AttachInstallation(inspect());
    }

    /// <summary>Báo lỗi khởi chạy trình gỡ cài đặt và cho phép người dùng thử lại sau.</summary>
    public void ShowUninstallLaunchError(string message)
    {
        InstallationStatus.Text = "Không thể khởi chạy chương trình gỡ cài đặt đã xác minh: " + message;
        UninstallButton.IsEnabled = _installationInfo?.State == InstallationState.VerifiedInstalled;
    }

    /// <summary>Vô hiệu hóa ghi cài đặt để máy chủ gọi khi bắt đầu quy trình bảo trì dữ liệu hoặc thoát.</summary>
    public void PrepareForUserDataMaintenance()
    {
        SettingsScroll.IsEnabled = false;
        SettingsPageList.IsEnabled = false;
        ResetPageButton.IsEnabled = false;
        _dataStore = null;
    }

    /// <summary>Hiển thị trạng thái thao tác nhanh do dịch vụ chạy phát hiện, ví dụ phím tắt đang bị chương trình khác chiếm.</summary>
    public void ShowInteractionStatus(string message) => InteractionSettingsStatus.Text = message ?? string.Empty;

    /// <summary>Đọc trạng thái hiển thị hiện tại của biểu tượng màn hình nền Windows; null nghĩa là không hỗ trợ hoặc không khả dụng.</summary>
    public Func<bool?>? DesktopIconsRead
    {
        get => _desktopIconsRead;
        set { _desktopIconsRead = value; if (IsInitialized) LoadDesktopStatus(); }
    }

    /// <summary>Yêu cầu Windows hiển thị hoặc ẩn biểu tượng màn hình nền; trả về mô tả lỗi, null nghĩa là thành công.</summary>
    public Func<bool, string?>? DesktopIconsSet
    {
        get => _desktopIconsSet;
        set { _desktopIconsSet = value; if (IsInitialized) LoadDesktopStatus(); }
    }

    private void LoadAppearanceControls()
    {
        if (_dataStore is null) return;
        _loadingAppearanceControls = true;
        try
        {
            var appearance = _dataStore.Settings.Current.Appearance;
            AppearanceMaterialCombo.SelectedIndex = appearance.GlassMaterial == GlassMaterialMode.FrostedGlass ? 1 : 0;
            LaunchpadIconStyleCombo.SelectedIndex = appearance.LaunchpadIconFilled ? 0 : 1;
            DockTransparencySlider.Value = appearance.DockGlassTransparency;
            LauncherTransparencySlider.Value = appearance.LauncherGlassTransparency;
            DockTransparencyValue.Text = appearance.DockGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
            LauncherTransparencyValue.Text = appearance.LauncherGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
            DockBuiltinIconBrightnessSlider.Value = appearance.DockBuiltinIconBrightness;
            LauncherTextBrightnessSlider.Value = appearance.LauncherTextBrightness;
            DockBuiltinIconBrightnessValue.Text = appearance.DockBuiltinIconBrightness.ToString("P0", CultureInfo.CurrentCulture);
            LauncherTextBrightnessValue.Text = appearance.LauncherTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
            ReduceMotionCheck.IsChecked = appearance.ReduceMotion;
            AppearanceStatus.Text = "Cửa sổ cài đặt luôn dùng nền tối đục; độ trong suốt kính áp dụng cho Dock và Launchpad.";
        }
        finally { _loadingAppearanceControls = false; }
    }

    private void OnAppearanceControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingAppearanceControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var snapshot = SettingsPageEditor.Clone(current);
        current.Appearance.GlassMaterial = AppearanceMaterialCombo.SelectedIndex == 1
            ? GlassMaterialMode.FrostedGlass : GlassMaterialMode.AndroidLiquidGlass;
        current.Appearance.ReduceMotion = ReduceMotionCheck.IsChecked == true;
        current.Appearance.LaunchpadIconFilled = LaunchpadIconStyleCombo.SelectedIndex != 1;
        SavePageSnapshot(snapshot, "Giao diện", AppearanceStatus);
        // A failed save replaces Appearance; the live theme must follow the restored snapshot.
        if (ThemeService.Current is { } theme && !ReferenceEquals(theme.Appearance, current.Appearance))
            theme.Update(current.Appearance);
    }

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingAppearanceControls || !IsInitialized || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var snapshot = SettingsPageEditor.Clone(current);
        if (ReferenceEquals(sender, DockTransparencySlider))
            current.Appearance.DockGlassTransparency = DockTransparencySlider.Value;
        else if (ReferenceEquals(sender, LauncherTransparencySlider))
            current.Appearance.LauncherGlassTransparency = LauncherTransparencySlider.Value;
        else if (ReferenceEquals(sender, DockBuiltinIconBrightnessSlider))
            current.Appearance.DockBuiltinIconBrightness = DockBuiltinIconBrightnessSlider.Value;
        else if (ReferenceEquals(sender, LauncherTextBrightnessSlider))
            current.Appearance.LauncherTextBrightness = LauncherTextBrightnessSlider.Value;
        else return;
        SavePageSnapshot(snapshot, "Giao diện", AppearanceStatus);
        // Rollback replaces Appearance; refresh the preview's reference after a failed save.
        if (ThemeService.Current is { } theme && !ReferenceEquals(theme.Appearance, current.Appearance))
            theme.Update(current.Appearance);
        DockTransparencyValue.Text = current.Appearance.DockGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
        LauncherTransparencyValue.Text = current.Appearance.LauncherGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
        DockBuiltinIconBrightnessValue.Text = current.Appearance.DockBuiltinIconBrightness.ToString("P0", CultureInfo.CurrentCulture);
        LauncherTextBrightnessValue.Text = current.Appearance.LauncherTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
    }

    private void LoadInteractionControls()
    {
        if (_dataStore is null) return;
        try
        {
            var current = _dataStore.Settings.Current;
            ToggleLauncherHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleLauncher);
            NextPageHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.NextPage);
            PreviousPageHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.PreviousPage);
            ToggleDesktopIconsHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleDesktopIcons);
            ToggleDockRevealLockHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleDockRevealLock);
            InteractionSettingsStatus.Text = "Phím tắt sẽ tự lưu và áp dụng sau khi ghi xong.";
        }
        catch (Exception ex)
        {
            InteractionSettingsStatus.Text = "Không thể tải cài đặt thao tác nhanh: " + ex.Message;
        }
    }

    private void OnApplyInteractionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { InteractionSettingsStatus.Text = "Chưa tải dữ liệu cài đặt."; return; }
        if (InteractionSettingsApply is null) { InteractionSettingsStatus.Text = "Dịch vụ ứng dụng chưa kết nối; thay đổi chưa được lưu."; return; }
        if (!TryReadHotkey(ToggleLauncherHotkeyBox, out var toggle)
            || !TryReadHotkey(NextPageHotkeyBox, out var next) || !TryReadHotkey(PreviousPageHotkeyBox, out var previous)
            || !TryReadHotkey(ToggleDesktopIconsHotkeyBox, out var icons)
            || !TryReadHotkey(ToggleDockRevealLockHotkeyBox, out var dockRevealLock)) return;
        var shortcuts = new ShortcutSettings
        {
            ToggleLauncher = toggle, HideLauncher = new(), NextPage = next, PreviousPage = previous,
            ToggleDesktopIcons = icons, ToggleDockRevealLock = dockRevealLock,
        };
        var display = new DisplaySettings
        {
            DockDisplayId = _dataStore.Settings.Current.Display.DockDisplayId,
            LauncherFollowsPointer = _dataStore.Settings.Current.Display.LauncherFollowsPointer,
            SuppressTriggersWhenFullscreen = _dataStore.Settings.Current.Display.SuppressTriggersWhenFullscreen,
        };
        var candidate = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        candidate.Shortcuts = shortcuts;
        candidate.Display = display;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null) { LoadInteractionControls(); InteractionSettingsStatus.Text = "Thiết lập không hợp lệ: " + validationError; return; }
        try
        {
            var error = InteractionSettingsApply(shortcuts, display);
            if (error is not null)
            {
                LoadInteractionControls();
                InteractionSettingsStatus.Text = "Áp dụng thất bại; đã khôi phục thiết lập trước đó: " + error;
                return;
            }
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "Đã áp dụng và lưu cài đặt thao tác nhanh.";
        }
        catch (Exception ex)
        {
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "Áp dụng thất bại; đã khôi phục thiết lập trước đó: " + ex.Message;
        }
    }

    private void OnShortcutRecordingCompleted(object? sender, EventArgs e)
    {
        if ((_dataStore is null || InteractionSettingsApply is null) && sender is ShortcutRecorder recorder)
            recorder.RestorePreviousBinding();
        OnApplyInteractionSettingsClick(sender ?? this, new RoutedEventArgs());
    }

    public bool TryCaptureRegisteredHotkey(nint packedChord)
    {
        var recorder = new[] { ToggleLauncherHotkeyBox, NextPageHotkeyBox,
            PreviousPageHotkeyBox, ToggleDesktopIconsHotkeyBox, ToggleDockRevealLockHotkeyBox }
            .FirstOrDefault(control => control.IsRecording && control.IsKeyboardFocusWithin);
        if (recorder is null) return false;
        var packed = unchecked((uint)packedChord.ToInt64());
        var modifiers = System.Windows.Input.ModifierKeys.None;
        if ((packed & 1) != 0) modifiers |= System.Windows.Input.ModifierKeys.Alt;
        if ((packed & 2) != 0) modifiers |= System.Windows.Input.ModifierKeys.Control;
        if ((packed & 4) != 0) modifiers |= System.Windows.Input.ModifierKeys.Shift;
        if ((packed & 8) != 0) modifiers |= System.Windows.Input.ModifierKeys.Windows;
        var virtualKey = (int)(packed >> 16);
        if (virtualKey is 5 or 6) recorder.CaptureMouseButton(virtualKey == 5 ? 1 : 2, modifiers);
        else if (virtualKey is 0x100 or 0x101) recorder.CaptureWheel(virtualKey == 0x100 ? 120 : -120, modifiers);
        else recorder.CaptureKey(System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey), modifiers);
        return true;
    }

    private bool TryReadHotkey(ShortcutRecorder box, out HotkeyDefinition value)
    {
        if (HotkeyText.TryParse(box.Text, out value, out var error)) return true;
        InteractionSettingsStatus.Text = $"Không nhận diện được {AutomationProperties.GetName(box)}: {error}";
        box.Focus();
        return false;
    }

    private void LoadDesktopStatus()
    {
        bool? state;
        try { state = DesktopIconsRead?.Invoke(); }
        catch (Exception ex)
        {
            DesktopIconsToggleButton.IsEnabled = false;
            DesktopSettingsStatus.Text = "Không thể đọc trạng thái biểu tượng màn hình: " + ex.Message;
            return;
        }
        DesktopIconsToggleButton.IsEnabled = state.HasValue && DesktopIconsSet is not null;
        DesktopSettingsStatus.Text = state.HasValue
            ? $"Biểu tượng trên màn hình hiện đang {(state.Value ? "hiển thị" : "ẩn")}."
            : "Môi trường hiện tại không hỗ trợ đọc hoặc bật/tắt biểu tượng trên màn hình.";
    }

    private void LoadWindowsTaskbarVisibility()
    {
        if (_dataStore is null) return;
        _loadingWindowsTaskbarVisibility = true;
        try
        {
            HideWindowsTaskbarCheck.IsChecked = _dataStore.Settings.Current.DesktopTaskbar.HideWindowsTaskbar;
            TaskbarVisibilityStatus.Text = HideWindowsTaskbarCheck.IsChecked == true
                ? "Tùy chọn ẩn taskbar Windows đang bật."
                : "Taskbar Windows đang được giữ theo trạng thái hiển thị hiện tại.";
        }
        finally
        {
            _loadingWindowsTaskbarVisibility = false;
        }
    }

    private void OnWindowsTaskbarVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingWindowsTaskbarVisibility || _dataStore is null) return;
        var taskbarSettings = _dataStore.Settings.Current.DesktopTaskbar;
        var previous = taskbarSettings.HideWindowsTaskbar;
        var next = HideWindowsTaskbarCheck.IsChecked == true;
        if (WindowsTaskbarVisibilityApply is null)
        {
            LoadWindowsTaskbarVisibility();
            TaskbarVisibilityStatus.Text = "Dịch vụ taskbar chưa sẵn sàng; cài đặt chưa được lưu.";
            return;
        }

        var applyError = WindowsTaskbarVisibilityApply(next);
        if (applyError is not null)
        {
            LoadWindowsTaskbarVisibility();
            TaskbarVisibilityStatus.Text = "Không thể áp dụng: " + applyError;
            return;
        }

        taskbarSettings.HideWindowsTaskbar = next;
        try
        {
            _dataStore.Settings.Save();
            LoadWindowsTaskbarVisibility();
            TaskbarVisibilityStatus.Text = next
                ? "Đã ẩn taskbar Windows; Dock và thanh trạng thái đã căn xuống mép màn hình."
                : "Đã hiện lại taskbar Windows và khôi phục vị trí các thanh Glassquid.";
        }
        catch (Exception ex)
        {
            taskbarSettings.HideWindowsTaskbar = previous;
            var rollbackError = WindowsTaskbarVisibilityApply(previous);
            LoadWindowsTaskbarVisibility();
            TaskbarVisibilityStatus.Text = "Lưu thất bại; đã khôi phục trạng thái trước đó."
                + (rollbackError is null ? string.Empty : " Taskbar chưa khôi phục được: " + rollbackError)
                + " " + ex.Message;
        }
    }

    private void OnDesktopIconsToggleClick(object sender, RoutedEventArgs e)
    {
        bool? current;
        try { current = DesktopIconsRead?.Invoke(); }
        catch (Exception ex) { DesktopSettingsStatus.Text = "Không thể đọc trạng thái biểu tượng màn hình: " + ex.Message; return; }
        if (!current.HasValue || DesktopIconsSet is null) { LoadDesktopStatus(); return; }
        string? error;
        try { error = DesktopIconsSet(!current.Value); }
        catch (Exception ex) { error = ex.Message; }
        if (error is not null) DesktopSettingsStatus.Text = "Không thể thay đổi cài đặt biểu tượng màn hình: " + error;
        else LoadDesktopStatus();
    }

    private void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { BackupStatus.Text = "Chưa tải dữ liệu cài đặt."; return; }
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục lưu bản sao lưu Glassquid", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var result = _dataStore.Backup.BackupTo(dialog.FolderName);
        BackupStatus.Text = result.Succeeded
            ? $"Đã tạo bản sao lưu tại {result.Directory} ({result.Files.Count} tệp)."
            : result.Error ?? "Không thể tạo bản sao lưu.";
    }

    private void OnDeleteUserDataClick(object sender, RoutedEventArgs e)
    {
        if (_userDataDeletionPending || _dataStore is null) return;
        if (UserDataDeletionRequested is null)
        {
            BackupStatus.Text = "Ứng dụng chưa kết nối với quy trình bảo trì an toàn; chưa xóa dữ liệu.";
            return;
        }

        var dataRoot = _dataStore.Paths.RootDirectory;
        var answer = MessageBox.Show(
            this,
            $"Thao tác này sẽ xóa toàn bộ dữ liệu người dùng Glassquid trong thư mục sau:\n\n{dataRoot}\n\nDữ liệu gồm cài đặt, thư viện ứng dụng, bố cục, biểu tượng tùy chỉnh, bản sao lưu nội bộ và nhật ký. Ứng dụng sẽ không bị gỡ cài đặt; các tệp đã nhập hoặc bản sao lưu bên ngoài thư mục này và mục khởi chạy cùng Windows sẽ được giữ nguyên. Bạn nên tạo bản sao lưu ở trên và lưu bên ngoài thư mục này.\n\nGlassquid sẽ dừng và thoát sau khi xác nhận. Bạn có muốn tiếp tục không?",
            "Xóa toàn bộ dữ liệu Glassquid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _userDataDeletionPending = true;
        DeleteUserDataButton.IsEnabled = false;
        PrepareForUserDataMaintenance();
        UserDataDeletionRequested.Invoke(this, EventArgs.Empty);
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_installationInfo?.State != InstallationState.VerifiedInstalled || _userDataDeletionPending) return;
        if (UninstallRequested is null)
        {
            ShowUninstallLaunchError("Ứng dụng chưa kết nối với quy trình gỡ cài đặt.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Sẽ khởi chạy trình gỡ cài đặt Glassquid đã xác minh:\n\n{_installationInfo.UninstallerPath}\n\nThao tác này chỉ xóa các tệp chương trình đã cài đặt, không xóa dữ liệu người dùng Glassquid. Bạn có muốn tiếp tục không?",
            "Gỡ cài đặt Glassquid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        UninstallButton.IsEnabled = false;
        UninstallRequested.Invoke(this, EventArgs.Empty);
    }

    private void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { BackupStatus.Text = "Chưa tải dữ liệu cài đặt."; return; }
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục chứa bản sao lưu Glassquid", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        if (DataRestored is null)
        {
            BackupStatus.Text = "Ứng dụng chưa kết nối với quy trình thoát an toàn; chưa khôi phục dữ liệu.";
            return;
        }
        var answer = MessageBox.Show(this,
            "Khôi phục sẽ thay thế dữ liệu Glassquid hiện tại bằng bản sao lưu đã chọn. Trước tiên, hệ thống sẽ lưu một bản sao dữ liệu hiện tại. Hãy khởi động lại Glassquid sau khi khôi phục để tải đầy đủ dữ liệu. Bạn có muốn tiếp tục không?",
            "Khôi phục bản sao lưu Glassquid", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var result = _dataStore.Backup.RestoreFrom(dialog.FolderName);
        if (!result.Succeeded)
        {
            BackupStatus.Text = result.Error ?? "Không thể khôi phục bản sao lưu.";
            return;
        }
        BackupStatus.Text = $"Đã khôi phục {result.Files.Count} tệp từ bản sao lưu. Bản sao an toàn của dữ liệu trước đó nằm tại: {result.SafetyCopyDirectory}. Hãy khởi động lại Glassquid. Các mục cài đặt trong cửa sổ này đã bị khóa để tránh ghi đè dữ liệu vừa khôi phục.";
        SettingsScroll.IsEnabled = false;
        SettingsPageList.IsEnabled = false;
        ResetPageButton.IsEnabled = false;
        _dataStore = null;
        DataRestored?.Invoke(this, EventArgs.Empty);
    }

    private void OnResetPageClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null || !CanResetSelectedPage) return;
        var page = PageKeys[SettingsPageList.SelectedIndex];
        var target = _dataStore.Settings.Current;
        var old = SettingsPageEditor.Clone(target);
        // Keep the persisted model and retained Core assembly compatible with earlier releases.
        var reset = SettingsPageEditor.ResetPage(old, page == "Màn hình nền" ? "Màn hình nền và thanh tác vụ" : page);
        var validationError = SettingsPageEditor.Validate(reset);
        if (validationError is not null) { StatusFor(page).Text = "Không thể khôi phục mặc định: " + validationError; return; }
        if (page == "Màn hình nền")
        {
            if (WindowsTaskbarVisibilityApply is null)
            {
                TaskbarVisibilityStatus.Text = "Dịch vụ taskbar chưa sẵn sàng; cài đặt chưa được khôi phục.";
                return;
            }
            var taskbarError = WindowsTaskbarVisibilityApply(reset.DesktopTaskbar.HideWindowsTaskbar);
            if (taskbarError is not null)
            {
                TaskbarVisibilityStatus.Text = "Không thể khôi phục cài đặt taskbar: " + taskbarError;
                return;
            }
        }
        if (page == "Thao tác nhanh")
        {
            if (InteractionSettingsApply is null) { InteractionSettingsStatus.Text = "Dịch vụ ứng dụng chưa kết nối; thay đổi chưa được lưu."; return; }
            var error = InteractionSettingsApply(reset.Shortcuts, reset.Display);
            if (error is not null) { InteractionSettingsStatus.Text = "Khôi phục mặc định thất bại; đã phục hồi thiết lập trước đó: " + error; LoadInteractionControls(); return; }
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "Đã khôi phục cài đặt mặc định của trang này.";
            return;
        }
        try
        {
            SettingsPageEditor.Apply(target, reset);
            _dataStore.Settings.Save();
        }
        catch (Exception ex)
        {
            SettingsPageEditor.Apply(target, old);
            if (page == "Màn hình nền")
                _ = WindowsTaskbarVisibilityApply?.Invoke(old.DesktopTaskbar.HideWindowsTaskbar);
            ReloadPage(page);
            StatusFor(page).Text = "Khôi phục mặc định thất bại; đã phục hồi thiết lập trước đó: " + ex.Message;
            return;
        }
        NotifyPageApplied(page);
        ReloadPage(page);
        if (page == "Màn hình nền")
            DesktopSettingsStatus.Text += " Cài đặt của trang này đã được khôi phục về mặc định; trạng thái biểu tượng màn hình của Windows không thay đổi.";
    }

    private void SavePageSnapshot(GlassquidSettings snapshot, string page, TextBlock status)
    {
        var validationError = SettingsPageEditor.Validate(_dataStore!.Settings.Current);
        if (validationError is not null)
        {
            SettingsPageEditor.Apply(_dataStore.Settings.Current, snapshot);
            LoadAppearanceControls();
            LoadDockControls();
            status.Text = "Thiết lập không hợp lệ; đã khôi phục thiết lập trước đó: " + validationError;
            return;
        }
        try
        {
            _dataStore!.Settings.Save();
        }
        catch (Exception ex)
        {
            SettingsPageEditor.Apply(_dataStore!.Settings.Current, snapshot);
            LoadAppearanceControls();
            LoadDockControls();
            status.Text = "Lưu thất bại; đã khôi phục thiết lập trước đó: " + ex.Message;
            return;
        }
        NotifyPageApplied(page);
        status.Text = "Đã lưu và áp dụng.";
    }

    private void NotifyPageApplied(string page)
    {
        var current = _dataStore!.Settings.Current;
        if (page == "Giao diện") ThemeService.Current?.Update(current.Appearance);
        if (page == "Dock")
        {
            DockSettingsHost.Current?.Update(current.Dock);
            ThemeService.Current?.Update(current.Appearance);
        }
        if (page == "Launchpad") LauncherSettingsChanged?.Invoke(this, current.Launcher);
        if (page == "Hiệu năng") PerformanceSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadPage(string page)
    {
        switch (page)
        {
            case "Giao diện": LoadAppearanceControls(); ThemeService.Current?.Update(_dataStore!.Settings.Current.Appearance); break;
            case "Dock": LoadDockControls(); DockSettingsHost.Current?.Update(_dataStore!.Settings.Current.Dock); break;
            case "Launchpad": LoadLauncherControls(); LauncherSettingsChanged?.Invoke(this, _dataStore!.Settings.Current.Launcher); break;
            case "Thao tác nhanh": LoadInteractionControls(); break;
            case "Hiệu năng": LoadPerformanceControls(); break;
            case "Màn hình nền": LoadWindowsTaskbarVisibility(); LoadDesktopStatus(); break;
        }
    }

    private TextBlock StatusFor(string page) => page switch
    {
        "Giao diện" => AppearanceStatus, "Dock" => DockSettingsStatus, "Launchpad" => LauncherSettingsStatus,
        "Thao tác nhanh" => InteractionSettingsStatus, "Hiệu năng" => PerformanceSettingsStatus, _ => DesktopSettingsStatus,
    };
}
