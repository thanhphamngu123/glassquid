using System.Diagnostics;
using System.IO;
using System.Text;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Trình điều khiển kiểm thử desktop độc lập cho giai đoạn 4.
/// </summary>
/// <remarks>
/// <para>
/// Lý do tồn tại chỉ có một: cả hai bộ kiểm thử hiện có đều không làm được "đầu vào desktop thực tế". Kiểm thử đơn vị và nghiệm thu giai đoạn
/// điều khiển máy trạng thái trong tiến trình, còn <c>Glassquid.UiAcceptance</c> điều khiển các sự kiện định tuyến WPF;
/// cả hai đều không chạm tới được <strong>hộp thoại con gốc</strong> (trình chọn tệp) và <strong>kéo-thả xuyên cửa sổ</strong>.
/// Lượt trước dùng Computer Use để bù hai phần này, kết quả là nó áp tọa độ của hộp thoại gốc vào cửa sổ nhỏ của dock
/// (<c>point (469, -379) is outside window bounds</c>), còn kéo-thả xuyên cửa sổ thì trả về thẳng
/// <c>not target window</c>.
/// </para>
/// <para>
/// Trình điều khiển này đi theo hướng khác: dùng <b>UI Automation theo danh tính</b> để tìm cửa sổ và control,
/// dùng <b>SendInput</b> để tiêm sự kiện chuột và bàn phím thực, và dùng <strong>kết quả có thể quan sát được</strong>
/// (handle tiền cảnh, mục được ghi xuống đĩa, hộp thoại đóng lại, hình thái cửa sổ) để đánh giá đạt hay không.
/// </para>
/// <para>
/// Mã thoát: toàn bộ các ca kiểm thử được yêu cầu đều đạt mới là 0; có thất bại là 1; không thất bại nhưng có bị chặn hoặc chưa chạy là 2.
/// Bỏ qua, thiếu sản phẩm build, hay khóa màn hình đều sẽ không nhận được 0.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly object ReportGate = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 4 && args[0] == "--owned-target")
            return OwnedTarget.Run(args[1], int.Parse(args[2]), args[3]);

        // Lỗi tham số là "lần này không thể chạy", không phải lỗi sản phẩm: in trợ giúp và thoát với trạng thái bị chặn,
        // đừng để một cờ viết sai trông như bị sập chương trình.
        DriverOptions options;

        try
        {
            options = DriverOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(DriverOptions.HelpText);
            return 0;
        }

        // Chẩn đoán chỉ đọc không cần cổng kiểm soát: chúng không tiêm bất kỳ đầu vào nào và cũng không chạm vào cửa sổ nào.
        if (options.DumpWindows)
        {
            Console.WriteLine("Timestamp=" + DateTimeOffset.Now.ToString("O"));
            foreach (var file in new[] { typeof(Program).Assembly.Location, options.ProductPath, Path.ChangeExtension(options.ProductPath, ".dll") })
                if (File.Exists(file)) Console.WriteLine("SHA256 " + file + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            if (options.ProbeSession && DesktopGate.BlockedReason(true) is { } reason)
            {
                Console.WriteLine("BLOCKED: " + reason);
                DumpWindows();
                return 2;
            }
            return DumpWindows();
        }

        if (options.DumpProcessTree is { } processId)
        {
            return DumpProcessTree(processId);
        }

        var report = new Report(options.ReportPath);
        var environment = Report.Snapshot(File.Exists(options.ProductPath) ? options.ProductPath : null, DriverDescription);
        report.SetEnvironment(environment);
        for (var round = 1; round <= options.Repeat; round++)
        {
            if (options.Wants("picker")) report.Plan($"{PickerCase.CaseName} (Lượt {round}/{options.Repeat})", "R-07; D-08", "Khôi phục và khởi chạy được mục tiêu sau khi trình chọn gốc định vị lại");
            if (options.Wants("explorer")) report.Plan($"{ExplorerDropCase.CaseName} (Lượt {round}/{options.Repeat})", "D-01; D-23; R-07", "Kéo thả thực từ Explorer, ghim và lưu bền vững qua khởi động lại");
            foreach (var item in BatchCCases.Catalog.Where(c => options.Wants(c.Id)))
                report.Plan(BatchCCases.Name(item.Name, round, options.Repeat), item.Requirement, item.Name);
        }
        report.Note("Phạm vi yêu cầu: " + options.CaseFilter + "; all mới là danh sách desktop đầy đủ. Đạt từng ca đơn lẻ không đại diện cho đạt toàn bộ.");
        report.Write();

        using var watchdog = StartWatchdog(report, options.TimeoutMinutes);

        try
        {
            return RunGuarded(options, report);
        }
        catch (Exception ex)
        {
            lock (ReportGate)
            {
                report.Add(new CaseRecord(
                    "Tự chạy trình điều khiển",
                    "—",
                    DriverDescription,
                    "Trình điều khiển hoàn thành toàn bộ các ca và ghi ra kết luận",
                    "Trình điều khiển tự ném ra ngoại lệ ngoài dự kiến, đây không phải kết luận về sản phẩm:" + Environment.NewLine + ex,
                    CaseStatus.Failed,
                    [],
                    null));

                report.Write();
            }

            return 1;
        }
    }

    private const string DriverDescription = "Đầu vào desktop thực: UI Automation định vị control + SendInput tiêm chuột và bàn phím";

    private static int RunGuarded(DriverOptions options, Report report)
    {
        // ---- Cổng kiểm soát: không thỏa mãn thì không gửi dù chỉ một sự kiện đầu vào ----

        var blocked = DesktopGate.BlockedReason(options.OptedIn);

        if (blocked is not null)
        {
            lock (ReportGate)
            {
                report.Note("Không vượt qua cổng kiểm soát môi trường, chưa tiêm bất kỳ đầu vào nào: " + blocked);
                report.Add(new CaseRecord(
                    "Cổng kiểm soát môi trường trình điều khiển desktop",
                    "Kế hoạch: nghiệm thu desktop chỉ chạy trên máy ảo kiểm thử (desktop độc lập)",
                    "Không có (chưa qua cổng kiểm soát, chưa tiêm đầu vào)",
                    "Tồn tại một desktop tương tác chỉ dùng cho kiểm thử",
                    blocked,
                    CaseStatus.Blocked,
                    [],
                    null));

                report.Write();
            }

            return 2;
        }

        NativeInput.VerifyLayout();

        if (!File.Exists(options.ProductPath))
        {
            return Blocked(report, $"Không tìm thấy sản phẩm build: {options.ProductPath}. Hãy chạy build.ps1 trước, hoặc dùng --product để chỉ định đường dẫn.");
        }

        if (!File.Exists(options.FixturePath))
        {
            return Blocked(report, $"Không tìm thấy fixture trình chọn gốc: {options.FixturePath}. Hãy chạy build.ps1 trước, hoặc dùng --fixture để chỉ định đường dẫn.");
        }

        var workRoot = DriverContext.DefaultWorkRoot();
        Directory.CreateDirectory(workRoot);

        var context = new DriverContext(options.ProductPath, options.FixturePath, workRoot, report);

        report.Note($"Thư mục làm việc: {workRoot}");
        report.Note($"Sản phẩm: {options.ProductPath}");
        report.Note($"Fixture: {options.FixturePath}");
        report.Note($"Mỗi ca được yêu cầu chạy {options.Repeat} lượt; phạm vi {options.CaseFilter}.");

        for (var round = 1; round <= options.Repeat; round++)
        {
            report.Note($"===== Lượt {round}/{options.Repeat} =====");

            if (options.Wants("picker")) Add(report, () => PickerCase.Run(context, round, options.Repeat));
            if (options.Wants("explorer")) Add(report, () => ExplorerDropCase.Run(context, round, options.Repeat));
            foreach (var item in BatchCCases.Catalog.Where(c => options.Wants(c.Id)))
                Add(report, () => BatchCCases.Run(context, item.Id, round, options.Repeat));
        }

        lock (ReportGate)
        {
            report.Note($"Hoàn thành tác vụ, thư mục làm việc được giữ lại tại {workRoot} (bằng chứng và ảnh chụp màn hình đều ở bên trong).");
            report.Write();
            Console.WriteLine($"Báo cáo: {report.Path}");
            Console.WriteLine($"Đạt {report.Cases.Count(c => c.Status == CaseStatus.Passed)}, "
                + $"Thất bại {report.Cases.Count(c => c.Status == CaseStatus.Failed)}, "
                + $"Bị chặn {report.Cases.Count(c => c.Status == CaseStatus.Blocked)}.");

            return report.ExitCode();
        }
    }

    /// <summary>Chạy một ca kiểm thử. Ngoại lệ của chính trình điều khiển cũng phải để lại dấu vết, thay vì làm tiến trình sập thẳng.</summary>
    private static void Add(Report report, Func<CaseRecord> run)
    {
        CaseRecord record;

        try
        {
            record = run();
        }
        catch (Exception ex)
        {
            record = new CaseRecord(
                "Trình điều khiển ném ngoại lệ khi thực thi ca kiểm thử",
                "—",
                DriverDescription,
                "Ca kiểm thử chạy xong và đưa ra kết luận",
                "Ngoại lệ của chính trình điều khiển, đây không phải kết luận về sản phẩm:" + Environment.NewLine + ex,
                CaseStatus.Failed,
                [],
                null);
        }

        lock (ReportGate)
        {
            report.Add(record);
            report.Write();
        }
    }

    private static int Blocked(Report report, string reason)
    {
        lock (ReportGate)
        {
            report.Note(reason);
            report.Add(new CaseRecord(
                "Điều kiện tiên quyết của trình điều khiển desktop",
                "—",
                "Không có (chưa tiêm đầu vào)",
                "Sản phẩm và fixture đều đã được build",
                reason,
                CaseStatus.Blocked,
                [],
                null));

            report.Write();
        }

        return 2;
    }

    /// <summary>
    /// Giới hạn tổng thời gian. Một khi lệnh gọi UIA xuyên tiến trình bị treo, bản thân trình điều khiển không có cách nào tự khôi phục,
    /// vì vậy luồng giám sát sẽ ghi báo cáo và thoát với trạng thái "Bị chặn" (mã thoát 2), thay vì treo đứng yên.
    /// </summary>
    private static Timer StartWatchdog(Report report, int timeoutMinutes)
    {
        return new Timer(
            _ =>
            {
                lock (ReportGate)
                {
                    report.Note($"Đã hết tổng thời gian {timeoutMinutes} phút, ghi báo cáo hiện tại và kết thúc với mã thoát 2.");
                    report.Add(new CaseRecord(
                        "Giới hạn tổng thời gian trình điều khiển desktop",
                        "—",
                        DriverDescription,
                        $"Toàn bộ các ca chạy xong trong {timeoutMinutes} phút",
                        $"Đã quá {timeoutMinutes} phút vẫn chưa chạy xong, đã dừng. Các ca đã đăng ký trước nhưng chưa hoàn thành được giữ nguyên là NotRun.",
                        CaseStatus.Blocked,
                        [],
                        null));

                    report.Write();
                }

                Environment.Exit(2);
            },
            null,
            TimeSpan.FromMinutes(timeoutMinutes),
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Chẩn đoán chỉ đọc: liệt kê các cửa sổ cấp cao nhất hiện tại. Khi chạy lần đầu trên một máy, hãy dùng nó để đối chiếu tên lớp cửa sổ trước.</summary>
    private static int DumpWindows()
    {
        Console.WriteLine($"Desktop ảo {Win32.VirtualScreen().Describe()}; DPI hệ thống {Win32.SystemDpi()}; nhận thức DPI của tiến trình này {Win32.DpiAwareness()}");
        Console.WriteLine($"Desktop đầu vào {Win32.InputDesktopName() ?? "(đọc thất bại)"}; desktop của tiến trình này {Win32.CurrentThreadDesktopName() ?? "(đọc thất bại)"}");
        Console.WriteLine($"Trạm cửa sổ hiển thị: {Win32.WindowStationIsVisible()}");
        Console.WriteLine($"Phiên bảng điều khiển hoạt động {Win32.ActiveConsoleSessionId()}; phiên của tiến trình này {Process.GetCurrentProcess().SessionId}");
        Console.WriteLine();

        foreach (var facts in Win32.TopLevelWindows().Select(Win32.Describe).Where(f => f is not null))
        {
            Console.WriteLine("  " + facts!.Describe());
        }

        return 0;
    }

    /// <summary>Chẩn đoán chỉ đọc: in cây con UIA theo ID tiến trình. Dùng để đối chiếu danh tính control, đặc biệt là hộp thoại tệp.</summary>
    private static int DumpProcessTree(int processId)
    {
        if (!Uia.Until(() => Uia.FindTopWindow(processId, null, null) is not null, 5000))
        {
            Console.WriteLine($"pid {processId} không có cửa sổ cấp cao nhất.");
            return 2;
        }

        foreach (var window in FindAllWindows(processId))
        {
            Console.WriteLine(Uia.Describe(window));
            Console.WriteLine(Uia.Dump(window));
            Console.WriteLine();
        }

        return 0;
    }

    private static IEnumerable<System.Windows.Automation.AutomationElement> FindAllWindows(int processId)
    {
        var condition = new System.Windows.Automation.PropertyCondition(
            System.Windows.Automation.AutomationElement.ProcessIdProperty,
            processId);

        var found = System.Windows.Automation.AutomationElement.RootElement
            .FindAll(System.Windows.Automation.TreeScope.Children, condition);

        return found.Cast<System.Windows.Automation.AutomationElement>();
    }
}

/// <summary>Tất cả các cờ dòng lệnh.</summary>
internal sealed class DriverOptions
{
    internal string ReportPath { get; private init; } = Path.GetFullPath("phase4-desktop.json");

    internal string ProductPath { get; private init; } = string.Empty;

    internal string FixturePath { get; private init; } = string.Empty;

    internal int Repeat { get; private init; } = 1;

    internal string CaseFilter { get; private init; } = "all";
    internal bool Wants(string id) => CaseFilter == "all" || CaseFilter == id;

    internal int TimeoutMinutes { get; private init; } = 30;

    internal bool OptedIn { get; private init; }

    internal bool Help { get; private init; }

    internal bool DumpWindows { get; private init; }
    internal bool ProbeSession { get; private init; }

    internal int? DumpProcessTree { get; private init; }

    internal static DriverOptions Parse(string[] args)
    {
        var root = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string DefaultUnder(string relative) => root is null
            ? string.Empty
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
                .Replace("$(Configuration)", configuration);

        var report = Path.GetFullPath("phase4-desktop.json");
        var product = DefaultUnder("src/Glassquid.App/bin/$(Configuration)/net10.0-windows/Glassquid.exe");
        var fixture = DefaultUnder("tests/Glassquid.UiAcceptance/bin/$(Configuration)/net10.0-windows/Glassquid.UiAcceptance.exe");
        var repeat = 1;
        var caseFilter = "all";
        var timeout = 30;
        var optedIn = string.Equals(
            Environment.GetEnvironmentVariable(DesktopGate.OptInEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var help = false;
        var dumpWindows = false;
        var probeSession = false;
        int? dumpTree = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];

            switch (token)
            {
                case "--case":
                    caseFilter = Next(args, ref index, token);
                    if (caseFilter is not ("all" or "picker" or "explorer") && !BatchCCases.Catalog.Any(c => c.Id == caseFilter))
                        throw new ArgumentException("Unknown case: " + caseFilter);
                    break;
                case "--report":
                    report = Next(args, ref index, token);
                    break;

                case "--product":
                    product = Next(args, ref index, token);
                    break;

                case "--fixture":
                    fixture = Next(args, ref index, token);
                    break;

                case "--repeat":
                    repeat = int.TryParse(Next(args, ref index, token), out var parsedRepeat) && parsedRepeat > 0
                        ? parsedRepeat
                        : throw new ArgumentException("--repeat cần một số nguyên dương.");

                    break;

                case "--timeout":
                    timeout = int.TryParse(Next(args, ref index, token), out var parsedTimeout) && parsedTimeout > 0
                        ? parsedTimeout
                        : throw new ArgumentException("--timeout cần một số nguyên dương (phút).");

                    break;

                case DesktopGate.OptInFlag:
                    optedIn = true;
                    break;

                case "--dump-windows":
                    dumpWindows = true;
                    break;

                case "--probe-session":
                    dumpWindows = true;
                    probeSession = true;
                    break;

                case "--dump-uia":
                    dumpTree = int.TryParse(Next(args, ref index, token), out var parsedPid)
                        ? parsedPid
                        : throw new ArgumentException("--dump-uia cần ID tiến trình.");

                    break;

                case "--help":
                case "-h":
                    help = true;
                    break;

                default:
                    throw new ArgumentException($"Tham số không hợp lệ: {token}{Environment.NewLine}{HelpText}");
            }
        }

        return new DriverOptions
        {
            ReportPath = report,
            ProductPath = product,
            FixturePath = fixture,
            Repeat = repeat,
            CaseFilter = caseFilter,
            TimeoutMinutes = timeout,
            OptedIn = optedIn,
            Help = help,
            DumpWindows = dumpWindows,
            ProbeSession = probeSession,
            DumpProcessTree = dumpTree,
        };
    }

    internal const string HelpText =
        """
        Trình điều khiển nghiệm thu desktop Glassquid (Giai đoạn 4)

        Cách dùng: Glassquid.DesktopAcceptance.exe [tùy chọn]

          --i-am-on-isolated-desktop  Xác nhận "Tôi đang ở trên desktop tương tác chỉ dùng cho kiểm thử".
                                      Nếu không có xác nhận này (hoặc biến môi trường GLASSQUID_ISOLATED_DESKTOP=1)
                                      sẽ không gửi bất kỳ sự kiện đầu vào nào mà ghi nhận bị chặn và thoát ngay.
          --case <id>                 all (mặc định) hoặc picker/explorer/launch/reorder/focus/magnification/autohide/desktop/packaged/settings.
          --repeat <n>                Số lượt chạy liên tiếp cho mỗi ca được yêu cầu, mặc định là 1.
          --timeout <phút>            Giới hạn tổng thời gian, mặc định 30 phút. Hết giờ sẽ ghi báo cáo hiện tại và thoát với mã 2.
          --report <tệp>              Đường dẫn báo cáo, mặc định phase4-desktop.json (.log là bản văn bản cùng thư mục).
          --product <đường dẫn>       Sản phẩm build, mặc định suy ra theo đường dẫn tương đối của kho mã nguồn.
          --fixture <đường dẫn>       Fixture trình chọn gốc, mặc định suy ra theo đường dẫn tương đối của kho mã nguồn.
          --dump-windows              Chẩn đoán chỉ đọc: liệt kê các cửa sổ cấp cao nhất rồi thoát (không cần cổng kiểm soát).
          --probe-session             Chỉ đọc: liệt kê cửa sổ, thời gian và mã băm sản phẩm; nếu không phải desktop tương tác đang hoạt động thì thoát 2, không tiêm đầu vào.
          --dump-uia <pid>            Chẩn đoán chỉ đọc: in cây con UIA của tiến trình đó rồi thoát (không cần cổng kiểm soát).
          --help                      Hiển thị trợ giúp này.

        Mã thoát: 0 tất cả đạt; 1 có thất bại; 2 có bị chặn hoặc chưa chạy.
        """;

    private static string Next(string[] args, ref int index, string token)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Thiếu giá trị phía sau {token}.");
        }

        index++;
        return args[index];
    }

    /// <summary>Tìm ngược từ thư mục sản phẩm của chính mình lên thư mục gốc kho mã nguồn (nhận diện bằng Glassquid.slnx), không thấy thì trả về null.</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Glassquid.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
