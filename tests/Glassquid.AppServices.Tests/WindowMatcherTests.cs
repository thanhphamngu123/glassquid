using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices.Tests;

/// <summary>
/// Kiểm thử đơn vị cho quy tắc khớp quyền sở hữu cửa sổ: khớp dựa trên <strong>ảnh chụp nhanh cửa sổ giả lập</strong>,
/// không tạo bất kỳ cửa sổ thật nào, nhờ vậy chạy nhanh và ổn định.
/// Mối tương ứng giữa quy tắc và danh tính cửa sổ/tiến trình thật được bảo chứng bởi kết quả đo thực tế của đầu dò ở giai đoạn 4
/// (xem <c>docs/evidence/phase4-probe-output.txt</c>),
/// còn hành vi đầu-cuối thật được bao phủ bởi <c>--phase4-acceptance</c>.
/// </summary>
public class WindowMatcherTests
{
    private static WindowIdentityInfo Window(
        nint handle,
        string? imagePath = null,
        string? windowAumid = null,
        string? processAumid = null,
        string title = "Cửa sổ")
        => new(handle, title, 1000 + (uint)handle, false, imagePath, windowAumid, processAumid);

    private static AppEntry ProgramEntry(string target, string? aumid = null) => new()
    {
        Id = "e1",
        Kind = AppItemKind.Program,
        Target = target,
        AppUserModelId = aumid,
    };

    [Fact]
    public void ClassicProgramMatchesByImagePathCaseInsensitive()
    {
        var entry = ProgramEntry(@"C:\Windows\System32\CharMap.EXE");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\WINDOWS\SYSTEM32\charmap.exe"),
            Window(2, imagePath: @"C:\Windows\explorer.exe"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void PackagedAppMatchesByProcessIdentity()
    {
        // Đo thực tế từ đầu dò: danh tính cửa sổ của Notepad bản đóng gói bị trống, danh tính nằm trên tiến trình sở hữu.
        var entry = ProgramEntry(@"C:\Windows\System32\notepad.exe", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Program Files\WindowsApps\...\Notepad.exe",
                processAumid: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void UwpFrameWindowMatchesByWindowIdentity()
    {
        // Đo thực tế từ đầu dò: cửa sổ khung của UWP được lưu trú trên ApplicationFrameHost,
        // danh tính riêng của ứng dụng được gắn trong kho thuộc tính cửa sổ, bản thân tiến trình chủ không có danh tính.
        var entry = new AppEntry
        {
            Id = "uwp",
            Kind = AppItemKind.Shortcut,
            Target = @"C:\ViDu\CaiDat.lnk",
            AppUserModelId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        };

        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Windows\System32\ApplicationFrameHost.exe",
                windowAumid: "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void WindowWithMismatchedIdentityDoesNotMatchOrFallbackToImagePath()
    {
        // Nguồn gốc quy tắc: hai ứng dụng UWP dùng chung một tiến trình chủ,
        // nếu cố khớp theo đường dẫn tiến trình chủ sẽ nhận nhầm cửa sổ của ứng dụng A thành ứng dụng B.
        var entry = ProgramEntry(@"C:\ViDu\MucTieu.exe", "com.app.a");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\ViDu\TienTrinhChu.exe", windowAumid: "com.app.b"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        Assert.Empty(matches);
    }

    [Fact]
    public void ClassicPathIsUnaffectedByIdentityRulesWhenNoIdentityExists()
    {
        // Mục không có danh tính (exe thông thường), còn cửa sổ có danh tính khác hoặc không có danh tính —
        // đều xét theo đường dẫn ảnh, hành vi giai đoạn 3 được giữ nguyên.
        var entry = ProgramEntry(@"C:\tools\myapp.exe");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\tools\myapp.exe", processAumid: "com.other.app"),
            Window(2, imagePath: @"C:\tools\myapp.exe"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void ScriptTargetDoesNotParticipateInImageMatching()
    {
        // Thực thể thật của .bat là cmd.exe, khớp theo đường dẫn kịch bản sẽ không bao giờ khớp được;
        // thay vì lần nào cũng nhận sai thì cứ khởi chạy thẳng (hành vi thoái lui xem chú thích ở WindowMatcher).
        var entry = new AppEntry
        {
            Id = "bat",
            Kind = AppItemKind.Program,
            Target = @"C:\tools\run.bat",
        };
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\tools\run.bat"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        Assert.Empty(matches);
    }

    [Fact]
    public void ReadsIdentityOnDemandWhenTargetIsLnkAndStoredIdentityIsEmpty()
    {
        // Ca kiểm thử này chỉ dùng "đường dẫn bộ đệm" để xác minh ngữ nghĩa bộ đệm trong phiên, không chạm vào .lnk thật:
        // sau khi hàm phân giải được gọi một lần, lần thứ hai sẽ lấy trực tiếp giá trị trong bộ đệm.
        var entry = new AppEntry
        {
            Id = "lnk",
            Kind = AppItemKind.Shortcut,
            Target = @"C:\KhongTonTai\ViDu.lnk",
        };
        var cache = new Dictionary<string, string?> { ["lnk"] = "com.cached.identity" };

        var resolved = WindowMatcher.ResolveAumid(entry, cache);

        Assert.Equal("com.cached.identity", resolved);
    }

    [Fact]
    public void StoredIdentityTakesPrecedenceWithoutReadingFile()
    {
        var entry = ProgramEntry(@"C:\ViDu\MucTieu.exe", "com.stored.identity");

        var resolved = WindowMatcher.ResolveAumid(entry, identityCache: null);

        Assert.Equal("com.stored.identity", resolved);
    }
}
