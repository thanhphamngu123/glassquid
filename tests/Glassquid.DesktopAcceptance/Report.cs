using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glassquid.DesktopAcceptance;

/// <summary>Kết luận ca kiểm thử. Cả bốn giá trị đều phải có khả năng xuất hiện trong báo cáo, thiếu một cái là coi ca chưa chạy thành đã chạy.</summary>
internal enum CaseStatus
{
    /// <summary>Kỳ vọng khớp với thực tế, và bằng chứng đến từ kết quả có thể quan sát được.</summary>
    Passed,

    /// <summary>Đã chạy được, nhưng hành vi sản phẩm không khớp với kỳ vọng.</summary>
    Failed,

    /// <summary>Môi trường không đáp ứng (khóa màn hình, cách ly quyền, thiếu thành phẩm, có sẵn phiên bản đang chạy), lần này chưa kiểm chứng được sản phẩm.</summary>
    Blocked,

    /// <summary>Lần này được yêu cầu chạy nhưng đã không chạy.</summary>
    NotRun,
}

/// <summary>
/// Một bản ghi ca kiểm thử. Các trường được định nghĩa theo định dạng kết quả thống nhất của lô D: tên ca, mã yêu cầu, phương thức điều khiển,
/// kỳ vọng, thực tế, kết luận, bằng chứng, ảnh chụp màn hình, cùng môi trường sản phẩm / hệ thống / hiển thị.
/// </summary>
internal sealed record CaseRecord(
    string Name,
    string Requirement,
    string Driver,
    string Expected,
    string Actual,
    CaseStatus Status,
    IReadOnlyList<string> Evidence,
    string? Screenshot);

/// <summary>Ảnh chụp nhanh môi trường khi trình điều khiển chạy. Cố tình không đặt tên là Environment: tên đó sẽ đụng độ với System.Environment.</summary>
internal sealed record EnvironmentSnapshot(
    string ProductVersion,
    string ProductPath,
    string OperatingSystem,
    string Session,
    string Display,
    string Driver);

/// <summary>Ghi báo cáo theo định dạng của lô D: JSON để máy đọc và văn bản có thể dán thẳng vào báo cáo bàn giao.</summary>
internal sealed class Report
{
    private readonly List<CaseRecord> _cases = [];
    private readonly List<string> _log = [];

    internal Report(string path) => Path = System.IO.Path.GetFullPath(path);

    internal string Path { get; }

    internal EnvironmentSnapshot? EnvironmentInfo { get; private set; }

    internal IReadOnlyList<CaseRecord> Cases => _cases;

    internal void SetEnvironment(EnvironmentSnapshot snapshot) => EnvironmentInfo = snapshot;

    internal void Plan(string name, string requirement, string expected)
    {
        if (_cases.Any(c => c.Name == name)) throw new InvalidOperationException("Duplicate case: " + name);
        _cases.Add(new CaseRecord(name, requirement, "Chưa thực thi", expected,
            "Đã đăng ký trong lượt này nhưng chưa thực thi; bị chặn bởi cổng kiểm soát, ngoại lệ hoặc quá thời gian đều không tính là đạt.", CaseStatus.NotRun, [], null));
    }

    internal void Note(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        _log.Add(line);
        Console.WriteLine(line);
    }

    internal void Add(CaseRecord record)
    {
        var index = _cases.FindIndex(c => c.Name == record.Name && c.Status == CaseStatus.NotRun);
        if (index >= 0) _cases[index] = record;
        else _cases.Add(record);

        var mark = record.Status switch
        {
            CaseStatus.Passed => "PASS",
            CaseStatus.Failed => "FAIL",
            CaseStatus.Blocked => "BLOCK",
            _ => "NOTRUN",
        };

        Console.WriteLine($"{mark} {record.Name} | {record.Requirement} | {record.Actual}");

        foreach (var evidence in record.Evidence)
        {
            Console.WriteLine($"      Bằng chứng: {evidence}");
        }

        if (record.Screenshot is not null)
        {
            Console.WriteLine($"      Ảnh chụp: {record.Screenshot}");
        }
    }

    /// <summary>
    /// Mã thoát: chỉ cần có một ca thất bại là 1; không có thất bại nhưng có bị chặn hoặc chưa chạy là 2;
    /// đạt toàn bộ mới là 0. Bỏ qua, thiếu ứng dụng, khóa màn hình đều không nhận được 0.
    /// </summary>
    internal int ExitCode()
    {
        if (_cases.Any(caseRecord => caseRecord.Status == CaseStatus.Failed))
        {
            return 1;
        }

        if (_cases.Count == 0)
        {
            return 2;
        }

        return _cases.All(caseRecord => caseRecord.Status == CaseStatus.Passed) ? 0 : 2;
    }

    internal void Write()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var status = ExitCode() switch
        {
            0 => "all passed",
            1 => "failed",
            _ => "blocked or not run",
        };

        var document = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["exitCode"] = ExitCode(),
            ["status"] = status,
            ["driver"] = "Nhập liệu màn hình nền thực tế: UI Automation định vị điều khiển + SendInput chèn chuột và bàn phím",
            ["environment"] = EnvironmentInfo is null ? null : new JsonObject
            {
                ["productVersion"] = EnvironmentInfo.ProductVersion,
                ["productPath"] = EnvironmentInfo.ProductPath,
                ["os"] = EnvironmentInfo.OperatingSystem,
                ["session"] = EnvironmentInfo.Session,
                ["display"] = EnvironmentInfo.Display,
                ["driver"] = EnvironmentInfo.Driver,
            },
            ["totals"] = new JsonObject
            {
                ["passed"] = _cases.Count(c => c.Status == CaseStatus.Passed),
                ["failed"] = _cases.Count(c => c.Status == CaseStatus.Failed),
                ["blocked"] = _cases.Count(c => c.Status == CaseStatus.Blocked),
                ["notRun"] = _cases.Count(c => c.Status == CaseStatus.NotRun),
            },
            ["cases"] = new JsonArray([.. _cases.Select(ToJson)]),
            ["log"] = new JsonArray([.. _log.Select(line => JsonValue.Create(line))]),
        };

        File.WriteAllText(Path, document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,

            // Bộ mã hóa mặc định sẽ chuyển ký tự Unicode sang \uXXXX. Bằng chứng viết cho người đọc nên giữ nguyên dạng dễ đọc.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), Encoding.UTF8);

        var text = System.IO.Path.ChangeExtension(Path, ".log");
        var lines = new StringBuilder();
        lines.AppendLine($"Nghiệm thu màn hình nền Glassquid: {status} (mã thoát {ExitCode()})");
        lines.AppendLine($"Thời gian: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        // Lặp lại từng mục môi trường trong biểu mẫu để khi trích dẫn riêng một ca kiểm thử vẫn có sẵn đầy đủ ngữ cảnh.
        if (EnvironmentInfo is not null)
        {
            lines.AppendLine($"Sản phẩm: {EnvironmentInfo.ProductVersion} ({EnvironmentInfo.ProductPath})");
            lines.AppendLine($"Hệ điều hành: {EnvironmentInfo.OperatingSystem}");
            lines.AppendLine($"Phiên: {EnvironmentInfo.Session}");
            lines.AppendLine($"Hiển thị: {EnvironmentInfo.Display}");
        }

        lines.AppendLine();

        foreach (var caseRecord in _cases)
        {
            lines.AppendLine($"{caseRecord.Status} | {caseRecord.Name}");
            lines.AppendLine($"  Yêu cầu: {caseRecord.Requirement}");
            lines.AppendLine($"  Điều khiển: {caseRecord.Driver}");
            lines.AppendLine($"  Kỳ vọng: {caseRecord.Expected}");
            lines.AppendLine($"  Thực tế: {caseRecord.Actual}");

            foreach (var evidence in caseRecord.Evidence)
            {
                lines.AppendLine($"  Bằng chứng: {evidence}");
            }

            if (caseRecord.Screenshot is not null)
            {
                lines.AppendLine($"  Ảnh chụp: {caseRecord.Screenshot}");
            }

            lines.AppendLine();
        }

        lines.AppendLine("—— Nhật ký chạy ——");
        foreach (var line in _log)
        {
            lines.AppendLine(line);
        }

        File.WriteAllText(text, lines.ToString(), Encoding.UTF8);
    }

    private JsonObject ToJson(CaseRecord record) => new()
    {
        ["case"] = record.Name,
        ["requirement"] = record.Requirement,
        ["driver"] = record.Driver,
        ["expected"] = record.Expected,
        ["actual"] = record.Actual,
        ["status"] = record.Status.ToString(),
        ["evidence"] = new JsonArray([.. record.Evidence.Select(item => JsonValue.Create(item))]),
        ["screenshot"] = record.Screenshot,
        ["environment"] = EnvironmentInfo is null ? null : new JsonObject
        {
            ["productVersion"] = EnvironmentInfo.ProductVersion,
            ["os"] = EnvironmentInfo.OperatingSystem,
            ["display"] = EnvironmentInfo.Display,
        },
    };

    /// <summary>Ảnh chụp nhanh môi trường của máy hiện tại và lần chạy này; phiên bản sản phẩm lấy từ chính thành phẩm thay vì hằng số trong mã nguồn.</summary>
    internal static EnvironmentSnapshot Snapshot(string? productPath, string driver)
    {
        var version = "(không tìm thấy thành phẩm)";

        if (productPath is not null && File.Exists(productPath))
        {
            var info = FileVersionInfo.GetVersionInfo(productPath);
            version = info.FileVersion ?? info.ProductVersion ?? "(không đọc được phiên bản)";
        }

        var monitors = Win32.Monitors();
        var display = monitors.Count == 0
            ? "(không liệt kê được màn hình)"
            : string.Join(
                "; ",
                monitors.Select(monitor => $"{(monitor.Primary ? "Chính" : "Phụ")} {monitor.Rect.Describe()} vùng làm việc {monitor.Work.Describe()}"))
              + $"; màn hình nền ảo {Win32.VirtualScreen().Describe()}; nhận thức DPI của tiến trình này {Win32.DpiAwareness()}";

        var session =
            $"pid {Environment.ProcessId}; phiên {Process.GetCurrentProcess().SessionId}"
            + $"; phiên bảng điều khiển hoạt động {Win32.ActiveConsoleSessionId()}"
            + $"; màn hình nền nhập liệu {Win32.InputDesktopName() ?? "(đọc thất bại)"}";

        return new EnvironmentSnapshot(
            version,
            productPath ?? "(chưa chỉ định)",
            $"{Environment.OSVersion.VersionString} ({Environment.OSVersion.Version})",
            session,
            display,
            driver);
    }
}
