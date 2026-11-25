using System.IO;
using System.Text.Json;
using Glassquid.Core.Input;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;

namespace Glassquid.App.Startup;

/// <summary>Chạy nghiệm thu quy tắc thuần túy và lưu trữ bền vững Giai đoạn 6 với thư mục dữ liệu độc lập, không tạo cửa sổ sản phẩm.</summary>
internal static class Phase6Acceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Keep internal callers on the same guarded path as the CLI.
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2) return 2;
        return AcceptanceEntryPoint.Run(realPaths, arguments[1], [new("report", arguments[0])],
            workspace => Run(realPaths, arguments, workspace));
    }

    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, Glassquid.Platform.Storage.AcceptanceWorkspace workspace)
    {
        var reportPath = arguments.Count > 0 && !string.IsNullOrWhiteSpace(arguments[0])
            ? Path.GetFullPath(arguments[0])
            : Path.Combine(Environment.CurrentDirectory, "docs", "evidence", "Giai đoạn 6", "phase6-cli.json");
        var root = workspace.DataRoot;
        var cases = new List<object>();
        var failed = 0;

        void Check(string name, bool okay, string expected, string actual)
        {
            if (!okay) failed++;
            cases.Add(new { name, driver = "settings rules and isolated files", expected, actual,
                status = okay ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
        }

        if (DataDirectoryGuard.Overlaps(root, realPaths.RootDirectory))
        {
            Check("Bảo vệ chồng lấn thư mục dữ liệu", false, "Từ chối thư mục trùng hoặc là thư mục cha/con của thư mục dữ liệu người dùng hiện tại", "Thư mục ứng viên bị chồng lấn, chưa đọc hoặc tạo thư mục ứng viên");
            Write(workspace.GetOutput("report"), reportPath, root, cases, 0, failed, 2);
            return 2;
        }

        var comparisons = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Check("Thư mục người dùng thật tự được bảo vệ", DataDirectoryGuard.Overlaps(realPaths.RootDirectory, realPaths.RootDirectory),
            "Trả về có chồng lấn", "Dùng DataDirectoryGuard.Overlaps");
        Check("Thư mục con của thư mục người dùng thật được bảo vệ", DataDirectoryGuard.Overlaps(Path.Combine(realPaths.RootDirectory, "phase6-probe"), realPaths.RootDirectory),
            "Trả về có chồng lấn", "Dùng DataDirectoryGuard.Overlaps");
        var sibling = Path.Combine(Path.GetDirectoryName(realPaths.RootDirectory)!, "Glassquid-Phase6-Sibling");
        Check("Thư mục cùng cấp không bị nhận nhầm là chồng lấn", !DataDirectoryGuard.Overlaps(sibling, realPaths.RootDirectory),
            "Trả về không chồng lấn", $"Thư mục thật={realPaths.RootDirectory}; Ứng viên={sibling}; comparison={comparisons}");

        var paths = new AppPaths(root);
        try
        {
            paths.EnsureCreated();
            var data = new GlassquidDataStore(paths);
            data.LoadAll();
            var changed = SettingsPageEditor.Clone(data.Settings.Current);
            changed.Launcher.Rows = 4;
            changed.Shortcuts.HotCornerDwellMs = 777;
            SettingsPageEditor.Apply(data.Settings.Current, changed);
            data.Settings.Save();
            var reloaded = new GlassquidDataStore(paths);
            reloaded.LoadAll();
            Check("Cài đặt mới sau khi lưu có thể tải lại bằng thực thể mới", reloaded.Settings.Current.Launcher.Rows == 4
                && reloaded.Settings.Current.Shortcuts.HotCornerDwellMs == 777,
                "Launcher.Rows=4 và thời gian dừng=777", $"Launcher.Rows={reloaded.Settings.Current.Launcher.Rows}; dwell={reloaded.Settings.Current.Shortcuts.HotCornerDwellMs}");

            var beforeReset = SettingsPageEditor.Clone(reloaded.Settings.Current);
            var resetShortcuts = SettingsPageEditor.ResetPage(beforeReset, "Thao tác nhanh");
            Check("Khôi phục mặc định một trang giữ lại cài đặt Launchpad", resetShortcuts.Launcher.Rows == beforeReset.Launcher.Rows
                && resetShortcuts.Shortcuts.HotCornerDwellMs == new ShortcutSettings().HotCornerDwellMs,
                $"Số hàng Launchpad giữ nguyên {beforeReset.Launcher.Rows} và Thao tác nhanh về mặc định", $"Số hàng={resetShortcuts.Launcher.Rows}; dwell={resetShortcuts.Shortcuts.HotCornerDwellMs}");

            resetShortcuts.Shortcuts.HotCornerDwellMs = 99;
            Check("Quy tắc phạm vi từ chối thời gian dừng thấp hơn biên", SettingsPageEditor.Validate(resetShortcuts) is not null,
                "Xác thực trả về lỗi", SettingsPageEditor.Validate(resetShortcuts) ?? "Không trả về lỗi");
            resetShortcuts.Shortcuts.HotCornerDwellMs = 3001;
            Check("Quy tắc phạm vi từ chối thời gian dừng cao hơn biên", SettingsPageEditor.Validate(resetShortcuts) is not null,
                "Xác thực trả về lỗi", SettingsPageEditor.Validate(resetShortcuts) ?? "Không trả về lỗi");

            var invalid = SettingsPageEditor.Clone(reloaded.Settings.Current);
            invalid.Shortcuts.ToggleLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "NOT_A_KEY" };
            Check("Quy tắc phím tắt từ chối tên phím không hợp lệ", SettingsPageEditor.Validate(invalid) is not null,
                "Xác thực trả về lỗi tên phím", SettingsPageEditor.Validate(invalid) ?? "Không trả về lỗi");

            var duplicate = SettingsPageEditor.Clone(reloaded.Settings.Current);
            duplicate.Shortcuts.ToggleLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "F13" };
            duplicate.Shortcuts.HideLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "F13" };
            Check("Quy tắc phím tắt từ chối gán trùng cùng một tổ hợp", SettingsPageEditor.Validate(duplicate)?.Contains("cùng một", StringComparison.Ordinal) == true,
                "Trả về lỗi trùng lặp tổ hợp", SettingsPageEditor.Validate(duplicate) ?? "Không trả về lỗi");

            var bytesBeforeInvalidSave = File.ReadAllBytes(paths.SettingsFile);
            try
            {
                data.Settings.Current.Shortcuts.HotCornerDwellMs = 99;
                var invalidCurrent = SettingsPageEditor.Validate(data.Settings.Current);
                if (invalidCurrent is null) data.Settings.Save();
                Check("Cài đặt không hợp lệ không được lưu và nội dung trên đĩa giữ nguyên", invalidCurrent is not null
                    && bytesBeforeInvalidSave.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                    "Không thực hiện Save và byte tệp không đổi", invalidCurrent ?? "Cài đặt đã được chấp nhận");
            }
            finally
            {
                data.Settings.Current.Shortcuts.HotCornerDwellMs = 777;
            }
        }
        catch (Exception ex)
        {
            Check("Nghiệm thu thuần tệp và quy tắc không phát sinh ngoại lệ chưa được xử lý", false, "Không có ngoại lệ", ex.ToString());
        }

        Write(workspace.GetOutput("report"), reportPath, root, cases, cases.Count - failed, failed, failed == 0 && cases.Count > 0 ? 0 : 1);
        Console.WriteLine($"Phase 6 CLI: {cases.Count - failed} passed, {failed} failed. Report: {reportPath}");
        return cases.Count > 0 && failed == 0 ? 0 : 1;
    }

    private static void Write(Stream output, string reportPath, string root, List<object> cases, int passed, int failed, int exitCode)
    {
        AcceptanceEntryPoint.WriteText(output, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            artifact = typeof(Phase6Acceptance).Assembly.FullName,
            environment = Environment.OSVersion.ToString(),
            machine = Environment.MachineName,
            dataDirectory = root,
            driver = "isolated settings files and validation rules; no product windows or system input",
            passed,
            failed,
            exitCode,
            cases,
        }, JsonOptions));
    }
}
