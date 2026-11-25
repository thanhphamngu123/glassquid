namespace Glassquid.App.Startup;

/// <summary>Ghi đè giao diện dùng để tạo mẫu trực quan.</summary>
public enum AppearanceSample
{
    /// <summary>Không ghi đè, sử dụng theo tệp cài đặt.</summary>
    None = 0,

    /// <summary>Sáng + chất liệu hệ thống.</summary>
    Light = 1,

    /// <summary>Tối + chất liệu hệ thống.</summary>
    Dark = 2,

    /// <summary>Sáng + không yêu cầu chất liệu hệ thống, dùng nhánh hạ cấp tự vẽ tô màu.</summary>
    Degraded = 3,
}

/// <summary>
/// Các lệnh thư viện ứng dụng không giao diện được giới thiệu ở giai đoạn 3.
/// </summary>
/// <remarks>
/// Mục đích tồn tại tương tự như <c>--selftest</c>: cho phép các bài kiểm thu như
/// "nhập bốn loại mục, vẫn còn sau khi khởi động lại, đều mở được" <strong>thực sự chạy một lượt</strong>
/// mà không cần người thao tác giao diện, và ghi kết quả thành văn bản có thể dán vào báo cáo.
/// Giao diện chỉ được nối vào ở giai đoạn 4/5; trước lúc đó,
/// các lệnh này cũng là cách duy nhất để kiểm chứng đầu-cuối việc nhập và khởi chạy.
/// </remarks>
public enum AppServiceVerb
{
    None = 0,

    /// <summary>Nhập đường dẫn hoặc URL được chỉ định.</summary>
    Import = 1,

    /// <summary>Chỉ quét menu Start và liệt kê kết quả, không thay đổi thư viện ứng dụng.</summary>
    ScanStartMenu = 2,

    /// <summary>Quét menu Start và nhập toàn bộ các mục.</summary>
    ImportStartMenu = 3,

    /// <summary>Liệt kê nội dung thư viện ứng dụng.</summary>
    ListLibrary = 4,

    /// <summary>Kiểm tra lại các đích không còn hợp lệ và báo cáo.</summary>
    CheckLibrary = 5,

    /// <summary>Định vị lại một bản ghi sang đường dẫn mới.</summary>
    Relocate = 6,

    /// <summary>Mở một bản ghi.</summary>
    Launch = 7,

    /// <summary>Thử kích hoạt cửa sổ đang có trước, nếu chưa có mới khởi chạy.</summary>
    Activate = 8,

    /// <summary>Dùng WIC giải mã từng biểu tượng trong bộ nhớ đệm để xác nhận tệp PNG đã ghi thực sự đọc được.</summary>
    VerifyIcons = 9,

    /// <summary>Xóa sạch bộ nhớ đệm biểu tượng.</summary>
    ClearIconCache = 10,

    /// <summary>Tạo các mẫu đại diện gồm tên tiếng Trung, khoảng trắng, đường dẫn hỏng, lối tắt có tham số, v.v.</summary>
    MakeSamples = 11,

    /// <summary>Tạo số lượng mục tổng hợp theo chỉ định để kiểm thu thư viện lớn.</summary>
    SeedLibrary = 12,

    /// <summary>Ghép các biểu tượng trong bộ nhớ đệm thành một ảnh bảng liên hệ để kiểm tra thủ công rằng biểu tượng đã được lấy đúng.</summary>
    IconContactSheet = 13,

    /// <summary>Chạy toàn bộ các kiểm tra nghiệm thu của giai đoạn 3.</summary>
    Phase3Acceptance = 14,

    /// <summary>Đầu dò A: đo thực tế ba dạng danh tính ứng dụng (tường minh trên .lnk / tường minh trên cửa sổ / danh tính gói tiến trình).</summary>
    ProbeAumid = 15,

    /// <summary>Đầu dò B: đo thực tế xem nguồn sự kiện WinEvent có bao phủ được việc cửa sổ xuất hiện, biến mất và thay đổi hiển thị hay không.</summary>
    ProbeRunEvents = 16,

    /// <summary>Chạy toàn bộ các kiểm tra nghiệm thu của giai đoạn 4 (tích lũy theo từng đợt).</summary>
    Phase4Acceptance = 17,

    /// <summary>Đầu dò C: đo thực tế quyền sở hữu đăng ký của đường ống kéo-thả (WPF / OLE).</summary>
    ProbeDragDrop = 18,

    /// <summary>Đầu dò D: đo thực tế ba hướng triển khai ứng viên cho "Hiển thị màn hình nền".</summary>
    ProbeShowDesktop = 19,

    Phase5Acceptance = 20,

    Phase6Acceptance = 21,
}

/// <summary>Lần khởi chạy này được yêu cầu vận hành theo phương thức nào.</summary>
public sealed class CommandLineOptions
{
    /// <summary>
    /// Chạy tự kiểm tra môi trường không giao diện, in kết quả rồi thoát.
    /// </summary>
    /// <remarks>
    /// Mục đích tồn tại là để sản phẩm biên dịch có thể được kiểm chứng mà không cần người canh cửa sổ:
    /// lệnh sẽ báo cáo phiên bản hệ thống, các khả năng kính mờ nào khả dụng, dữ liệu được lưu ở đâu,
    /// dữ liệu có nạp sạch sẽ hay không và phiên bản đang chạy có tự phát hiện được chính mình không.
    /// Đây là hình thức tự động hóa cho bước kiểm tra "khởi chạy được và thoát sạch" của giai đoạn 1.
    /// </remarks>
    public bool SelfTest { get; private init; }

    /// <summary>
    /// Xuất chẩn đoán hiển thị và không gian tọa độ rồi thoát.
    /// </summary>
    /// <remarks>
    /// Tách riêng với <see cref="SelfTest"/> vì hai phần quan tâm đến những thứ không thay đổi cùng lúc:
    /// tự kiểm tra theo dõi độ tin cậy dữ liệu, còn chẩn đoán theo dõi tọa độ và màn hình. Sai tọa độ
    /// sẽ không báo lỗi mà chỉ khiến cửa sổ hiện sai vị trí, vì vậy việc này xứng đáng có một lệnh riêng,
    /// và đầu ra cần dán thẳng được vào báo cáo.
    /// </remarks>
    public bool Diagnose { get; private init; }

    /// <summary>Hiển thị bảng khởi chạy khi khởi động.</summary>
    public bool ShowLauncher { get; private init; }

    /// <summary>Hiển thị trung tâm cài đặt khi khởi động.</summary>
    public bool ShowSettings { get; private init; }

    /// <summary>Khởi chạy khi người dùng hiện tại đăng nhập; không được mở bảng khởi chạy hoặc gọi một phiên bản khác.</summary>
    public bool Autostart { get; private init; }

    /// <summary>
    /// Ép khởi động với một giao diện nhất định để tạo mẫu trực quan có thể tái lập.
    /// </summary>
    /// <remarks>
    /// Sản phẩm bàn giao của giai đoạn 2 là "mẫu chế độ sáng, tối và hạ cấp". Để chụp được các mẫu
    /// có thể đối chiếu, phải cố định được giao diện thay vì phụ thuộc vào chủ đề hệ thống tại thời điểm đó.
    /// Tùy chọn này chỉ ghi đè cho lần chạy hiện tại, không ghi ngược lại tệp cài đặt.
    /// </remarks>
    public AppearanceSample Sample { get; private init; } = AppearanceSample.None;

    /// <summary>Được yêu cầu xuất văn bản trợ giúp.</summary>
    public bool Help { get; private init; }

    /// <summary>
    /// Yêu cầu phiên bản đang chạy thoát.
    /// </summary>
    /// <remarks>
    /// Mục đích tồn tại là để có thể dừng sản phẩm từ kịch bản hoặc dòng lệnh mà không cần bấm menu khay hệ thống
    /// hay buộc kết thúc tiến trình; quy trình dựng và kiểm thử dựa vào chính tùy chọn này để kiểm tra
    /// xem ứng dụng có thoát sạch hay không.
    /// </remarks>
    public bool Exit { get; private init; }

    public string? AcceptanceDataRoot { get; private init; }

    public string? AcceptanceReport { get; private init; }

    /// <summary>Tất cả các tham số không nhận diện được. Lý do báo lại là để không âm thầm bỏ qua lỗi chính tả.</summary>
    public IReadOnlyList<string> Unknown { get; private init; } = [];

    /// <summary>Lệnh thư viện ứng dụng cần thực thi; là <see cref="AppServiceVerb.None"/> khi không phải kiểu khởi chạy này.</summary>
    public AppServiceVerb Verb { get; private init; } = AppServiceVerb.None;

    /// <summary>Các tham số bổ sung cho lệnh thư viện ứng dụng.</summary>
    public IReadOnlyList<string> VerbArguments { get; private init; } = [];

    /// <summary>Lần khởi chạy này có phải là một lệnh thư viện ứng dụng hay không.</summary>
    public bool IsAppServiceCommand => Verb != AppServiceVerb.None;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var selfTest = false;
        var diagnose = false;
        var showLauncher = false;
        var showSettings = false;
        var autostart = false;
        var help = false;
        var exit = false;
        string? acceptanceDataRoot = null;
        string? acceptanceReport = null;
        var unknown = new List<string>();
        var sample = AppearanceSample.None;
        var verb = AppServiceVerb.None;
        var verbArguments = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var raw = args[index];
            var token = raw.Trim().ToLowerInvariant();

            // Lệnh thư viện ứng dụng nhận giá trị: các tham số phía sau cho đến công tắc tiếp theo đều thuộc về nó.
            if (TryParseVerb(token, out var parsedVerb))
            {
                if (verb != AppServiceVerb.None)
                {
                    // Mỗi lần chỉ làm một việc. Đưa ra hai lệnh cùng lúc là mơ hồ,
                    // thay vì đoán bừa một lệnh thì nên báo lỗi ra.
                    unknown.Add(raw);
                    continue;
                }

                verb = parsedVerb;
                continue;
            }

            if (verb != AppServiceVerb.None && !token.StartsWith("--", StringComparison.Ordinal))
            {
                verbArguments.Add(raw);
                continue;
            }

            switch (token)
            {
                case "--acceptance-data-root":
                    if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    { unknown.Add(raw); acceptanceDataRoot = string.Empty; }
                    else if (acceptanceDataRoot is not null) unknown.Add(raw);
                    else acceptanceDataRoot = args[++index];
                    break;

                case "--acceptance-report":
                    if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    { unknown.Add(raw); acceptanceReport = string.Empty; }
                    else if (acceptanceReport is not null) unknown.Add(raw);
                    else acceptanceReport = args[++index];
                    break;
                case "--selftest":
                case "--self-test":
                    selfTest = true;
                    break;

                case "--diagnose":
                    diagnose = true;
                    break;

                case "--sample-light":
                    sample = AppearanceSample.Light;
                    break;

                case "--sample-dark":
                    sample = AppearanceSample.Dark;
                    break;

                case "--sample-degraded":
                    sample = AppearanceSample.Degraded;
                    break;

                case "--show-launcher":
                    showLauncher = true;
                    break;

                case "--show-settings":
                    showSettings = true;
                    break;

                case "--autostart":
                    autostart = true;
                    break;

                case "--exit":
                    exit = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;

                default:
                    unknown.Add(raw);
                    break;
            }
        }

        var phaseAcceptance = verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance
            or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance;
        if (autostart && (selfTest || diagnose || showLauncher || showSettings || exit || help
            || sample != AppearanceSample.None || verb != AppServiceVerb.None
            || acceptanceDataRoot is not null || acceptanceReport is not null || unknown.Count > 0))
        {
            unknown.Add("--autostart phải được dùng đơn lẻ, không thể kết hợp với các tham số khác");
        }

        if ((selfTest || phaseAcceptance || acceptanceDataRoot is not null || acceptanceReport is not null)
            && (new[] { selfTest, diagnose, exit, showLauncher, showSettings, help, sample != AppearanceSample.None, verb != AppServiceVerb.None }).Count(active => active) > 1)
        {
            unknown.Add("Tự kiểm tra hoặc nghiệm thu không thể kết hợp với các chế độ khởi chạy khác");
        }

        return new CommandLineOptions
        {
            SelfTest = selfTest,
            Diagnose = diagnose,
            ShowLauncher = showLauncher,
            ShowSettings = showSettings,
            Autostart = autostart,
            Sample = sample,
            Exit = exit,
            AcceptanceDataRoot = acceptanceDataRoot,
            AcceptanceReport = acceptanceReport,
            Help = help,
            Unknown = unknown,
            Verb = verb,
            VerbArguments = verbArguments,
        };
    }

    private static bool TryParseVerb(string token, out AppServiceVerb verb)
    {
        switch (token)
        {
            case "--import": verb = AppServiceVerb.Import; return true;
            case "--scan-start-menu": verb = AppServiceVerb.ScanStartMenu; return true;
            case "--import-start-menu": verb = AppServiceVerb.ImportStartMenu; return true;
            case "--list-library": verb = AppServiceVerb.ListLibrary; return true;
            case "--check-library": verb = AppServiceVerb.CheckLibrary; return true;
            case "--relocate": verb = AppServiceVerb.Relocate; return true;
            case "--launch": verb = AppServiceVerb.Launch; return true;
            case "--activate": verb = AppServiceVerb.Activate; return true;
            case "--verify-icons": verb = AppServiceVerb.VerifyIcons; return true;
            case "--clear-icon-cache": verb = AppServiceVerb.ClearIconCache; return true;
            case "--make-samples": verb = AppServiceVerb.MakeSamples; return true;
            case "--seed-library": verb = AppServiceVerb.SeedLibrary; return true;
            case "--icon-contact-sheet": verb = AppServiceVerb.IconContactSheet; return true;
            case "--phase3-acceptance": verb = AppServiceVerb.Phase3Acceptance; return true;
            case "--probe-aumid": verb = AppServiceVerb.ProbeAumid; return true;
            case "--probe-runevents": verb = AppServiceVerb.ProbeRunEvents; return true;
            case "--phase4-acceptance": verb = AppServiceVerb.Phase4Acceptance; return true;
            case "--phase5-acceptance": verb = AppServiceVerb.Phase5Acceptance; return true;
            case "--phase6-acceptance": verb = AppServiceVerb.Phase6Acceptance; return true;
            case "--probe-dragdrop": verb = AppServiceVerb.ProbeDragDrop; return true;
            case "--probe-showdesktop": verb = AppServiceVerb.ProbeShowDesktop; return true;

            default:
                verb = AppServiceVerb.None;
                return false;
        }
    }

    public static string HelpText =>
        """
        Glassquid (tên tạm thời)

        Cách dùng: Glassquid.exe [tùy chọn]

          --selftest         Chạy tự kiểm tra môi trường và thoát (không hiển thị giao diện)
          --selftest --acceptance-data-root <thư mục mới> --acceptance-report <tệp báo cáo mới>
                             Chạy tự kiểm tra trong thư mục dữ liệu cô lập (dùng cho kịch bản dựng và phát hành)
          --diagnose         Xuất chẩn đoán hiển thị và không gian tọa độ rồi thoát
          --diagnose --acceptance-data-root <thư mục mới> --acceptance-report <tệp báo cáo mới>
                             Cô lập dữ liệu và báo cáo chẩn đoán (vẫn tạo cửa sổ chẩn đoán, cần desktop thử nghiệm khả dụng)
          --show-launcher    Hiển thị bảng khởi chạy (nếu đang chạy sẽ thông báo cho phiên bản hiện có)
          --show-settings    Hiển thị trung tâm cài đặt (nếu đang chạy sẽ thông báo cho phiên bản hiện có)
          --autostart        Khởi chạy khi đăng nhập (nếu đang chạy sẽ thoát ngay, không mở giao diện)
          --sample-light     Khởi động với giao diện sáng (dùng để tạo mẫu, không ghi ngược cài đặt)
          --sample-dark      Khởi động với giao diện tối
          --sample-degraded  Khởi động với giao diện hạ cấp không yêu cầu chất liệu hệ thống
          --exit             Thông báo cho phiên bản đang chạy thoát
          --help             Hiển thị trợ giúp này

          --phase3-acceptance <đầu ra bảng liên hệ> <đầu ra báo cáo> <thư mục dữ liệu mới>  Nghiệm thu giai đoạn 3
          --phase4-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>                          Nghiệm thu giai đoạn 4
          --phase5-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>                          Nghiệm thu lệnh giai đoạn 5
          --phase6-acceptance <đầu ra báo cáo> <thư mục dữ liệu mới>                          Nghiệm thu tương tác và cài đặt giai đoạn 6

        Lệnh thư viện ứng dụng (giai đoạn 3, không giao diện):

          --import <đường dẫn hoặc URL>...    Nhập mục; có thể nhập nhiều mục
          --scan-start-menu                   Quét menu Start và liệt kê, không thay đổi thư viện
          --import-start-menu                 Quét menu Start và nhập toàn bộ
          --list-library                      Liệt kê nội dung thư viện ứng dụng
          --check-library                     Kiểm tra lại các đích không còn hợp lệ và báo cáo
          --relocate <id mục> <đường dẫn mới> Định vị lại một bản ghi sang vị trí mới
          --launch <id mục>                   Mở một bản ghi
          --activate <id mục>                 Ưu tiên kích hoạt cửa sổ sẵn có, chưa có mới khởi chạy
          --verify-icons                      Dùng bộ giải mã ảnh hệ thống đọc qua toàn bộ biểu tượng đệm
          --icon-contact-sheet [tệp]          Ghép các biểu tượng đệm thành một ảnh bảng liên hệ
          --clear-icon-cache                  Xóa sạch bộ nhớ đệm biểu tượng
          --make-samples [thư mục]            Tạo các mẫu đại diện gồm tên tiếng Trung, khoảng trắng, đường dẫn hỏng, v.v.
          --seed-library <số lượng>           Tạo số lượng mục tổng hợp theo chỉ định

        Khi khởi chạy không kèm tùy chọn, thanh dock sẽ xuất hiện; bảng khởi chạy và trung tâm cài đặt có thể mở từ menu khay hệ thống.
        """;
}
