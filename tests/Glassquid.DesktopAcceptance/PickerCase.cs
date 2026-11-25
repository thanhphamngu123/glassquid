using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Kiểm chứng tối thiểu 1: Chọn một đường dẫn có chứa ký tự tiếng Trung và khoảng trắng trong trình chọn tệp <strong>gốc</strong>,
/// đối chiếu chuỗi đích mà sản phẩm nhận được khớp từng ký tự với đường dẫn đó.
/// </summary>
/// <remarks>
/// Điểm nghẽn ở vòng trước không nằm ở sản phẩm, mà ở trình điều khiển: Computer Use chỉ liệt kê được cửa sổ gốc không có thanh tác vụ,
/// không lấy được hộp thoại con gốc kia, nên đã áp tọa độ của hộp thoại vào hình chữ nhật nhỏ của thanh Dock,
/// dẫn đến nhấp ra ngoài cửa sổ (<c>point (469, -379) is outside window bounds</c>).
/// Cách làm ở đây hoàn toàn khác: <b>tìm cửa sổ theo danh tính</b> — tiến trình + tên lớp cửa sổ <c>#32770</c>,
/// rồi tìm ô nhập liệu và nút theo loại điều khiển cùng mã định danh tự động hóa; tọa độ chỉ lấy từ hình chữ nhật thực tế của điều khiển.
///
/// Bộ thử nghiệm (fixture) là <c>Glassquid.UiAcceptance --picker</c> có sẵn trong kho mã nguồn: nó điều khiển
/// <c>RelocateFromDialog</c> của sản phẩm (<c>IFileOpenDialog</c> thực tế) và tự khẳng định trong tiến trình của mình rằng
/// "mục trỏ tới đích thay thế, biểu tượng được khôi phục, nhấp vào có thể khởi chạy". Trình điều khiển này làm hai việc mà nó không làm được:
/// dùng chuột và bàn phím thật để hoàn tất việc chọn, đồng thời <strong>mở một góc nhìn độc lập</strong> đọc tệp
/// <c>apps.json</c> đã lưu xuống đĩa để đối chiếu chuỗi mà sản phẩm nhận được.
/// </remarks>
internal static class PickerCase
{
    internal const string CaseName = "Mục hoạt động lại sau khi chọn đường dẫn có ký tự tiếng Trung và khoảng trắng bằng trình chọn tệp gốc";

    private const string Requirement = "R-07 Có thể định vị lại khi đường dẫn mất hiệu lực; D-08 Nhấp để khởi chạy";

    private const string Driver = "Thao tác desktop thật: UIA định vị hộp thoại gốc + SendInput nhập liệu và nhấp chuột";

    private const string Expected =
        "Sau khi chọn đường dẫn có ký tự tiếng Trung và khoảng trắng trong trình chọn tệp gốc, chuỗi đích được sản phẩm ghi nhận khớp từng ký tự với đường dẫn đó, "
        + "mục không còn mất hiệu lực, biểu tượng được khôi phục và nhấp vào có thể khởi chạy đích.";

    internal static CaseRecord Run(DriverContext context, int round, int rounds)
    {
        var name = $"{CaseName} (Vòng {round}/{rounds})";
        var evidence = new List<string>();
        string? screenshot = null;

        CaseRecord Conclude(CaseStatus status, string actual) =>
            new(name, Requirement, Driver, Expected, actual, status, [.. evidence], screenshot);

        var work = context.NewWorkDirectory($"trinh-chon-goc-vong-{round}");
        var fixtureReport = Path.Combine(work, "ui-acceptance.json");

        using var fixture = ProcessLease.Launch(
            context.FixturePath,
            ["--setup-only", "--picker", "--report", fixtureReport],
            workingDirectory: work,
            captureOutput: true);

        evidence.Add($"Lệnh bộ thử nghiệm: {fixture.CommandLine}");
        context.Report.Note($"Kiểm chứng tối thiểu 1 (Vòng {round}): Bộ thử nghiệm đã khởi động pid {fixture.ProcessId}");

        // ---- 1. Đích thay thế của bộ thử nghiệm: tự nó in ra, trình điều khiển không đoán ----

        var replacement = fixture.WaitForOutputLine("PICKER_REPLACEMENT=", 90000);
        if (replacement is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                "Bộ thử nghiệm không in ra PICKER_REPLACEMENT=… trong 90 giây, cho thấy nó chưa đi tới bước \"mở trình chọn tệp\". "
                + "Đây là lỗi bộ thử nghiệm chưa chạy được, không phải vấn đề của trình chọn tệp. Phần cuối đầu ra của bộ thử nghiệm:"
                + Environment.NewLine + Tail(fixture.Output()));
        }

        evidence.Add($"Đường dẫn cần chọn (gồm ký tự tiếng Trung và khoảng trắng): {replacement}");

        if (!File.Exists(replacement))
        {
            return Conclude(CaseStatus.Failed, $"Đích thay thế do bộ thử nghiệm khai báo không tồn tại: {replacement}");
        }

        // ---- 2. Thuộc tính của cửa sổ Dock bản phát hành: không được thay đổi trong lúc kiểm thử ----

        var dockElement = Uia.WaitForTopWindow(fixture.ProcessId, null, "Dock", 30000);
        if (dockElement is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Không chờ được cửa sổ Dock của bộ thử nghiệm (pid {fixture.ProcessId}). Các cửa sổ cấp cao nhất của tiến trình này mà tiến trình hiện tại nhìn thấy:"
                + Environment.NewLine + DescribeWindows(fixture.ProcessId));
        }

        var dockHandle = Uia.Handle(dockElement);
        var dockFacts = Win32.Describe(dockHandle);
        evidence.Add($"Cửa sổ Dock của bộ thử nghiệm: {dockFacts?.Describe() ?? "(không đọc được thuộc tính cửa sổ)"}");

        if (dockFacts is null)
        {
            return Conclude(CaseStatus.Blocked, "Không đọc được thuộc tính cửa sổ Dock của bộ thử nghiệm, không thể đối chiếu xem nó có bị bộ thử nghiệm sửa đổi hay không.");
        }

        if (!dockFacts.HasNoActivate || dockFacts.IsOnTaskbar)
        {
            return Conclude(
                CaseStatus.Failed,
                "Cửa sổ Dock của bộ thử nghiệm không đúng hình thái bản phát hành (yêu cầu có WS_EX_NOACTIVATE và không xuất hiện trên thanh tác vụ): "
                + dockFacts.Describe());
        }

        // ---- 3. Trình chọn tệp gốc: tìm theo tiến trình và lớp cửa sổ, không áp đặt tọa độ ----

        var dialog = Uia.WaitForDialog(fixture.ProcessId, 45000);
        if (dialog is null)
        {
            screenshot ??= TryShot(work, round, "khong-cho-duoc-trinh-chon");
            return Conclude(
                CaseStatus.Failed,
                "Quy trình định vị lại của sản phẩm không tạo trình chọn tệp gốc (lớp cửa sổ #32770) trong 45 giây. "
                + "Các cửa sổ cấp cao nhất hiện tại của tiến trình này:" + Environment.NewLine + DescribeWindows(fixture.ProcessId));
        }

        var treeFile = Path.Combine(work, $"cay-dieu-khien-trinh-chon-vong-{round}.txt");
        File.WriteAllText(treeFile, Uia.Dump(dialog));
        evidence.Add($"Cây điều khiển của trình chọn (dùng để đối chiếu danh tính điều khiển khi chạy lần đầu trên máy đích): {treeFile}");

        screenshot = TryShot(work, round, "trinh-chon-da-hien");

        // Khi tới lượt các điều khiển của hộp thoại cũng phải chờ: trong khoảng ngắn khi cửa sổ đã hiển thị nhưng bên trong chưa bố cục xong,
        // hình chữ nhật của ô nhập liệu sẽ rỗng; nếu coi "hình chữ nhật rỗng = không tìm thấy" sẽ dẫn đến kết luận sai.
        var editReady = Uia.Until(() => PickFileNameEdit(dialog) is not null, 15000, 300);
        var edit = editReady ? PickFileNameEdit(dialog) : null;

        if (edit is null)
        {
            return Conclude(
                CaseStatus.Failed,
                "Không tìm thấy ô nhập tên tệp khả dụng trong trình chọn. Cây điều khiển đã được lưu tại: " + treeFile);
        }

        evidence.Add($"Ô nhập tên tệp: {Uia.Describe(edit)}");

        var editClick = Uia.Click(edit, "Ô nhập tên tệp");
        evidence.Add(editClick.Detail);

        var usedKeyboard = false;

        if (!editClick.Clicked)
        {
            // Các cửa sổ bật lên như biểu ngữ thông báo sẽ không tự biến mất, nên nhấp chuột trọng lực sẽ không bao giờ trúng. Chuyển sang tiêu điểm bàn phím:
            // SetFocus của UIA không phụ thuộc vào việc có cửa sổ nào đè tại điểm đó hay không, và thao tác nhập sau đó vẫn là phím bấm thật.
            if (!Uia.TryFocus(edit))
            {
                return Conclude(CaseStatus.Blocked, "Không thể nhấp trúng ô nhập tên tệp, và tiêu điểm bàn phím cũng không đặt được vào đó: " + editClick.Detail);
            }

            evidence.Add("Thao tác nhấp bị cửa sổ bật lên che khuất, đã chuyển sang tiêu điểm bàn phím + phím bấm thật (HasKeyboardFocus là true sau SetFocus).");
            usedKeyboard = true;
        }

        NativeInput.SelectAll();
        Thread.Sleep(120);
        NativeInput.TypeText(replacement);
        Thread.Sleep(250);

        var typed = Uia.ValueOf(edit);
        evidence.Add($"Văn bản trong ô nhập sau khi gõ: {Quote(typed)}");

        if (typed is not null && !string.Equals(typed, replacement, StringComparison.Ordinal))
        {
            screenshot = TryShot(work, round, "khong-khop-sau-khi-go");
            return Conclude(
                CaseStatus.Failed,
                $"Văn bản trong ô nhập không khớp với đường dẫn cần chọn: ô nhập {Quote(typed)} (độ dài {typed.Length}), "
                + $"kỳ vọng {Quote(replacement)} (độ dài {replacement.Length}).");
        }

        var openReady = Uia.Until(() => PickOpenButton(dialog) is not null, 10000, 300);
        var open = openReady ? PickOpenButton(dialog) : null;

        if (open is null)
        {
            return Conclude(CaseStatus.Failed, "Không tìm thấy nút \"Mở\" trong trình chọn. Cây điều khiển đã được lưu tại: " + treeFile);
        }

        evidence.Add($"Nút \"Mở\": {Uia.Describe(open)}");

        var openClick = Uia.Click(open, "Nút Mở");
        evidence.Add(openClick.Detail);

        if (openClick.Clicked)
        {
            // Đường dẫn chuột thành công.
        }
        else if (usedKeyboard)
        {
            // Đã đi theo đường dẫn bàn phím: nhấn Enter tại ô tên tệp chính là "Mở".
            evidence.Add("Nút \"Mở\" cũng bị che khuất, chuyển sang nhấn Enter tại ô tên tệp để gửi (tương đương nhấp \"Mở\").");
            NativeInput.PressEnter();
        }
        else
        {
            return Conclude(CaseStatus.Blocked, "Không thể nhấp trúng nút \"Mở\": " + openClick.Detail);
        }

        var dialogClosed = Uia.Until(
            () => Uia.FindDialog(fixture.ProcessId) is null,
            20000);

        if (!dialogClosed)
        {
            screenshot = TryShot(work, round, "hop-thoai-khong-dong");
            return Conclude(
                CaseStatus.Failed,
                "Trình chọn không đóng lại trong vòng 20 giây sau khi nhấp \"Mở\" — lựa chọn không được chấp nhận. Ảnh chụp màn hình: " + (screenshot ?? "(không chụp được)"));
        }

        evidence.Add("Trình chọn đã đóng sau khi nhấp \"Mở\" (kết quả quan sát được).");

        // ---- 4. Khẳng định riêng của bộ thử nghiệm + đọc độc lập dữ liệu trên đĩa để đối chiếu "sản phẩm đã nhận được gì" ----

        if (!fixture.WaitForExit(120000))
        {
            screenshot ??= TryShot(work, round, "bo-thu-nghiem-chua-thoat");
            return Conclude(
                CaseStatus.Failed,
                "Bộ thử nghiệm không thoát trong vòng 120 giây sau khi trình chọn đóng. Ảnh chụp màn hình: " + (screenshot ?? "(không chụp được)"));
        }

        var verdict = ReadFixtureVerdict(fixtureReport);
        evidence.Add($"Báo cáo bộ thử nghiệm: {fixtureReport}");

        if (!verdict.Ok)
        {
            screenshot ??= TryShot(work, round, "khang-dinh-bo-thu-nghiem-that-bai");
            return Conclude(CaseStatus.Failed, "Khẳng định bên trong bộ thử nghiệm không đạt: " + verdict.Detail);
        }

        var dataDirectory = ReadFixtureDataDirectory(fixtureReport);
        if (dataDirectory is null)
        {
            return Conclude(CaseStatus.Failed, "Không đọc được thư mục dữ liệu trong báo cáo bộ thử nghiệm, không thể đối chiếu độc lập kết quả đã lưu xuống đĩa.");
        }

        var entries = ProductLibrary.ReadEntries(dataDirectory);
        evidence.Add($"Thư mục dữ liệu của bộ thử nghiệm: {ProductLibrary.Describe(dataDirectory)}");

        var exact = entries.FirstOrDefault(entry => string.Equals(entry.Target, replacement, StringComparison.Ordinal));

        if (exact is null)
        {
            var nearMiss = entries.FirstOrDefault(entry =>
                entry.Target.Contains(Path.GetFileName(replacement), StringComparison.OrdinalIgnoreCase));

            return Conclude(
                CaseStatus.Failed,
                "Chuỗi đích mà sản phẩm lưu xuống đĩa không khớp từng ký tự với đường dẫn cần chọn. "
                + $"Kỳ vọng {Quote(replacement)}; mục gần nhất trong thư viện là {Quote(nearMiss?.Target)} "
                + $"(có {entries.Count} mục trong thư viện):"
                + Environment.NewLine
                + string.Join(Environment.NewLine, entries.Select(entry => "  · " + Quote(entry.Target))));
        }

        if (exact.IsTargetMissing)
        {
            return Conclude(CaseStatus.Failed, "Sau khi định vị lại, mục vẫn bị đánh dấu là mất hiệu lực: " + Quote(exact.Target));
        }

        return Conclude(
            CaseStatus.Passed,
            $"Trình chọn gốc đã chấp nhận đường dẫn được gõ vào, đích mà sản phẩm lưu xuống đĩa khớp từng ký tự với đường dẫn đó ({Quote(exact.Target)}), "
            + "mục không còn mất hiệu lực, và các khẳng định \"khôi phục biểu tượng + nhấp để khởi chạy\" trong bộ thử nghiệm đều đạt.");
    }

    /// <summary>
    /// Chọn ra ô nhập tên tệp.
    /// </summary>
    /// <remarks>
    /// Sắp xếp theo mức độ tin cậy của bằng chứng thay vì đoán theo vị trí: trong hộp thoại tệp từ Vista trở về sau,
    /// mã định danh tự động hóa của ô nhập liệu bên trong hộp tổ hợp tên tệp là <c>1148</c>; hộp thoại cổ điển là <c>1001</c>;
    /// bước lùi tiếp theo mới xét xem tên có chứa "Tên tệp / File name" hay không.
    /// Nếu cả ba tiêu chí đều không trúng thì lấy ô nhập liệu khả dụng nằm dưới cùng: trong hộp thoại tệp, ô tên tệp vốn luôn nằm ở phía dưới.
    /// Cây điều khiển thực tế sẽ được lưu lại trong lần chạy đầu tiên; nếu chọn nhầm có thể đối chiếu trực tiếp vào đó để sửa.
    /// </remarks>
    private static AutomationElement? PickFileNameEdit(AutomationElement dialog)
    {
        var candidates = Uia.Descendants(dialog, ControlType.Edit)
            .Where(Uia.IsEnabled)
            .Where(element => !Uia.IsOffscreen(element))
            .Where(element => !Uia.RectOf(element).IsEmpty)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(element => Uia.RectOf(element).Top)
            .First();

        int Score(AutomationElement element)
        {
            var id = Uia.AutomationId(element);

            if (id == "1148")
            {
                return 100;
            }

            if (id == "1001")
            {
                return 90;
            }

            if (HasFileLabel(Uia.Name(element)))
            {
                return 80;
            }

            var parent = Uia.Parent(element);
            if (parent is not null && HasFileLabel(Uia.Name(parent)))
            {
                return 70;
            }

            return 10;
        }

        static bool HasFileLabel(string text) =>
            text.Contains("Tên tệp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("File name", StringComparison.OrdinalIgnoreCase);
    }

    private static AutomationElement? PickOpenButton(AutomationElement dialog)
    {
        var buttons = Uia.Descendants(dialog, ControlType.Button)
            .Where(Uia.IsEnabled)
            .Where(element => !Uia.IsOffscreen(element))
            .ToList();

        return buttons.FirstOrDefault(element => Uia.AutomationId(element) == "1")
            ?? buttons.FirstOrDefault(element =>
                Uia.Name(element).StartsWith("Mở", StringComparison.OrdinalIgnoreCase)
                || Uia.Name(element).StartsWith("Open", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Đọc kết luận của ca kiểm thử picker trong báo cáo bộ thử nghiệm.</summary>
    private static (bool Ok, string Detail) ReadFixtureVerdict(string path)
    {
        if (!File.Exists(path))
        {
            return (false, "Bộ thử nghiệm không ghi ra tệp báo cáo: " + path);
        }

        var document = JsonNode.Parse(File.ReadAllText(path));

        if (document?["checks"] is not JsonArray checks)
        {
            return (false, "Báo cáo bộ thử nghiệm ở dạng hết thời gian chờ (không có kết quả từng mục): " + (document?["detail"]?.GetValue<string>() ?? "Không có mô tả"));
        }

        var picker = checks.FirstOrDefault(node =>
            (node?["name"]?.GetValue<string>() ?? string.Empty).StartsWith("native picker", StringComparison.OrdinalIgnoreCase));

        if (picker is null)
        {
            return (false, "Không có ca kiểm thử picker trong báo cáo bộ thử nghiệm — nó đã không chạy tới bước đó.");
        }

        var pass = picker["pass"]?.GetValue<bool>() ?? false;
        var detail = picker["detail"]?.GetValue<string>() ?? string.Empty;

        return (pass, pass ? "Khẳng định bên trong bộ thử nghiệm đã đạt" : detail);
    }

    private static string? ReadFixtureDataDirectory(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonNode.Parse(File.ReadAllText(path));
        var root = document?["dataDirectory"]?.GetValue<string>();

        return root is null ? null : Path.Combine(root, "data");
    }

    private static string TryShot(string work, int round, string label)
    {
        var path = Path.Combine(work, $"{label}-vong-{round}.png");
        return ScreenShot.TryCapture(path) ?? "(không thể chụp màn hình)";
    }

    private static string DescribeWindows(int processId)
    {
        var lines = Win32.TopLevelWindows()
            .Select(Win32.Describe)
            .Where(facts => facts is not null && facts.ProcessId == processId)
            .Select(facts => "  · " + facts!.Describe())
            .ToList();

        return lines.Count == 0 ? "  (không có cửa sổ cấp cao nhất nào hiển thị của tiến trình này)" : string.Join(Environment.NewLine, lines);
    }

    private static string Quote(string? text) => text is null ? "(không đọc được)" : $"\"{text}\"";

    private static string Tail(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "(không có đầu ra)" : string.Join(Environment.NewLine, lines.TakeLast(12));
}