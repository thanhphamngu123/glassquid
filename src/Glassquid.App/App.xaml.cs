using System.Windows;
using Glassquid.App.Startup;
using Glassquid.Core.Storage;
using Glassquid.UI.Theming;

namespace Glassquid.App;

/// <summary>
/// Điểm vào ứng dụng.
/// </summary>
/// <remarks>
/// Cố ý giữ lớp này thật mỏng. Nó chỉ quyết định chế độ chạy rồi bàn giao quyền điều khiển cho
/// <see cref="AppHost"/>; toàn bộ hành vi sản phẩm đều nằm ở đó hoặc các tầng bên dưới.
/// </remarks>
public partial class App : Application, IDisposable
{
    private AppHost? _host;
    private CrashDiagnostics? _crashDiagnostics;
    private Glassquid.Platform.Storage.AcceptanceWorkspace? _pendingDiagnosticWorkspace;
    private bool _shaderFailureSubscribed;

    /// <summary>
    /// Giải phóng host và mọi tài nguyên mà nó sở hữu.
    /// </summary>
    /// <remarks>
    /// Triển khai phương thức này thay vì chỉ phó mặc cho <see cref="OnExit"/> để quyền sở hữu các trường có thể giải phóng
    /// luôn rõ ràng với cả người đọc lẫn trình phân tích tĩnh.
    /// </remarks>
    public void Dispose()
    {
        if (_shaderFailureSubscribed)
        {
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= OnBackdropShaderFailure;
            _shaderFailureSubscribed = false;
        }
        _host?.Dispose();
        _host = null;
        _pendingDiagnosticWorkspace?.Dispose();
        _pendingDiagnosticWorkspace = null;
        _crashDiagnostics?.Dispose();
        _crashDiagnostics = null;
        GC.SuppressFinalize(this);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);
        if (!Glassquid.Platform.WindowsCapabilities.IsSupported)
        {
            MessageBox.Show("Phiên bản này yêu cầu Windows 11 (Build 22000 trở lên).", "Glassquid", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2); return;
        }
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += OnBackdropShaderFailure;
        _shaderFailureSubscribed = true;

        if (options.Autostart && options.Unknown.Count > 0)
        {
            Console.Error.WriteLine("--autostart không thể dùng cùng tham số khởi chạy khác; đã từ chối thực thi.");
            Shutdown(2);
            return;
        }

        if (options.SelfTest && (options.Verb != AppServiceVerb.None || options.Diagnose || options.Exit
            || options.ShowLauncher || options.ShowSettings || options.Sample != AppearanceSample.None || options.Help))
        {
            Console.Error.WriteLine("--selftest không thể dùng cùng tham số khởi chạy hoặc chế độ dịch vụ ứng dụng khác.");
            Shutdown(2);
            return;
        }

        if (options.Unknown.Count > 0 && (options.SelfTest || options.Verb is AppServiceVerb.Phase3Acceptance
            or AppServiceVerb.Phase4Acceptance or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance))
        {
            Console.Error.WriteLine("Tham số tự kiểm tra hoặc nghiệm thu bị trùng lặp hay không hợp lệ; đã từ chối chạy quy trình nghiệm thu.");
            Shutdown(2);
            return;
        }

        var hasIsolationArguments = options.AcceptanceDataRoot is not null || options.AcceptanceReport is not null;
        if (hasIsolationArguments && !AcceptanceEntryPoint.IsValidIsolatedRun(options))
        {
            Console.Error.WriteLine("Tham số cách ly nghiệm thu chỉ được dùng riêng với --selftest hoặc --diagnose cùng các tham số thư mục dữ liệu và báo cáo đầy đủ.");
            Shutdown(2);
            return;
        }

        if (options.Help)
        {
            ReportWriter.Write(CommandLineOptions.HelpText, null);
            Shutdown(0);
            return;
        }

        var paths = AppPaths.ForCurrentUser();

        if (hasIsolationArguments)
        {
            if (options.Diagnose)
            {
                _pendingDiagnosticWorkspace = AcceptanceEntryPoint.Prepare(paths, options.AcceptanceDataRoot!,
                    [new("report", options.AcceptanceReport!)]);
                if (_pendingDiagnosticWorkspace is null) { Shutdown(2); return; }
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    var workspace = _pendingDiagnosticWorkspace;
                    _pendingDiagnosticWorkspace = null;
                    if (workspace is null) return;
                    var exitCode = AcceptanceEntryPoint.Run(workspace, prepared =>
                    {
                        ThemeService.Initialize(Dispatcher);
                        return DiagnosticRunner.Run(new AppPaths(prepared.DataRoot), prepared.GetOutput("report"));
                    });
                    Shutdown(exitCode);
                });
                return;
            }

            var isolatedExitCode = AcceptanceEntryPoint.Run(
                paths,
                options.AcceptanceDataRoot!,
                [new Glassquid.Platform.Storage.AcceptanceOutput("report", options.AcceptanceReport!)],
                workspace =>
                {
                    ThemeService.Initialize(Dispatcher);
                    return SelfTest.Run(new AppPaths(workspace.DataRoot), workspace.GetOutput("report"));
                });
            Shutdown(isolatedExitCode);
            return;
        }

        if (options.IsAppServiceCommand)
        {
            // Lệnh thư viện ứng dụng hoàn toàn không cần giao diện: nhập, khởi chạy, rà soát đều chỉ chạm tới dữ liệu và hệ thống.
            // Do đó ở đây chạy đồng bộ xong rồi thoát ngay, không tạo bất kỳ cửa sổ nào và cũng không khởi tạo chủ đề.
            var exitCode = AppServiceRunner.Run(paths, options);
            Shutdown(exitCode);
            return;
        }

        if (options.SelfTest)
        {
            // Tự kiểm tra sẽ báo cáo chủ đề và bảng màu đã phân giải, nên dù không hiển thị bất kỳ cửa sổ nào,
            // dịch vụ chủ đề vẫn phải tồn tại trước.
            ThemeService.Initialize(Dispatcher);

            var exitCode = SelfTest.Run(paths);
            Shutdown(exitCode);
            return;
        }

        if (options.Diagnose)
        {
            // Chẩn đoán cần đọc giá trị DIP của WPF và đặt cửa sổ thật lên màn hình rồi đọc lại vị trí,
            // nên không thể chạy đồng bộ tại đây: lúc này cửa sổ chưa được hệ thống định vị nên đọc lại sẽ ra giá trị cũ.
            // Đưa vào thực thi khi vòng lặp thông điệp rảnh, chạy xong rồi đóng lại.
            ThemeService.Initialize(Dispatcher);

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () =>
                {
                    var exitCode = DiagnosticRunner.Run(paths);
                    Shutdown(exitCode);
                });

            return;
        }

        if (options.Unknown.Count > 0)
        {
            // Báo cáo thay vì bỏ qua, tránh việc cờ nhập sai trông như đã có hiệu lực.
            ReportWriter.Write(
                $"Bỏ qua tham số không nhận diện được: {string.Join(' ', options.Unknown)}{Environment.NewLine}{CommandLineOptions.HelpText}",
                null);
        }

        _crashDiagnostics = new CrashDiagnostics(this, paths.LogDirectory);
        _host = new AppHost(this, options);

        switch (_host.Start())
        {
            case StartupOutcome.Started:
                break;

            case StartupOutcome.NothingToDo:
                // Lần khởi chạy này chỉ yêu cầu “thoát”, trong khi lúc đó không có phiên bản nào đang chạy.
                // Không có phiên bản khác để thông báo, lại càng không nên vì thế mà dựng giao diện lên.
                ReportWriter.Write("Không có Glassquid nào đang chạy; không cần thoát.", null);
                Shutdown(0);
                Environment.Exit(0);
                break;

            case StartupOutcome.LegacyInstanceBlocked:
            case StartupOutcome.UserDataLeaseUnavailable:
            case StartupOutcome.PrimaryDisplayUnavailable:
                Shutdown(2);
                Environment.Exit(2);
                break;

            default:
                // Một phiên bản khác đã nắm giữ UI và đã được thông báo cần làm gì.
                // Dọn dẹp dứt điểm: Thoát ngay lập tức để không tạo tiến trình rác chạy ngầm
                Shutdown(0);
                Environment.Exit(0);
                break;
        }
    }

    private static void OnBackdropShaderFailure(object? sender, EventArgs e) => Glassquid.UI.BackdropRecovery.UseBasicRendering();

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
        Environment.Exit(e.ApplicationExitCode);
    }
}
