using Glassquid.Core.Models;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>
/// Bộ phân định duy nhất cho câu hỏi "những cửa sổ đang chạy nào thuộc về bản ghi này".
/// </summary>
/// <remarks>
/// Chiến lược đối khớp được chốt theo kết luận đo thực tế của Đầu dò giai đoạn 4 (bằng chứng: <c>docs/evidence/giai-doan-4-ket-qua-tham-do.txt</c>):
///
/// <list type="number">
/// <item>Mục có định danh tường minh (đọc từ .lnk lúc nhập) → đối khớp theo <strong>định danh hiệu lực</strong> của cửa sổ;
/// điều này bao phủ ứng dụng đóng gói (định danh cửa sổ lấy từ định danh gói của tiến trình sở hữu) và UWP (định danh cửa sổ gắn trên
/// cửa sổ khung của ApplicationFrameHost). Cửa sổ có định danh hiệu lực khác nhau đều không tính —
/// hai ứng dụng UWP dùng chung một tiến trình chủ, nếu ép khớp theo đường dẫn tiến trình chủ sẽ
/// nhận nhầm cửa sổ của ứng dụng A thành ứng dụng B.</item>
/// <item>Mục không có định danh, đích là tệp thực thi → đối khớp theo đường dẫn ảnh tiến trình (lộ trình đã xác minh ở giai đoạn 3).</item>
/// <item>Cả hai đều không áp dụng → coi là "không có cửa sổ đang chạy", khi bấm vào sẽ khởi chạy bình thường.
/// Hành vi thoái hóa được ghi trung thực vào báo cáo bàn giao: ứng dụng đơn thể tự đưa cửa sổ có sẵn lên trước,
/// chương trình đa thể (như cmd mở qua trình chủ bảng điều khiển) sẽ mở thêm một cửa sổ mới.</item>
/// </list>
///
/// <para>
/// Bản thân việc đối khớp là hàm thuần túy trên một <strong>ảnh chụp cửa sổ</strong> (<see cref="MatchWindows"/>),
/// nên có thể kiểm thử đơn vị trong môi trường không có cửa sổ; truy vấn trực tiếp chỉ là tổ hợp "liệt kê + đối khớp ảnh chụp".
/// </para>
/// </remarks>
public static class WindowMatcher
{
    /// <summary>
    /// Tìm các cửa sổ đang chạy lúc này của bản ghi này trong ảnh chụp cho trước.
    /// </summary>
    /// <param name="entry">Bản ghi thư viện ứng dụng, không được null.</param>
    /// <param name="snapshot">Ảnh chụp cửa sổ ứng viên, thường lấy từ một lần gọi
    /// <see cref="WindowLocator.EnumerateTopLevelWindows"/>.</param>
    /// <param name="aumid">
    /// Định danh hiệu lực của mục; khi là null sẽ đối khớp theo đường dẫn ảnh. Bên gọi có thể dùng
    /// <see cref="ResolveAumid"/> để phân giải.
    /// </param>
    public static IReadOnlyList<WindowIdentityInfo> MatchWindows(
        AppEntry entry,
        IReadOnlyList<WindowIdentityInfo> snapshot,
        string? aumid)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(aumid))
        {
            return MatchByIdentity(snapshot, aumid);
        }

        if (CanMatchByImage(entry))
        {
            var target = NormalizePath(entry.Target);
            return snapshot
                .Where(window => string.Equals(window.ImagePath, target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return [];
    }

    /// <summary>Truy vấn trực tiếp: liệt kê cửa sổ hiện tại rồi đối khớp. Dùng cho kịch bản bấm một lần.</summary>
    /// <param name="entry">Bản ghi thư viện ứng dụng.</param>
    /// <param name="identityCache">
    /// Bộ nhớ đệm trong phiên cho định danh mục; các mục có đích vẫn là .lnk sẽ đọc định danh tường minh một lần theo nhu cầu.
    /// Truyền null thì mỗi lần đọc trực tiếp.
    /// </param>
    public static IReadOnlyList<WindowIdentityInfo> FindWindows(
        AppEntry entry,
        IDictionary<string, string?>? identityCache = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return MatchWindows(entry, WindowLocator.EnumerateTopLevelWindows(), ResolveAumid(entry, identityCache));
    }

    /// <summary>
    /// Phân giải định danh hiệu lực của mục: ưu tiên dùng giá trị đã lưu lúc nhập, khi đích vẫn là .lnk thì đọc trực tiếp theo nhu cầu.
    /// </summary>
    public static string? ResolveAumid(AppEntry entry, IDictionary<string, string?>? identityCache = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.AppUserModelId))
        {
            return entry.AppUserModelId;
        }

        if (!entry.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (identityCache is not null && identityCache.TryGetValue(entry.Id, out var cached))
        {
            return cached;
        }

        var resolved = WindowIdentity.TryGetShortcutAumid(entry.Target);

        if (identityCache is not null)
        {
            identityCache[entry.Id] = resolved;
        }

        return resolved;
    }

    /// <summary>
    /// Lọc cửa sổ theo định danh. Khi định danh hiệu lực của cửa sổ <strong>không khớp</strong> với định danh mục thì không tính là khớp,
    /// cũng không lùi về đường dẫn ảnh — đây chính là điểm thực thi quy tắc "cả hai phía đều có định danh nhưng không khớp, không được ép khớp theo đường dẫn tiến trình chủ".
    /// </summary>
    private static List<WindowIdentityInfo> MatchByIdentity(
        IReadOnlyList<WindowIdentityInfo> windows,
        string aumid)
    {
        var matches = new List<WindowIdentityInfo>();

        foreach (var window in windows)
        {
            if (string.Equals(window.EffectiveAumid, aumid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(window);
            }

            // Các cửa sổ đi tới đây có định danh rỗng hoặc khác. Cửa sổ có định danh khác dù đường dẫn ảnh tình cờ bằng
            // đích cũng không tính — định danh là do ứng dụng tự khai báo, khai báo không khớp thì không đáng tin.
            // Cửa sổ có định danh rỗng không thuộc diện này: các chương trình Win32 cổ điển cả hai phía đều không đọc được định danh,
            // quyền sở hữu của chúng vẫn do đường dẫn ảnh quyết định (nhánh thứ hai của MatchWindows).
        }

        return matches;
    }

    /// <summary>
    /// Bản ghi này có thể đối khớp cửa sổ theo ảnh tiến trình hay không.
    /// </summary>
    /// <remarks>
    /// Chỉ đối khớp khi đích là tệp thực thi. Các đích như <c>.bat</c> bản thân là tập lệnh,
    /// tiến trình thực sự chạy là cmd.exe, đem đường dẫn của nó ra so sánh sẽ không bao giờ khớp được,
    /// dẫn tới mỗi lần bấm lại mở thêm một cái — thay vì đối khớp vô ích như vậy, thà cứ khởi chạy thẳng mỗi lần bấm.
    /// </remarks>
    public static bool CanMatchByImage(AppEntry entry)
        => entry.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Thu gọn về dạng có thể so sánh được. Nhất quán với quy tắc của <see cref="WindowLocator"/>.</summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }
}
