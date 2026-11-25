using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Kiểm thử xác minh tối thiểu 2: Kéo một tệp có tên chứa chữ có dấu và dấu cách
/// từ <strong>File Explorer thật</strong> vào Thanh dock thật, kiểm tra thư viện ứng dụng
/// đã thêm mới và ghim mục đó, đồng thời vẫn còn sau khi khởi động lại.
/// </summary>
/// <remarks>
/// Khác biệt so với <c>Glassquid.UiAcceptance --drag</c> hiện có nằm ở <strong>nguồn</strong>:
/// bài kiểm thử kia dùng một cửa sổ WPF tự vẽ làm nguồn kéo, chỉ chứng minh được kênh OLE
/// có thể truyền <c>CF_HDROP</c> xuyên tiến trình; ở đây dùng đúng nguồn mà người dùng thật sự
/// sử dụng hằng ngày — mục tệp trong cửa sổ File Explorer, được điều khiển bởi chuỗi nhấn, di chuyển
/// và thả chuột thật. Lệnh <c>drag</c> của Computer Use ở vòng trước từ chối rõ ràng
/// "điểm đích thuộc cửa sổ khác" và trả về <c>not target window</c>, đó không phải lỗi kéo thả
/// của sản phẩm; trình điều khiển này gửi toàn bộ thao tác trong cùng một chuỗi đầu vào hệ thống,
/// nên có thể kéo xuyên cửa sổ.
///
/// Ba bước ghi xuống đĩa và khởi động lại được cố ý tách riêng để đối chiếu: không đọc tệp
/// trong lúc đang kéo (tránh nhầm lẫn "xem trước" thành "đã xác nhận lưu"), đọc <c>apps.json</c>
/// và <c>dock-layout.json</c> sau khi thả chuột, rồi sau khi khởi động lại thì dùng một tiến trình
/// hoàn toàn mới chạy <c>--list-library</c> để đọc lại từ đĩa.
/// </remarks>
internal static class ExplorerDropCase
{
    internal const string CaseName = "Kéo từ Explorer vào Thanh dock thật và ghim lại (đường dẫn có dấu và dấu cách)";

    private const string Requirement = "D-01 Nhập (lối vào kéo thả); D-23 Hình thái cửa sổ Thanh dock; R-07 Mục vẫn còn sau khi tải lại";

    private const string Driver = "Thao tác desktop thật: UIA định vị mục Explorer và cửa sổ Thanh dock + SendInput kéo thả";

    private const string Expected =
        "Sau khi kéo một tệp có chữ có dấu và dấu cách từ Explorer vào Thanh dock rồi thả chuột, "
        + "mục đó xuất hiện trong thư viện ứng dụng và được ghim lên Thanh dock; "
        + "sau khi khởi động lại sản phẩm, mục vẫn còn và vẫn được ghim; trong suốt quá trình đó, "
        + "cửa sổ Thanh dock giữ WS_EX_NOACTIVATE, không xuất hiện trên thanh tác vụ và kích thước không bị thay đổi bởi bài kiểm thử.";

    internal static CaseRecord Run(DriverContext context, int round, int rounds)
    {
        var name = $"{CaseName} (Lượt {round}/{rounds})";
        var evidence = new List<string>();
        string? screenshot = null;

        CaseRecord Conclude(CaseStatus status, string actual) =>
            new(name, Requirement, Driver, Expected, actual, status, [.. evidence], screenshot);

        var work = context.NewWorkDirectory($"Keo-tha-Explorer-luot-{round}");
        var folder = Path.Combine(work, "Kéo thả Explorer có dấu cách");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Mục kéo thả có dấu cách.txt");
        File.WriteAllText(file, "glassquid desktop driver drop sample");
        evidence.Add($"Tệp được kéo vào: {file}");

        // ---- 1. Điều kiện tiên quyết: Thư viện ứng dụng thật phải sạch ----

        var libraryRoot = ProductLibrary.DefaultRoot;
        var foreign = ProductLibrary.ForeignEntries(libraryRoot, DriverContext.WorkRootFamily);

        if (foreign.Count > 0)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Trong thư viện ứng dụng Glassquid trên máy này có {foreign.Count} bản ghi không phải do trình điều khiển này tạo, "
                + "việc xác minh kéo thả sẽ ghi dữ liệu vào thư viện nên từ chối chạy trên dữ liệu thật:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, foreign.Take(5).Select(entry => "  · " + entry.Target))
                + Environment.NewLine
                + "Vui lòng chạy lượt kiểm thử này trong tài khoản kiểm thử riêng hoặc máy ảo.");
        }

        var existing = DesktopGate.ExistingInstanceReason();
        if (existing is not null)
        {
            return Conclude(CaseStatus.Blocked, existing);
        }

        // ---- 2. Khởi chạy sản phẩm thật, đọc cửa sổ Thanh dock của nó ----

        using var product = ProcessLease.Launch(context.ProductPath, []);
        evidence.Add($"Lệnh sản phẩm: {product.CommandLine}");
        context.Report.Note($"Kiểm thử xác minh tối thiểu 2 (lượt {round}): sản phẩm đã khởi chạy pid {product.ProcessId}");

        var dock = Uia.WaitForTopWindow(product.ProcessId, null, "Dock", 60000);
        if (dock is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Không đợi được cửa sổ Thanh dock của sản phẩm trong 60 giây (pid {product.ProcessId}). Các cửa sổ cấp cao nhất của tiến trình này:"
                + Environment.NewLine + DescribeWindows(product.ProcessId));
        }

        var before = DescribeDock(dock);
        evidence.Add("Trước khi kéo thả: " + before);
        screenshot = TryShot(work, round, "Truoc-khi-keo-tha");

        // ---- 3. Mở File Explorer thật, tìm mục tệp theo danh tính ----

        var leaf = Path.GetFileName(folder);

        // Không dùng UseShellExecute: nó không hỗ trợ ArgumentList trên .NET, mà đường dẫn lại có dấu cách,
        // ghép chuỗi thủ công bằng dấu ngoặc kép sớm muộn cũng lỗi. explorer.exe sẽ bàn giao cửa sổ cho tiến trình shell đang chạy,
        // tiến trình vừa khởi chạy sẽ thoát ngay, nên không đưa vào quản lý vòng đời tiến trình — khi dọn dẹp sẽ đóng cửa sổ theo tên thư mục duy nhất.
        _ = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            ArgumentList = { folder },
        });

        var explorerWindow = Uia.WaitForWindow(
            "CabinetWClass",
            leaf,
            60000,
            accept: window => IsExplorerProcess(window));

        if (explorerWindow is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Không đợi được cửa sổ Explorer có tiêu đề chứa \"{leaf}\" trong 60 giây (lớp CabinetWClass). "
                + "Các cửa sổ cấp cao nhất liệt kê được:" + Environment.NewLine + DescribeWindows(null));
        }

        evidence.Add($"Cửa sổ Explorer: {Uia.Describe(explorerWindow)}");

        // Cửa sổ xuất hiện không có nghĩa là nội dung đã được điền xong: Explorer nạp danh sách bất đồng bộ,
        // tìm ngay một lần sẽ dẫn đến kết luận sai là "trình điều khiển không định vị được mục". Do đó cần thăm dò chờ nó xuất hiện.
        var item = Uia.Until(() => FindFileItem(explorerWindow, file) is not null, 30000, 400)
            ? FindFileItem(explorerWindow, file)
            : null;

        if (item is null)
        {
            screenshot = TryShot(work, round, "Khong-tim-thay-muc-tep");
            return Conclude(
                CaseStatus.Blocked,
                $"Đã đợi 30 giây trong cửa sổ Explorer nhưng vẫn không thấy mục mang tên \"{Path.GetFileName(file)}\". "
                + "Trình điều khiển không định vị được nguồn kéo, sản phẩm chưa được xác minh. Cây điều khiển mục của cửa sổ này đã được lưu:"
                + Environment.NewLine + DumpItems(work, round, explorerWindow));
        }

        evidence.Add($"Mục nguồn kéo: {Uia.Describe(item)}");

        var sourceRect = Uia.RectOf(item);
        var drop = DropPoint(dock);

        if (sourceRect.IsEmpty || !Win32.VirtualScreen().Contains(new ScreenPoint(sourceRect.Left + 1, sourceRect.Top + 1)))
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Mục nguồn kéo không nằm trong màn hình khả kiến (hình chữ nhật {sourceRect.Describe()}, màn hình ảo {Win32.VirtualScreen().Describe()}).");
        }

        var start = new ScreenPoint(sourceRect.Left + (sourceRect.Width / 2), sourceRect.Top + (sourceRect.Height / 2));
        evidence.Add($"Điểm bắt đầu kéo {start.X},{start.Y} (tâm mục Explorer); điểm thả {drop.Point.X},{drop.Point.Y} ({drop.How})");

        // Trước khi nhấn chuột phải xác nhận bên dưới con trỏ đúng là Explorer. Lượt 1 thực tế từng thất bại: khi chưa kiểm tra điểm bắt đầu này,
        // thao tác nhấn và di chuyển bị gửi nhầm sang cửa sổ khác (ví dụ Thanh dock chưa nhường tiền cảnh), khiến cả lượt kéo bị vô hiệu mà không nhận ra.
        var explorerProcess = Uia.ProcessIdOf(explorerWindow);
        var sourceReady = false;
        WindowFacts? sourceOwner = null;

        for (var attempt = 0; attempt < 6 && !sourceReady; attempt++)
        {
            NativeInput.MoveTo(start);
            Thread.Sleep(250);

            var top = Win32.WindowAt(start);
            var root = top == 0 ? 0 : Win32.RootAncestor(top);
            sourceOwner = Win32.Describe(root);
            sourceReady = root != 0 && Win32.ProcessOf(root) == explorerProcess;

            if (!sourceReady)
            {
                Thread.Sleep(700);
            }
        }

        if (!sourceReady)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"Cửa sổ phía trên điểm bắt đầu kéo {start.X},{start.Y} không thuộc Explorer (pid {explorerProcess}), "
                + $"thực tế là {sourceOwner?.Describe() ?? "(không có cửa sổ)"}. Thao tác kéo chưa bắt đầu, sản phẩm chưa được xác minh.");
        }

        // ---- 4. Một thao tác kéo thả xuyên cửa sổ thật ----

        NativeInput.DragPath(NativeInput.Interpolate(start, drop.Point, 24));

        // ---- 5. Kết quả cần đợi vẫn là kết quả quan sát được: mục được ghi xuống đĩa ----

        var entriesAfterDrop = Array.Empty<LibraryEntry>();
        var landed = Uia.Until(
            () =>
            {
                try
                {
                    entriesAfterDrop = [.. ProductLibrary.ReadEntries(libraryRoot)];
                    return entriesAfterDrop.Any(entry => string.Equals(entry.Target, file, StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // Sản phẩm có thể đang thay thế tệp nguyên tử, đọc lại ở lượt kế tiếp.
                    return false;
                }
            },
            40000,
            300);

        screenshot = TryShot(work, round, "Sau-khi-keo-tha");

        var status = StatusTextOf(dock);
        evidence.Add($"Văn bản trạng thái trên Thanh dock sau khi thả chuột: {status ?? "(không có văn bản)"}");
        evidence.Add("Sau khi kéo thả: " + DescribeDock(dock));
        evidence.Add(ProductLibrary.Describe(libraryRoot));

        if (!landed)
        {
            return Conclude(
                CaseStatus.Failed,
                $"Trong vòng 40 giây sau khi thả chuột vẫn không thấy \"{file}\" xuất hiện trong thư viện ứng dụng. "
                + $"Thư viện hiện có {entriesAfterDrop.Length} mục, văn bản trạng thái Thanh dock là {status ?? "(trống)"}. "
                + "Ảnh chụp màn hình: " + (screenshot ?? "(không chụp được)"));
        }

        var entry = entriesAfterDrop.Single(candidate => string.Equals(candidate.Target, file, StringComparison.Ordinal));

        // ---- 6. Ghim vào Thanh dock (ngữ nghĩa của kéo vào là "thêm và ghim") ----

        var pinned = ProductLibrary.ReadPinned(libraryRoot);
        if (!pinned.Contains(entry.Id))
        {
            return Conclude(
                CaseStatus.Failed,
                $"Mục đã vào thư viện ứng dụng (id {entry.Id}) nhưng chưa được ghim lên Thanh dock; các mục đã ghim trong dock-layout.json: "
                + string.Join(", ", pinned));
        }

        evidence.Add($"ID mục đã ghim: {entry.Id}");

        var afterDropFacts = DescribeDock(dock);

        // ---- 7. Kiểm tra sau khi khởi động lại: tiến trình mới đọc lại từ đĩa ----

        evidence.Add(product.Stop("Đã ghi dữ liệu kéo thả xuống đĩa, chuẩn bị khởi động lại để kiểm tra tính bền vững"));

        var listing = RunListLibrary(context);
        evidence.Add($"Đầu ra của --list-library sau khi khởi động lại (phần cuối):{Environment.NewLine}{Tail(listing)}");

        if (!listing.Contains(file, StringComparison.Ordinal))
        {
            return Conclude(
                CaseStatus.Failed,
                "Tiến trình mới sau khi khởi động lại không liệt kê được bản ghi này từ đĩa, chứng tỏ việc ghim chỉ tồn tại trong bộ nhớ. Phần cuối đầu ra --list-library:"
                + Environment.NewLine + Tail(listing));
        }

        using var relaunched = ProcessLease.Launch(context.ProductPath, []);
        var dockAgain = Uia.WaitForTopWindow(relaunched.ProcessId, null, "Dock", 60000);

        if (dockAgain is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                "Không đợi được cửa sổ Thanh dock sau khi khởi động lại. Các cửa sổ cấp cao nhất của tiến trình này:"
                + Environment.NewLine + DescribeWindows(relaunched.ProcessId));
        }

        var afterReload = DescribeDock(dockAgain);
        evidence.Add("Sau khi khởi động lại: " + afterReload);

        // Hình thái cửa sổ không được thay đổi vì bài kiểm thử: cả ba lần lấy mẫu đều phải có kiểu mở rộng đúng yêu cầu sản phẩm.
        var shapes = new[] { before, afterDropFacts, afterReload };

        foreach (var shape in shapes)
        {
            if (!shape.Facts.HasNoActivate || shape.Facts.IsOnTaskbar)
            {
                return Conclude(
                    CaseStatus.Failed,
                    "Hình thái cửa sổ Thanh dock không khớp yêu cầu sản phẩm (yêu cầu WS_EX_NOACTIVATE, không xuất hiện trên thanh tác vụ): "
                    + shape.Facts.Describe());
            }
        }

        // Chỉ so sánh kích thước giữa hai lần "sau khi kéo thả" và "sau khi khởi động lại": Thanh dock tự điều chỉnh kích thước theo nội dung,
        // việc mở rộng hợp lệ khi có ô biểu tượng mới vào là thiết kế sản phẩm, không phải thay đổi do bài kiểm thử. Hai lần có cùng nội dung phải giống hệt nhau.
        if (afterDropFacts.Facts.Rect.Describe() != afterReload.Facts.Rect.Describe())
        {
            return Conclude(
                CaseStatus.Failed,
                "Hai lần chạy có cùng nội dung (sau khi kéo thả / sau khi khởi động lại) nhưng kích thước Thanh dock lại không khớp: "
                + $"{afterDropFacts.Facts.Rect.Describe()} → {afterReload.Facts.Rect.Describe()}. "
                + "Điều này cho thấy hình thái cửa sổ sau khi khởi động lại khác với lúc kéo thả.");
        }

        if (afterReload.Tiles < afterDropFacts.Tiles)
        {
            return Conclude(
                CaseStatus.Failed,
                $"Số ô ứng dụng trên Thanh dock bị thiếu sau khi khởi động lại: trước khi khởi động lại có {afterDropFacts.Tiles}, sau khi khởi động lại có {afterReload.Tiles}.");
        }

        if (afterDropFacts.Tiles <= before.Tiles && before.Tiles == 0)
        {
            return Conclude(
                CaseStatus.Blocked,
                "Không thấy bất kỳ biểu tượng ô ứng dụng nào trong cây điều khiển của cửa sổ Thanh dock (cả trước và sau khi kéo đều là 0), "
                + "không thu thập được bằng chứng phía giao diện. Đây là hạn chế phương tiện định vị của trình điều khiển, không thể tính là sản phẩm đạt hay thất bại. "
                + "Cây điều khiển cửa sổ đã được lưu: " + DumpWindow(work, round, dockAgain));
        }

        // ---- 8. Dọn dẹp: chỉ đóng cửa sổ Explorer do chính lượt này tự mở ----

        var explorerFacts = Win32.Describe(Uia.Handle(explorerWindow));
        if (explorerFacts is not null && explorerFacts.Title.Contains(leaf, StringComparison.Ordinal))
        {
            _ = Win32.RequestClose(explorerFacts.Handle);
            evidence.Add($"Đã gửi yêu cầu đóng cửa sổ Explorer mở trong lượt này (tiêu đề chứa \"{leaf}\").");
        }
        else
        {
            evidence.Add("Tiêu đề cửa sổ Explorer không còn chứa tên thư mục duy nhất của lượt này nên không tác động vào nó.");
        }

        evidence.Add(relaunched.Stop("Dọn dẹp"));

        return Conclude(
            CaseStatus.Passed,
            $"Kéo thả thật ghi đĩa thành công: thư viện ứng dụng thêm mới 1 mục ({entry.DisplayName}), đã ghim lên Thanh dock; "
            + $"sau khi khởi động lại tiến trình mới vẫn liệt kê được mục đó, hình thái và kích thước cửa sổ Thanh dock nhất quán trước sau (số ô {before.Tiles} → "
            + $"{afterDropFacts.Tiles} → {afterReload.Tiles}).");
    }

    /// <summary>
    /// Sau khi khởi động lại, dùng một tiến trình hoàn toàn mới để đọc thư viện ứng dụng từ đĩa.
    /// </summary>
    /// <remarks>
    /// Dừng tiến trình giao diện trước để tránh cơ chế chuyển tiếp đơn thể đẩy tiến trình này về luôn mà không in gì ra.
    /// Lấy hai nguồn bằng chứng: đầu ra chuẩn của tiến trình (tiến trình gốc là WinExe, báo cáo được ghi qua việc gắn vào console cha),
    /// và tệp <c>appservices.log</c> trong thư mục nhật ký của chính sản phẩm — chỉ cần một trong hai nơi có nhắc tới mục này là coi như đã đọc được,
    /// tránh việc chi tiết gắn console làm đánh giá nhầm một lần lưu bền vững thật sự thành thất bại.
    /// </remarks>
    private static string RunListLibrary(DriverContext context)
    {
        using var lister = ProcessLease.Launch(context.ProductPath, ["--list-library"], captureOutput: true);
        _ = lister.WaitForExit(60000);

        var text = string.Join(Environment.NewLine, lister.Output());
        var logFile = Path.Combine(ProductLibrary.DefaultRoot, "logs", "appservices.log");

        if (File.Exists(logFile))
        {
            try
            {
                text += Environment.NewLine + File.ReadAllText(logFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text += Environment.NewLine + $"(Không đọc được {logFile}: {ex.Message})";
            }
        }

        return text;
    }

    private static AutomationElement? FindFileItem(AutomationElement window, string filePath)
    {
        var full = Path.GetFileName(filePath);
        var withoutExtension = Path.GetFileNameWithoutExtension(filePath);

        var items = Uia.Descendants(window, ControlType.ListItem)
            .Concat(Uia.Descendants(window, ControlType.DataItem))
            .Where(element => !Uia.IsOffscreen(element))
            .Where(element => !Uia.RectOf(element).IsEmpty)
            .ToList();

        return items.FirstOrDefault(element => Matches(Uia.Name(element)));

        bool Matches(string itemName) =>
            itemName.Equals(full, StringComparison.OrdinalIgnoreCase)
            || itemName.Equals(withoutExtension, StringComparison.OrdinalIgnoreCase)
            // Khi bật "ẩn phần mở rộng đã biết", tên mục chính là phần đã bỏ đuôi tệp, dùng tiền tố để dự phòng.
            || itemName.StartsWith(withoutExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chọn một điểm thả nằm trong cửa sổ Thanh dock và không bị dải ô biểu tượng chặn mất.
    /// </summary>
    /// <remarks>
    /// Dải ô biểu tượng chỉ xử lý đúng một định dạng dữ liệu "sắp xếp nội bộ" (xem <c>DockTileStrip.OnDragOver</c>),
    /// khi danh sách tệp đi tới nó không đặt <c>Handled</c>, sự kiện sẽ nổi bọt lên <c>OnExternalDrop</c>
    /// của cửa sổ. Vì vậy điểm thả chỉ cần nằm trong cửa sổ là được; ưu tiên chọn vùng văn bản trạng thái,
    /// vì đó chắc chắn là vùng trống và ít bị ảnh hưởng nhất bởi thay đổi bố cục ở lần kế tiếp.
    /// </remarks>
    private static (ScreenPoint Point, string How) DropPoint(AutomationElement dock)
    {
        // Ưu tiên văn bản trạng thái kết quả kéo thả: nó luôn nằm bên dưới hàng ô biểu tượng, điểm thả không bị dịch theo lần đổi bố cục kế tiếp,
        // và bản thân câu "Đã ghim N mục" chính là kết quả quan sát được sẽ cần đối chiếu sau đó.
        var status = Uia.Descendants(dock, ControlType.Text)
            .FirstOrDefault(element =>
                (Uia.Name(element).Contains("ghim", StringComparison.OrdinalIgnoreCase)
                    || Uia.Name(element).Contains("mục", StringComparison.OrdinalIgnoreCase))
                && !Uia.RectOf(element).IsEmpty);

        if (status is not null)
        {
            var rect = Uia.RectOf(status);
            if (!rect.IsEmpty)
            {
                return (new ScreenPoint(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2)), "Vùng văn bản trạng thái Thanh dock (bên trong cửa sổ)");
            }
        }

        // Phương án dự phòng vẫn nằm trong cửa sổ: dải ô biểu tượng chỉ xử lý định dạng sắp xếp nội bộ, khi danh sách tệp tới nó không đặt Handled,
        // sự kiện sẽ nổi bọt lên OnExternalDrop của cửa sổ, nên bất kỳ điểm nào trong cửa sổ đều làm điểm thả được.
        var window = Uia.RectOf(dock);
        if (!window.IsEmpty)
        {
            return (new ScreenPoint(window.Left + (window.Width / 2), window.Bottom - 8), "Mép dưới bên trong cửa sổ Thanh dock");
        }

        return (new ScreenPoint(Win32.VirtualScreen().Left + 100, Win32.VirtualScreen().Bottom - 40), "Góc dưới bên trái màn hình ảo (dự phòng)");
    }

    private sealed record DockShape(WindowFacts Facts, int Tiles, int Texts, string Status);

    private static DockShape DescribeDock(AutomationElement dock)
    {
        var facts = Win32.Describe(Uia.Handle(dock))
            ?? new WindowFacts(0, "?", "?", 0, 0, default, false, 0);

        var images = Uia.Descendants(dock, ControlType.Image);
        var texts = Uia.Descendants(dock, ControlType.Text);

        return new DockShape(facts, images.Count, texts.Count, StatusTextOf(dock) ?? string.Empty);
    }

    private static string? StatusTextOf(AutomationElement dock)
    {
        var texts = Uia.Descendants(dock, ControlType.Text)
            .Select(Uia.Name)
            .Where(text =>
                text.Contains("ghim", StringComparison.OrdinalIgnoreCase)
                || text.Contains("mục", StringComparison.OrdinalIgnoreCase)
                || text.Contains("thất bại", StringComparison.OrdinalIgnoreCase)
                || text.Contains("không thành công", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return texts.Count == 0 ? null : string.Join(" / ", texts);
    }

    private static bool IsExplorerProcess(AutomationElement window)
    {
        var processId = 0;

        try
        {
            processId = window.Current.ProcessId;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Xuất danh sách các mục trong cửa sổ Explorer để lưu trữ: dùng để đối chiếu danh tính điều khiển khi định vị thất bại.</summary>
    private static string DumpItems(string work, int round, AutomationElement window)
    {
        var path = Path.Combine(work, $"Muc-Explorer-luot-{round}.txt");
        File.WriteAllText(path, Uia.Dump(window));

        var names = Uia.Descendants(window, ControlType.ListItem)
            .Select(element => "  · \"" + Uia.Name(element) + "\"")
            .Take(20)
            .ToList();

        return path + Environment.NewLine + (names.Count == 0 ? "(Không có bất kỳ điều khiển mục nào)" : string.Join(Environment.NewLine, names));
    }

    private static string DumpWindow(string work, int round, AutomationElement window)
    {
        var path = Path.Combine(work, $"Cay-dieu-khien-Thanh-dock-luot-{round}.txt");
        File.WriteAllText(path, Uia.Dump(window));
        return path;
    }

    private static string TryShot(string work, int round, string label)
    {
        var path = Path.Combine(work, $"{label}-luot-{round}.png");
        return ScreenShot.TryCapture(path) ?? "(Ảnh chụp màn hình không khả dụng)";
    }

    private static string DescribeWindows(int? processId)
    {
        var lines = Win32.TopLevelWindows()
            .Select(Win32.Describe)
            .Where(facts => facts is not null && (processId is null || facts.ProcessId == processId))
            .Select(facts => "  · " + facts!.Describe())
            .Take(40)
            .ToList();

        return lines.Count == 0 ? "  (Không có cửa sổ cấp cao nhất nào hiển thị)" : string.Join(Environment.NewLine, lines);
    }

    private static string Tail(string text)
    {
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "(Không có đầu ra)" : string.Join(Environment.NewLine, lines.TakeLast(15));
    }
}
