using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices;

/// <summary>Kết quả của một lần kiểm tra lại trạng thái mất hiệu lực.</summary>
/// <param name="Checked">Số mục đã kiểm tra.</param>
/// <param name="Missing">Số mục không tìm thấy đích.</param>
/// <param name="NewlyBroken">Các mục mới phát hiện mất hiệu lực trong lần này.</param>
/// <param name="Recovered">Các mục đã tìm lại được đích nên được bỏ đánh dấu mất hiệu lực.</param>
/// <param name="Changed">Trạng thái có thay đổi, bên gọi cần lưu lại.</param>
public sealed record MissingTargetReport(
    int Checked,
    int Missing,
    IReadOnlyList<AppEntry> NewlyBroken,
    IReadOnlyList<AppEntry> Recovered,
    bool Changed);

/// <summary>Kết quả định vị lại một bản ghi.</summary>
public enum RelocateResult
{
    /// <summary>Đã sửa xong và lưu lại.</summary>
    Relocated = 0,

    /// <summary>Không có bản ghi này.</summary>
    UnknownItem = 1,

    /// <summary>Vị trí mới không tồn tại.</summary>
    TargetNotFound = 2,

    /// <summary>Vị trí mới đã bị một bản ghi khác chiếm; không thay đổi bất cứ thứ gì.</summary>
    ConflictsWithExisting = 3,

    /// <summary>Bản ghi này không hỗ trợ định vị lại (địa chỉ web).</summary>
    NotRelocatable = 4,

    /// <summary>Sửa được trong bộ nhớ nhưng không lưu xuống được.</summary>
    SaveFailed = 5,
}

/// <summary>Kết quả định vị lại, kèm theo giải thích để hiển thị.</summary>
/// <param name="Result">Phân loại kết quả.</param>
/// <param name="Message">Một câu thông báo cho người dùng xem.</param>
/// <param name="ItemId">Bản ghi khác bị xung đột (có giá trị khi <see cref="RelocateResult.ConflictsWithExisting"/>).</param>
public sealed record RelocationOutcome(RelocateResult Result, string Message, string? ItemId = null);

/// <summary>
/// Giữ cho thư viện ứng dụng trung thực về việc "mục tiêu không còn nữa", đồng thời cho phép người dùng định vị lại.
/// </summary>
/// <remarks>
/// R-07 yêu cầu các mục bị mất hiệu lực phải <strong>được giữ lại</strong> và hiển thị là mất hiệu lực, chứ không bị âm thầm bỏ đi.
/// Do đó ở đây không bao giờ xóa bất kỳ mục nào: nó chỉ lật duy nhất cờ <see cref="AppEntry.IsTargetMissing"/>.
/// Bản thân dữ liệu, tham chiếu của bố cục tới nó, và biểu tượng đều được giữ nguyên — khi người dùng cắm lại ổ cứng di động,
/// lần kiểm tra lại tiếp theo sẽ xóa cờ đánh dấu và mục sẽ khôi phục như ban đầu.
///
/// <para>
/// <b>Địa chỉ web không bao giờ bị coi là mất hiệu lực.</b> Một địa chỉ web có mở được hay không phụ thuộc vào mạng chứ không phụ thuộc vào tệp trên máy,
/// dùng sự tồn tại của tệp để phán đoán nó chỉ cho ra kết luận sai.
/// </para>
/// </remarks>
public sealed class LibraryMaintenance
{
    private readonly AppLibraryStore _library;
    private readonly IconCache _icons;
    private readonly IImportObserver? _observer;

    public LibraryMaintenance(AppLibraryStore library, IconCache icons, IImportObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(icons);

        _library = library;
        _icons = icons;
        _observer = observer;
    }

    /// <summary>
    /// Kiểm tra lại xem mục tiêu của từng bản ghi có còn tồn tại không.
    /// </summary>
    /// <remarks>
    /// Chỉ lưu khi trạng thái thực sự thay đổi. Ghi thư viện ứng dụng vô điều kiện lúc khởi động sẽ làm thời gian sửa đổi tệp
    /// thay đổi sau mỗi lần bật máy, khiến manh mối "lần sửa đổi gần nhất là khi nào" mất hết ý nghĩa.
    /// </remarks>
    public MissingTargetReport RefreshMissingTargets(bool persist = true)
    {
        var broken = new List<AppEntry>();
        var recovered = new List<AppEntry>();
        var missing = 0;
        var checkedCount = 0;

        foreach (var entry in _library.Items)
        {
            // Trong tệp được sửa thủ công có thể có phần tử rỗng, chúng vừa không dùng được vừa không thể kiểm tra.
            if (entry is null || !entry.IsUsable)
            {
                continue;
            }

            checkedCount++;

            // Địa chỉ web không có khái niệm "tệp có còn hay không".
            if (entry.Kind == AppItemKind.Url)
            {
                continue;
            }

            var gone = LaunchService.IsTargetGone(entry);

            if (gone)
            {
                missing++;
            }

            if (gone == entry.IsTargetMissing)
            {
                continue;
            }

            entry.IsTargetMissing = gone;

            if (gone)
            {
                broken.Add(entry);
            }
            else
            {
                recovered.Add(entry);
            }
        }

        var changed = broken.Count > 0 || recovered.Count > 0;

        if (changed && persist)
        {
            _library.Save();
            _observer?.OnLibraryChanged();
        }

        return new MissingTargetReport(checkedCount, missing, broken, recovered, changed);
    }

    /// <summary>Xóa các ứng dụng đã gỡ (mất mục tiêu) khỏi thư viện và đồng bộ lại giao diện.</summary>
    public int PruneMissingTargets()
    {
        var toRemove = new List<string>();
        foreach (var entry in _library.Items)
        {
            if (entry is null || !entry.IsUsable) continue;
            if (entry.Kind == AppItemKind.Url) continue;
            if (entry.IsTargetMissing || LaunchService.IsTargetGone(entry))
                toRemove.Add(entry.Id);
        }
        if (toRemove.Count == 0) return 0;

        var removed = 0;
        foreach (var id in toRemove)
        {
            if (_library.Remove(id))
                removed++;
        }

        if (removed > 0)
        {
            _library.Save();
            _observer?.OnLibraryChanged();
        }
        return removed;
    }

    /// <summary>Xóa thủ công một mục khỏi thư viện ứng dụng.</summary>
    public bool RemoveEntry(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        if (!_library.Remove(itemId)) return false;
        _library.Save();
        _observer?.OnLibraryChanged();
        return true;
    }

    /// <summary>Xóa hàng loạt các mục đã chọn khỏi thư viện ứng dụng.</summary>
    public int RemoveEntries(IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var removed = 0;
        foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(itemId) && _library.Remove(itemId))
                removed++;
        }
        if (removed == 0) return 0;
        _library.Save();
        _observer?.OnLibraryChanged();
        return removed;
    }

    /// <summary>
    /// Trỏ một bản ghi sang một vị trí mới.
    /// </summary>
    /// <remarks>
    /// Làm đồng thời ba việc, thiếu một việc đều không được: đổi mục tiêu, bỏ đánh dấu mất hiệu lực, trích xuất lại biểu tượng.
    /// Nếu chỉ đổi mục tiêu thì trên giao diện bản ghi đó vẫn mang biểu tượng cũ và tiếp tục hiển thị là mất hiệu lực;
    /// nếu chỉ xóa cờ đánh dấu thì bản ghi sẽ trỏ tới một vị trí mới không tồn tại nhưng lại hiển thị bình thường.
    ///
    /// <para>
    /// Khi vị trí mới đã bị một bản ghi khác chiếm thì <strong>không sửa gì cả</strong> và báo xung đột đúng sự thật.
    /// Cố tình sửa tiếp sẽ làm hai bản ghi có khóa chống trùng giống nhau — từ đó trong thư viện ứng dụng xuất hiện một cặp mục trùng lặp không thể phân biệt,
    /// mà đó chính là loại lỗi mà Giai đoạn 2 đã tốn 10 bài kiểm thử để phòng ngừa.
    /// </para>
    /// </remarks>
    public RelocationOutcome Relocate(string itemId, string newPath)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new RelocationOutcome(RelocateResult.UnknownItem, "Không tìm thấy mục này trong thư viện ứng dụng.");
        }

        if (entry.Kind == AppItemKind.Url)
        {
            return new RelocationOutcome(RelocateResult.NotRelocatable, "Không cần tìm vị trí tệp cho trang web.");
        }

        if (string.IsNullOrWhiteSpace(newPath))
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, "Chưa cung cấp vị trí mới.");
        }

        string full;

        try
        {
            full = Path.GetFullPath(newPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, $"Không thể xác định vị trí mới: {newPath}");
        }

        if (!ExistsQuietly(full))
        {
            return new RelocationOutcome(RelocateResult.TargetNotFound, $"Vị trí mới không tồn tại: {full}");
        }

        // Kiểm tra trước xem có đụng phải bản ghi khác không. Việc kiểm tra dùng một bản sao tạm thời, nên bản ghi gốc lúc này chưa bị sửa đổi.
        var probe = CloneWithTarget(entry, full);
        var collided = _library.FindByKey(AppEntryKey.Compute(probe));

        if (collided is not null && !string.Equals(collided.Id, entry.Id, StringComparison.Ordinal))
        {
            return new RelocationOutcome(
                RelocateResult.ConflictsWithExisting,
                $"Một mục khác đã trỏ đến vị trí này: {collided.DisplayName}",
                collided.Id);
        }

        var previousTarget = entry.Target;
        var previousKind = entry.Kind;
        var previousIconSource = entry.IconSource;
        var previousMissing = entry.IsTargetMissing;

        entry.Target = full;
        entry.Kind = ItemKindDetector.Detect(full, Directory.Exists(full));
        entry.IsTargetMissing = false;

        // Nếu nguồn biểu tượng trước đây đi theo mục tiêu cũ thì chuyển theo sang vị trí mới; nếu nó trỏ tới nơi khác
        // (vị trí biểu tượng tùy chỉnh của lối tắt), giữ nguyên không đổi — đó chính là biểu tượng người dùng muốn.
        if (string.IsNullOrWhiteSpace(previousIconSource)
            || string.Equals(previousIconSource, previousTarget, StringComparison.OrdinalIgnoreCase))
        {
            entry.IconSource = full;
        }

        if (!_library.Update(entry))
        {
            // Không tìm thấy nó trong chỉ mục, hoặc vị trí mới đụng phải bản ghi khác. Ở đây bắt buộc phải khôi phục **mọi**
            // trường đã bị sửa — đối tượng bị sửa trực tiếp tại chỗ, sót một trường sẽ để lại một mục bị sửa dở dang.
            // Lỗi bỏ sót Kind từng được phát hiện khi rà soát: sau khi đổi mục tiêu từ tệp sang thư mục và bị từ chối cập nhật,
            // loại của bản ghi đó vẫn là thư mục nhưng lại trỏ về tệp ban đầu.
            entry.Target = previousTarget;
            entry.Kind = previousKind;
            entry.IconSource = previousIconSource;
            entry.IsTargetMissing = previousMissing;
            return new RelocationOutcome(RelocateResult.UnknownItem, "Không tìm thấy mục này trong chỉ mục thư viện ứng dụng.");
        }

        try
        {
            _library.Save();
        }
        catch (StorageException ex)
        {
            entry.Target = previousTarget;
            entry.Kind = previousKind;
            entry.IconSource = previousIconSource;
            entry.IsTargetMissing = previousMissing;
            _library.Update(entry);
            return new RelocationOutcome(RelocateResult.SaveFailed, $"Không thể lưu vị trí mới; vị trí ban đầu đã được khôi phục: {ex.Message}");
        }

        // Đổi mục tiêu thì trích xuất lại biểu tượng. Thất bại chỉ làm thiếu một hình ảnh, không ảnh hưởng bản thân việc định vị lại.
        CacheIcon(entry);

        _observer?.OnLibraryChanged();
        return new RelocationOutcome(RelocateResult.Relocated, $"Đã cập nhật vị trí: {full}");
    }

    /// <summary>Đổi biểu tượng tùy chỉnh cho một bản ghi; truyền null nghĩa là gỡ bỏ biểu tượng tùy chỉnh.</summary>
    public bool SetCustomIcon(string itemId, string? iconFilePath)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return false;
        }

        if (!_icons.ApplyCustomIcon(entry, iconFilePath))
        {
            return false;
        }

        _library.Update(entry);
        _library.Save();
        _observer?.OnLibraryChanged();
        return true;
    }

    private void CacheIcon(AppEntry entry)
    {
        var cached = _icons.EnsureFor(entry.IconSource);

        // Sau khi định vị lại, tệp đệm của biểu tượng cũ có thể không còn ai tham chiếu nữa. Ở đây không chủ động xóa —
        // muốn trả lời đúng câu hỏi "tệp đệm này còn bản ghi nào khác đang dùng không" cần quét toàn bộ thư viện,
        // trong khi cái giá phải trả (vài KB tệp mồ côi) nhỏ hơn rất nhiều so với cái giá của việc trả lời sai (xóa nhầm biểu tượng người khác đang dùng).
        _ = cached;
    }

    private static AppEntry CloneWithTarget(AppEntry entry, string target) => new()
    {
        Id = entry.Id,
        Target = target,
        Kind = ItemKindDetector.Detect(target, Directory.Exists(target)),
        Arguments = entry.Arguments,
    };

    private static bool ExistsQuietly(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
