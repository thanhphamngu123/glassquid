using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>
/// Mở mục trong thư viện ứng dụng và ưu tiên đưa cửa sổ lên trước nếu ứng dụng đã mở sẵn.
/// </summary>
/// <remarks>
/// Bốn loại mục đều đi qua cùng một đường dẫn shell, lý do xem tại <see cref="ShellExecutor"/>.
///
/// <para>
/// <b>Mục bị lỗi tuyệt đối không thất bại âm thầm.</b> Khi mục tiêu không còn nữa thì trả về "không tìm thấy mục tiêu" kèm giải thích dễ đọc,
/// thay vì ném ngoại lệ hoặc không làm gì cả — sau khi người dùng nhấn biểu tượng, họ phải nhận được một lời giải thích.
/// Đồng thời cũng không từ chối khởi chạy các mục khác chỉ vì trong thư viện có mục bị lỗi.
/// </para>
/// </remarks>
public sealed class LaunchService : ILaunchService
{
    private readonly AppLibraryStore _library;

    public LaunchService(AppLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>ID tiến trình thu được từ lần khởi chạy thành công gần nhất, để tính năng "kích hoạt nếu đang chạy" tái sử dụng trong cùng một phiên.</summary>
    public uint LastLaunchedProcessId { get; private set; }

    public LaunchResult LaunchItem(string itemId)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new LaunchResult
            {
                Succeeded = false,
                Failure = LaunchFailureReason.UnknownItem,
                Message = "Không tìm thấy mục này trong thư viện ứng dụng.",
            };
        }

        // Đường dẫn web không có khái niệm "tệp có tồn tại hay không", giao thẳng cho shell xử lý.
        if (entry.Kind == AppItemKind.Url)
        {
            return OpenWithShell(entry);
        }

        if (IsTargetGone(entry))
        {
            return new LaunchResult
            {
                Succeeded = false,
                Failure = LaunchFailureReason.TargetMissing,
                Message = $"Không tìm thấy mục tiêu ở vị trí cũ. Hãy tìm lại vị trí: {entry.Target}",
            };
        }

        return OpenWithShell(entry);
    }

    /// <summary>
    /// Ưu tiên đưa cửa sổ đã có lên phía trước; chỉ khởi chạy khi chưa có cửa sổ.
    /// </summary>
    /// <remarks>
    /// Việc khớp cửa sổ chia làm hai hướng, quy tắc xem tại <see cref="WindowMatcher"/>: mục có định danh tường minh
    /// (ứng dụng đóng gói và lối tắt UWP) được khớp theo định danh; tệp thực thi thông thường khớp theo đường dẫn ảnh tiến trình.
    /// Khi cả hai đều không áp dụng được thì khởi chạy bình thường — hành vi dự phòng xem giải thích tại <see cref="WindowMatcher"/>.
    /// </remarks>
    public ActivationResult ActivateOrLaunch(string itemId)
        => ActivateOrLaunch(itemId, minimizeIfForeground: false);

    /// <summary>Biểu tượng Dock có thể thu nhỏ cửa sổ tiền cảnh khớp duy nhất; cửa sổ nền hoặc đã thu nhỏ vẫn được đưa lên phía trước.</summary>
    public ActivationResult ActivateOrLaunch(string itemId, bool minimizeIfForeground)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "Không tìm thấy mục này trong thư viện ứng dụng.",
            };
        }

        if (entry.Kind == AppItemKind.Url)
        {
            var plain = LaunchItem(itemId);

            return plain.Succeeded
                ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = plain.Message };
        }

        if (IsTargetGone(entry))
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = $"Không tìm thấy mục tiêu ở vị trí cũ. Hãy tìm lại vị trí: {entry.Target}",
            };
        }

        var canMatch = !string.IsNullOrWhiteSpace(WindowMatcher.ResolveAumid(entry))
            || WindowMatcher.CanMatchByImage(entry);

        if (!canMatch)
        {
            var plain = LaunchItem(itemId);

            return plain.Succeeded
                ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = plain.Message };
        }

        var windows = WindowMatcher.FindWindows(entry);

        switch (windows.Count)
        {
            case 1:
                if (minimizeIfForeground
                    && windows[0].Handle == WindowControl.GetForegroundWindow()
                    && !WindowControl.IsMinimized(windows[0].Handle))
                {
                    _ = WindowControl.Minimize(windows[0].Handle);
                    return WindowControl.IsMinimized(windows[0].Handle)
                        ? new ActivationResult { Outcome = ActivationOutcome.Minimized }
                        : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = "Không thể thu nhỏ cửa sổ này. Hãy thử lại." };
                }

                return Raise(new WindowCandidateInfo(windows[0].Handle, windows[0].Title, windows[0].ProcessId, windows[0].IsMinimized));

            case > 1:
                return new ActivationResult
                {
                    Outcome = ActivationOutcome.MultipleWindowsFound,
                    Candidates = windows
                        .Select(window => new WindowCandidate { Handle = window.Handle, Title = window.Title })
                        .ToList(),
                };

            default:
                var launched = OpenWithShell(entry);

                return launched.Succeeded
                    ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                    : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = launched.Message };
        }
    }

    /// <summary>
    /// Đưa một cửa sổ trong nhóm các cửa sổ ứng viên lên phía trước. Dùng cho danh sách chọn nhiều cửa sổ.
    /// </summary>
    /// <remarks>
    /// Ba kết cục được phân tách rõ ràng vì chúng mang ý nghĩa khác nhau đối với người dùng:
    /// cửa sổ đã đóng (danh sách được liệt kê vài giây trước), hệ thống từ chối đưa lên trước (có thể để người dùng chuyển từ thanh tác vụ), thành công.
    /// Gộp chung thành một câu "thất bại" sẽ khiến người dùng không biết phải làm gì.
    /// </remarks>
    public static ActivationResult ActivateWindow(nint handle)
    {
        if (handle == 0)
        {
            return new ActivationResult { Outcome = ActivationOutcome.Failed, Message = "Chưa chỉ định cửa sổ." };
        }

        if (!WindowControl.IsAlive(handle))
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "Cửa sổ đó đã đóng. Hãy nhấn biểu tượng lần nữa.",
            };
        }

        return WindowLocator.TryBringToForeground(handle)
            ? new ActivationResult { Outcome = ActivationOutcome.Activated }
            : new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "Windows không cho phép đưa cửa sổ lên trước. Hãy chuyển sang cửa sổ đó từ thanh tác vụ.",
            };
    }

    /// <summary>Kiểm tra mục tiêu hiện còn tồn tại hay không.</summary>
    public static bool IsTargetGone(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind == AppItemKind.Url || string.IsNullOrWhiteSpace(entry.Target))
        {
            return false;
        }

        try
        {
            return !File.Exists(entry.Target) && !Directory.Exists(entry.Target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không đọc được không đồng nghĩa với không tồn tại — có thể là vấn đề phân quyền. Xử lý như "tồn tại",
            // để quá trình khởi chạy chạm tới lỗi thực tế, thay vì chặn trước ở đây và đưa ra kết luận sai.
            return false;
        }
    }

    private static ActivationResult Raise(WindowCandidateInfo window)
    {
        if (WindowLocator.TryBringToForeground(window.Handle))
        {
            return new ActivationResult { Outcome = ActivationOutcome.Activated };
        }

        return new ActivationResult
        {
            Outcome = ActivationOutcome.Failed,
            Message = "Đã tìm thấy cửa sổ đang chạy, nhưng Windows không cho phép đưa cửa sổ lên trước. Hãy chuyển sang cửa sổ đó từ thanh tác vụ.",
        };
    }

    private LaunchResult OpenWithShell(AppEntry entry)
    {
        var outcome = ShellExecutor.Open(entry.Target, entry.Arguments, entry.WorkingDirectory);

        if (outcome.Started)
        {
            LastLaunchedProcessId = outcome.ProcessId;
            return new LaunchResult { Succeeded = true };
        }

        return new LaunchResult
        {
            Succeeded = false,
            Failure = Classify(outcome),
            Message = Describe(outcome, entry),
        };
    }

    private static LaunchFailureReason Classify(ShellLaunchOutcome outcome) => outcome switch
    {
        { IsFileNotFound: true } => LaunchFailureReason.TargetMissing,
        { IsAccessDenied: true } => LaunchFailureReason.AccessDenied,
        { IsNoAssociation: true } => LaunchFailureReason.Unsupported,
        _ => LaunchFailureReason.ShellRejected,
    };

private static string Describe(ShellLaunchOutcome outcome, AppEntry entry)
    {
        var explanation = outcome switch
        {
            { IsFileNotFound: true } => $"Không tìm thấy mục tiêu: {entry.Target}",
            { IsAccessDenied: true } => "Windows đã từ chối mở mục tiêu này. Có thể cần quyền quản trị viên hoặc yêu cầu nâng quyền đã bị hủy.",
            { IsNoAssociation: true } => $"Windows không biết cách mở loại tệp này: {entry.Target}",
            _ => "Windows không thể hoàn tất thao tác mở.",
        };

        // Trả về kèm mã lỗi gốc: mã này có thể tra cứu được ý nghĩa cụ thể, còn câu "shell đã từ chối" thì không.
        // Khi điều tra loại sự cố này, đây là manh mối hữu ích duy nhất.
        return $"{explanation}(Mã lỗi Windows: {outcome.ErrorCode})";
    }
}
