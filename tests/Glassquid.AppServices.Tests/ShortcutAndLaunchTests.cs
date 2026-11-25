using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

/// <summary>Phân giải <c>.lnk</c>.</summary>
public class ShortcutResolverTests
{
    [Fact]
    public void DocRaMucTieuThamSoVaThuMucLamViec()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram("Chương trình mục tiêu.cmd");
        var shortcut = Path.Combine(workspace.Root, "Lối tắt.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--one --two \"có khoảng trắng\"",
            WorkingDirectory: workspace.Root)));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);
        Assert.Equal(program, info.Target);
        Assert.Equal("--one --two \"có khoảng trắng\"", info.Arguments);
        Assert.Equal(workspace.Root, info.WorkingDirectory);
        Assert.True(info.HasResolvedTarget);
        Assert.False(info.TargetIsUrl);
    }

    [Fact]
    public void ViTriBieuTuongCoChiSoThiChiSoDuocGiuLai()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "Lối tắt.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            workspace.CreateProgram("Chương trình.cmd"),
            IconLocation: @"C:\Windows\System32\shell32.dll,7")));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.Equal(@"C:\Windows\System32\shell32.dll,7", info?.IconSource);
    }

    [Fact]
    public void KhongGhiViTriBieuTuongThiQuayVeChinhMucTieu()
    {
        using var workspace = new TempWorkspace();
        var program = workspace.CreateProgram("Chương trình.cmd");
        var shortcut = Path.Combine(workspace.Root, "Lối tắt.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(program)));

        Assert.Equal(program, ShortcutResolver.Resolve(shortcut)?.IconSource);
    }

    [Fact]
    public void LoiTatTroToiUrlDocDuocUrlTuDanhSachId()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "Trang web.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition("https://example.com/duong dan co khoang trang")));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);

        // GetPath luôn trả về chuỗi rỗng đối với URL, cả bốn cờ đều đã thử. Chỉ sau khi bổ sung nhánh danh sách ID
        // thì ở đây mới có nội dung.
        Assert.True(info.TargetIsUrl, $"Mục tiêu thực tế \"{info.Target}\"");
        Assert.StartsWith("https://example.com/", info.Target, StringComparison.Ordinal);
        Assert.True(info.HasShellItem);
    }

    [Fact]
    public void TepRongKhongDocDuocThiTraVeNullThayViNemNgoaiLe()
    {
        using var workspace = new TempWorkspace();
        var empty = Path.Combine(workspace.Root, "Trống.lnk");
        File.WriteAllBytes(empty, []);

        Assert.Null(ShortcutResolver.Resolve(empty));
    }

    [Fact]
    public void TepKhongTonTaiThiTraVeNullThayViNemNgoaiLe()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(ShortcutResolver.Resolve(Path.Combine(workspace.Root, "Không tồn tại.lnk")));
    }

    [Fact]
    public void DuongDanRongThiTraVeNull()
    {
        Assert.Null(ShortcutResolver.Resolve(string.Empty));
        Assert.Null(ShortcutResolver.Resolve("   "));
    }

    [Fact]
    public void DuongDanTiengVietVaKhoangTrangKhepKin()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram(Path.Combine("Thư mục con chương trình", "Trình giả lập Ghi chú.cmd"));
        var folder = Path.Combine(workspace.Root, "Thư mục con lối tắt");
        Directory.CreateDirectory(folder);
        var shortcut = Path.Combine(folder, "Lối tắt có khoảng trắng và tiếng Việt.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--gia-tri \"Giá trị có khoảng trắng\"",
            WorkingDirectory: Path.GetDirectoryName(program)!)));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);
        Assert.Equal(program, info.Target);
        Assert.Equal("--gia-tri \"Giá trị có khoảng trắng\"", info.Arguments);
    }

    [Fact]
    public void GhiMotLoiTatRoiDocLaiCacTruongKhopNhau()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram("Chương trình khép kín.cmd");
        var definition = new ShortcutDefinition(
            program,
            Arguments: "--a --b",
            WorkingDirectory: workspace.Root,
            IconLocation: @"C:\Windows\System32\shell32.dll,2",
            Description: "Trường hợp kiểm thử khép kín");

        var shortcut = Path.Combine(workspace.Root, "Khép kín.lnk");
        Assert.True(ShortcutWriter.Write(shortcut, definition));

        var info = Assert.IsType<ShortcutInfo>(ShortcutResolver.Resolve(shortcut));

        Assert.Equal(definition.TargetPath, info.Target);
        Assert.Equal(definition.Arguments, info.Arguments);
        Assert.Equal(definition.WorkingDirectory, info.WorkingDirectory);
        Assert.Equal(definition.IconLocation, info.IconSource);
    }

    [Fact]
    public void PhanGiaiLienTucHaiTramLanKhongRoRiHandleGdiVaUser()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "Vòng lặp.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(workspace.CreateProgram("Chương trình.cmd"))));

        // Làm nóng một lần trước để loại bỏ nhiễu như lần đầu nạp dll.
        _ = ShortcutResolver.Resolve(shortcut);

        var before = CountGuiResources();

        for (var i = 0; i < 200; i++)
        {
            _ = ShortcutResolver.Resolve(shortcut);
        }

        var after = CountGuiResources();

        // Biểu hiện của rò rỉ là số lượng handle tăng đơn điệu. Cho phép dao động rất nhỏ (shell bên trong có bộ nhớ đệm),
        // nhưng nếu mỗi lần phân giải rò rỉ một handle thì ở đây sẽ tăng lên hai ba trăm.
        Assert.True(after.Gdi - before.Gdi < 20, $"GDI handle tăng từ {before.Gdi} lên {after.Gdi}");
        Assert.True(after.User - before.User < 20, $"USER handle tăng từ {before.User} lên {after.User}");
    }

    private static (int Gdi, int User) CountGuiResources()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();

        return (GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(nint process, int flags);
}

/// <summary>Khởi chạy mục.</summary>
public class LaunchServiceTests
{
    [Fact]
    public void MucKhongXacDinhTraVeUnknownItemThayViNemNgoaiLe()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Launch.LaunchItem("id-khong-ton-tai");

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchFailureReason.UnknownItem, result.Failure);
    }

    [Fact]
    public void MucTieuBienMatThiBaoCaoTargetMissingVaDuaRaMoTaDeDoc()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("Sẽ biến mất.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);

        var result = services.Launch.LaunchItem(entry.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchFailureReason.TargetMissing, result.Failure);

        // Mô tả phải giúp người dùng hiểu và biết bước tiếp theo cần làm gì, chứ không phải thông báo ngoại lệ thô.
        Assert.NotNull(result.Message);
        Assert.Contains("tìm lại vị trí", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MucBiHongKhongAnhHuongDenViecKhoiChayCacMucKhac()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var doomed = workspace.CreateFile("Sẽ biến mất.txt");
        var doomedEntry = ImportOne(services, doomed);
        File.Delete(doomed);

        var healthy = ImportOne(services, workspace.CreateFile("Bình thường.txt"));

        var broken = services.Launch.LaunchItem(doomedEntry.Id);
        Assert.False(broken.Succeeded);

        // Điều thực sự cần khẳng định là dòng này: mục hỏng trong thư viện không được làm liên lụy mục tốt.
        //
        // Chỉ kiểm tra **phán đoán**, không mở thật: mở thật sẽ bật Notepad/Explorer,
        // mà "cửa sổ do kiểm thử mở ra" lại là chỗ liên tục phát sinh vấn đề trong lượt này (cửa sổ sót lại, hộp thoại
        // "Vị trí không khả dụng" sau khi thư mục bị xóa). Thao tác mở thật được kiểm thử nghiệm thu chạy xác định bằng charmap
        // (nó luôn tự sở hữu cửa sổ riêng, đóng là thoát), cùng danh sách kiểm tra thủ công bao phủ.
        Assert.False(LaunchService.IsTargetGone(healthy));
        Assert.False(healthy.IsTargetMissing);
        Assert.True(ShellTargets.CanResolve(healthy.Target), "Shell không phân giải được mục tiêu của bản ghi bình thường này");
    }

    [Fact]
    public void MucTieuCuaThuMucVaTaiLieuDeuDuocShellPhanGiai()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var folder = ImportOne(services, workspace.CreateDirectory("Thư mục"));
        var document = ImportOne(services, workspace.CreateFile("Tài liệu.txt"));

        // Kiểm tra ở tầng dịch vụ "shell có nhận diện mục tiêu này không": dùng đúng nhánh kiểm tra mục tiêu shell: trong nghiệm thu,
        // không cửa sổ, không hộp thoại. Việc mở thật được bao phủ bởi nghiệm thu (charmap) và kiểm tra thủ công.
        Assert.True(ShellTargets.CanResolve(folder.Target), "Shell không phân giải được mục tiêu thư mục");
        Assert.True(ShellTargets.CanResolve(document.Target), "Shell không phân giải được mục tiêu tài liệu");

        Assert.False(LaunchService.IsTargetGone(folder));
        Assert.False(LaunchService.IsTargetGone(document));
    }

    [Fact]
    public void KhongKhopCuaSoChoMucKhongPhaiChuongTrinh()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // Thư mục, tài liệu và URL mỗi lần mở có thể rơi vào các tiến trình chủ khác nhau,
        // coi cửa sổ của chúng là "cửa sổ của mục này" sẽ gây nhận diện nhầm:
        // người dùng bấm vào một biểu tượng trang web, kết quả lại bị chuyển sang một cửa sổ trình duyệt khác đang mở sẵn.
        var folder = ImportOne(services, workspace.CreateDirectory("Thư mục"));
        var document = ImportOne(services, workspace.CreateFile("Tài liệu.txt"));

        // Điều khẳng định là **cơ chế khớp**: hai loại mục này sẽ không đi vào bước xác định quyền sở hữu cửa sổ.
        // Không mở thật chúng -- mở thư mục sẽ bật cửa sổ Explorer, mà cửa sổ do kiểm thử mở ra
        // chính là chỗ liên tục phát sinh vấn đề trong lượt này (cửa sổ sót lại, hộp thoại "Vị trí không khả dụng" sau khi thư mục bị xóa).
        // Việc "quả thật mở được" được nghiệm thu chạy xác định bằng charmap và kiểm tra thủ công bao phủ.
        Assert.False(WindowMatcher.CanMatchByImage(folder));
        Assert.False(WindowMatcher.CanMatchByImage(document));

        Assert.Empty(WindowMatcher.FindWindows(folder));
        Assert.Empty(WindowMatcher.FindWindows(document));
    }

    [Fact]
    public void YeuCauKichHoatMucKhongXacDinhBaoCaoThatBai()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var outcome = services.Launch.ActivateOrLaunch("id-khong-ton-tai");

        Assert.Equal(ActivationOutcome.Failed, outcome.Outcome);
    }

    [Fact]
    public void KichHoatCuaSoBangHandleBangKhongBaoCaoThatBaiThayViNemNgoaiLe()
    {
        var outcome = LaunchService.ActivateWindow(0);

        Assert.Equal(ActivationOutcome.Failed, outcome.Outcome);
    }

    [Fact]
    public void UrlKhongTinhLaMucTieuBienMat()
    {
        var entry = new AppEntry
        {
            Id = "x",
            Kind = AppItemKind.Url,
            Target = "https://ten-mien-nay-chac-chan-khong-ton-tai.example/",
        };

        Assert.False(LaunchService.IsTargetGone(entry));
    }

    private static AppEntry ImportOne(AppServiceHost services, string source)
    {
        var result = services.Import.ImportItems([new ImportRequest { Source = source }]);
        return Assert.Single(result.Added);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }
}

/// <summary>Tìm cửa sổ thuộc về một chương trình và đưa nó lên tiền cảnh.</summary>
public class WindowLocatorTests
{
    /// <summary>
    /// Dùng "Bảng ký tự" (Character Map) làm đối tượng kiểm thử.
    /// </summary>
    /// <remarks>
    /// Lý do chọn nó là: đây là một trong số ít chương trình Win32 cổ điển vẫn <strong>tự sở hữu cửa sổ</strong>.
    /// Đổi sang Notepad thì không được -- <c>System32\notepad.exe</c> hiện nay chỉ là một stub,
    /// chương trình thực sự mở cửa sổ là phiên bản đóng gói trong WindowsApps; còn chương trình console thì do
    /// <c>WindowsTerminal.exe</c> chứa. Cả hai loại đó đều không khớp được, xem
    /// giải thích về ranh giới ở bên dưới <see cref="WindowLocatorTests"/>.
    /// </remarks>
    private const string ClassicApp = @"C:\Windows\System32\charmap.exe";

    [Fact]
    public void TimCuaSoThuocChuongTrinhTheoDuongDanAnh()
    {
        if (!File.Exists(ClassicApp))
        {
            // Bỏ qua khi chạy trên máy khác không có chương trình này, thay vì đưa ra một lỗi giả.
            return;
        }

        // Trên màn hình của người dùng có thể đang tự mở Character Map. Lúc này không thể tắt nó (đó là cửa sổ của người dùng),
        // cũng không thể trộn trạng thái của nó vào cùng một kết luận -- bỏ qua kiểm thử này một cách trung thực.
        if (WindowLocator.FindWindowsForImage(ClassicApp).Count > 0)
        {
            return;
        }

        uint spawnedProcessId = 0;

        try
        {
            var launch = ShellExecutor.Open(ClassicApp);
            Assert.True(launch.Started, $"Shell từ chối khởi chạy chương trình đích, mã lỗi {launch.ErrorCode}");
            spawnedProcessId = launch.ProcessId;

            // Cửa sổ không xuất hiện ngay lập tức: đang chờ sự kiện thật từ bên ngoài, nên dùng vòng lặp thăm dò.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlyList<WindowCandidateInfo> found = [];

            while (DateTime.UtcNow < deadline)
            {
                found = WindowLocator.FindWindowsForImage(ClassicApp);

                if (found.Count > 0)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            Assert.NotEmpty(found);
            Assert.All(found, window => Assert.NotEqual(0, window.Handle));

            // Cửa sổ khớp được phải thực sự có tiêu đề: loại bỏ cửa sổ không có tiêu đề là đúng,
            // nếu không "chỉ có một cửa sổ" sẽ thành "có nhiều cửa sổ", giao diện sẽ bật danh sách chọn.
            Assert.All(found, window => Assert.False(string.IsNullOrWhiteSpace(window.Title)));
        }
        finally
        {
            // Chỉ dọn dẹp theo pid đã ghi nhận -- tuyệt đối không theo tên tiến trình (làm vậy sẽ tắt nhầm cửa sổ cùng tên của người dùng).
            KillRecordedProcess(spawnedProcessId);
        }
    }

    [Fact]
    public void KhongCoCuaSoKhopThiTraVeDanhSachRongThayViNemNgoaiLe()
    {
        Assert.Empty(WindowLocator.FindWindowsForImage(@"C:\hoan-toan khong-ton-tai\khong-ton-tai.exe"));
        Assert.Empty(WindowLocator.FindWindowsForImage(null));
        Assert.Empty(WindowLocator.FindWindowsForImage("   "));
        Assert.Empty(WindowLocator.FindWindowsForImage("Chỉ là một cái tên"));
    }

    [Fact]
    public void CuaSoDesktopKhongDuocTinhLaUngVien()
    {
        // Progman (màn hình nền) hiển thị, có tiêu đề, không có cửa sổ chủ, nhưng nó mang WS_EX_TOOLWINDOW,
        // vì vậy bắt buộc phải bị loại trừ. Nếu không loại trừ, mục Explorer sẽ khớp trúng cửa sổ màn hình nền,
        // người dùng bấm vào dock một cái là bị chuyển về màn hình nền.
        var desktop = WindowLocator.FindWindowsForImage(@"C:\Windows\explorer.exe");

        Assert.DoesNotContain(desktop, window => window.Title.Contains("Program Manager", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleBangKhongVaHandleKhongHopLeDeuThatBaiImLang()
    {
        Assert.False(WindowLocator.TryBringToForeground(0));
        Assert.False(WindowLocator.TryBringToForeground(new nint(0x1234)));
    }

    [Fact]
    public void BaKetCucKhiKichHoatCuaSoDuocPhanBietRoRang()
    {
        // Sau khi bấm một dòng trong danh sách nhiều cửa sổ thì sẽ đi qua nhánh này, ba kết cục mang ý nghĩa khác nhau với người dùng:
        // gộp chung thành một câu "đã thất bại" thì người dùng không biết phải làm gì. Ở đây khóa chặt phân loại và mô tả từng trường hợp.
        var none = LaunchService.ActivateWindow(0);
        Assert.Equal(ActivationOutcome.Failed, none.Outcome);
        Assert.Contains("Chưa chỉ định cửa sổ", none.Message, StringComparison.Ordinal);

        // Danh sách được liệt kê từ vài giây trước: cửa sổ có thể đã bị người dùng đóng. Đây không phải "hệ thống từ chối".
        var gone = LaunchService.ActivateWindow(new nint(0x1234));
        Assert.Equal(ActivationOutcome.Failed, gone.Outcome);
        Assert.Contains("đã đóng", gone.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("thanh tác vụ", gone.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kết thúc tiến trình đích do chính bài kiểm thử tự khởi chạy và ghi nhận.
    /// </summary>
    /// <remarks>
    /// <strong>Tuyệt đối không dọn dẹp theo tên tiến trình.</strong><c>GetProcessesByName</c> sẽ tắt
    /// <em>tất cả</em> tiến trình mang tên đó cùng lúc, bao gồm cả Character Map do chính người dùng mở -- một bài kiểm thử không nên
    /// có tác dụng phụ như vậy. Chỉ nhận pid đã được ghi lại lúc khởi chạy.
    /// </remarks>
    private static void KillRecordedProcess(uint processId)
    {
        if (processId == 0)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);

            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Tiến trình đã không còn, hoặc không lấy được handle -- vốn dĩ nó do bài kiểm thử bật lên, thoát là đạt mục đích.
        }
    }
}

/// <summary>Quét menu Start.</summary>
public class StartMenuScannerTests
{
    [Fact]
    public void QuetMenuStartTrenMayVaLoaiBoTrungLap()
    {
        var scan = StartMenuScanner.Scan();

        // Hai thư mục gốc này chắc chắn tồn tại trên Windows; thiếu một thư mục chứng tỏ
        // bước lấy đường dẫn bằng SHGetKnownFolderPath đã gặp trục trặc.
        Assert.Equal(2, scan.Roots.Count);
        Assert.NotEmpty(scan.Shortcuts);

        // Loại bỏ trùng lặp chỉ có thể làm số lượng ít đi hoặc giữ nguyên, không thể nhiều lên.
        Assert.True(scan.Shortcuts.Count <= scan.ScannedFileCount,
            $"Ban đầu {scan.ScannedFileCount}, sau khi lọc trùng {scan.Shortcuts.Count}");

        // Các mục cùng tên trong cùng một nhóm chỉ được phép còn lại một mục.
        var keys = scan.Shortcuts
            .Select(shortcut => $"{shortcut.Group}\u0000{shortcut.Name}");

        Assert.Equal(scan.Shortcuts.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void KetQuaQuetKhongCoTenRong()
    {
        var scan = StartMenuScanner.Scan();

        Assert.DoesNotContain(scan.Shortcuts, shortcut => string.IsNullOrWhiteSpace(shortcut.Name));
        Assert.DoesNotContain(scan.Shortcuts, shortcut => string.IsNullOrWhiteSpace(shortcut.Path));
    }

    [Fact]
    public void TatCaLoiTatQuetDuocDeuTonTaiThucTe()
    {
        var scan = StartMenuScanner.Scan();

        // Kết quả quét phải có thể mang đi nhập trực tiếp, nên mọi đường dẫn đều phải là thật.
        Assert.All(scan.Shortcuts, shortcut => Assert.True(File.Exists(shortcut.Path), shortcut.Path));
    }

    [Fact]
    public void DaSoMucTrongMenuStartPhanGiaiDuocMucTieu()
    {
        var scan = StartMenuScanner.Scan();
        var sample = scan.Shortcuts.Take(30).ToList();

        var resolved = sample.Count(shortcut => ShortcutResolver.Resolve(shortcut.Path) is not null);

        // Không yêu cầu 100%: các lối tắt UWP và quảng cáo vốn dĩ không giải ra được đường dẫn tệp,
        // chúng cần đi theo nhánh "giữ lại .lnk". Nhưng phần lớn phải phân giải được.
        Assert.True(resolved >= sample.Count * 8 / 10,
            $"{sample.Count} mục nhưng chỉ phân giải được {resolved} mục");
    }
}
