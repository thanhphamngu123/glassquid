using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices;

/// <summary>
/// Tiếp nhận các mục người dùng đưa vào và lưu vào thư viện ứng dụng dùng chung.
/// </summary>
/// <remarks>
/// Ba nguyên tắc bắt buộc:
/// <list type="number">
/// <item><b>Báo cáo từng mục, không thất bại cả lô.</b> Khi nhập hai mươi tệp cùng lúc mà một tệp không có quyền truy cập,
/// mười chín tệp còn lại vẫn vào thư viện bình thường, còn mục thất bại sẽ xuất hiện kèm lý do trong kết quả.</item>
/// <item><b>Không hoàn tác dữ liệu đã có.</b> Lỗi chỉ ảnh hưởng tới đúng mục bị lỗi;
/// các mục đã được thêm vào thư viện hoặc có sẵn trước khi nhập đều không bị ảnh hưởng.</item>
/// <item><b>Chỉ ghi tệp một lần.</b> Khi nhập hàng loạt, hệ thống đọc thư viện ngay từ đầu rồi xử lý xong tất cả mới lưu.
/// Lưu mỗi lần nhập một mục vừa chậm vừa dễ để lại nửa lô dở dang nếu gặp lỗi giữa chừng.</item>
/// </list>
///
/// <para>
/// Lỗi trích xuất biểu tượng <strong>không tính là lỗi nhập</strong>: giao diện thiếu một hình ảnh chỉ là vấn đề hiển thị,
/// bản thân mục đó vẫn dùng được nên không thể vì thế mà từ chối mục của người dùng.
/// </para>
/// </remarks>
public sealed class ImportService : IImportService
{
    private readonly AppLibraryStore _library;
    private readonly IconCache _icons;
    private readonly IImportObserver? _observer;
    private readonly LayoutStore? _layouts;
    private readonly Func<int> _launcherCapacity;

    public ImportService(AppLibraryStore library, IconCache icons, IImportObserver? observer = null)
        : this(library, icons, observer, null)
    {
    }

    public ImportService(AppLibraryStore library, IconCache icons, IImportObserver? observer,
        LayoutStore? layouts, Func<int>? launcherCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(icons);

        _library = library;
        _icons = icons;
        _observer = observer;
        _layouts = layouts;
        _launcherCapacity = launcherCapacity ?? (() => 35);
    }

    /// <summary>Số tệp biểu tượng thực tế đã ghi trong phiên này, giúp báo cáo chi phí của lần nhập.</summary>
    public int IconsWritten { get; private set; }

    /// <summary>Số lần tái sử dụng tệp biểu tượng sẵn có trong phiên này.</summary>
    public int IconsReused { get; private set; }

    public ImportResult ImportItems(IReadOnlyList<ImportRequest> requests)
        => ImportItems(requests, addExistingEntriesToLauncher: true);

    /// <summary>
    /// Import a batch, optionally keeping already-known entries out of the launcher layout.
    /// Automatic discovery uses this mode so a repeated scan does not restore shortcuts the
    /// user deliberately removed from the launcher.
    /// </summary>
    public ImportResult ImportItems(IReadOnlyList<ImportRequest> requests, bool addExistingEntriesToLauncher)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var added = new List<AppEntry>();
        var duplicates = new List<AppEntry>();
        var failures = new List<ImportFailure>();

        if (requests.Count == 0)
        {
            return new ImportResult();
        }

        IconsWritten = 0;
        IconsReused = 0;

        foreach (var request in requests)
        {
            if (request is null)
            {
                continue;
            }

            try
            {
                ImportOne(request, added, duplicates, failures);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Ngoại lệ ở một mục không được kéo theo cả lô. Khối này bắt các tình huống ngoài dự kiến
                // ở một nguồn đơn lẻ — thiếu quyền, bị xóa giữa chừng hoặc shell trả về mã lỗi lạ.
                // Trường hợp này thuộc về “báo cáo từng mục”, không phải “thất bại cả lô”:
                // nếu người dùng kéo vào một lô tệp có một tệp hỏng, 19 tệp còn lại vẫn phải vào thư viện bình thường.
                failures.Add(new ImportFailure
                {
                    Source = request.Source ?? string.Empty,
                    Reason = ImportFailureReason.Other,
                    Message = $"Đã xảy ra lỗi khi xử lý: {ex.Message}",
                });
            }
        }

        if (added.Count > 0)
        {
            // Chỉ ghi xuống đĩa khi thực sự có mục mới được thêm. Một lần nhập toàn mục trùng không nên sửa đổi tệp,
            // vì như vậy sẽ làm thay đổi thời gian sửa đổi tệp một cách không cần thiết.
            try { _library.Save(); }
            catch (StorageException)
            {
                foreach (var entry in added) _library.Remove(entry.Id);
                throw;
            }
        }

        var layoutChanged = false;
        var layoutEntries = addExistingEntriesToLauncher ? added.Concat(duplicates) : added;
        if (_layouts is not null && layoutEntries.Any())
        {
            var capacity = Math.Max(1, _launcherCapacity());
            var draft = LauncherLayoutRules.Reconcile(_layouts.Launcher,
                _library.Items.Where(item => item.IsUsable).Select(item => item.Id), capacity);
            draft = LauncherLayoutEditor.AddShortcuts(draft,
                layoutEntries.Where(item => item.IsUsable).Select(item => item.Id), capacity);
            layoutChanged = !LauncherLayoutRules.Equivalent(_layouts.Launcher, draft);
            if (layoutChanged && !_layouts.CommitLauncher(draft, _layouts.LauncherVersion))
                throw new StorageException("Bố cục Launchpad đã thay đổi; hãy thử thêm lại.", _layouts.LauncherFilePath);
        }
        if (added.Count > 0 || layoutChanged) _observer?.OnLibraryChanged();

        return new ImportResult
        {
            Added = added,
            Duplicates = duplicates,
            Failures = failures,
        };
    }

    /// <summary>Nhập một nguồn đơn lẻ và ghi kết quả vào ba danh sách được truyền vào.</summary>
    public void ImportOne(
        ImportRequest request,
        List<AppEntry> added,
        List<AppEntry> duplicates,
        List<ImportFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(duplicates);
        ArgumentNullException.ThrowIfNull(failures);

        var (entry, reason, message) = AppEntryFactory.TryCreate(request);

        if (entry is null)
        {
            failures.Add(new ImportFailure
            {
                Source = request.Source ?? string.Empty,
                Reason = reason,
                Message = message,
            });

            return;
        }

        // Khử trùng lặp trước rồi mới trích xuất biểu tượng: mục trùng không nên tốn chi phí trích xuất cho một biểu tượng sẽ không được lưu.
        var existing = _library.FindByKey(AppEntryKey.Compute(entry));
        if (existing is not null)
        {
            duplicates.Add(existing);
            return;
        }

        CacheIcon(entry);

        if (_library.Add(entry) != AddEntryResult.Added)
        {
            // Đi tới đây nghĩa là trong cùng một lô có nguồn bị trùng (ví dụ kéo cùng một thư mục hai lần).
            // Trường hợp này cũng được xem là “đã tồn tại”, không phải lỗi.
            duplicates.Add(entry);
            return;
        }

        added.Add(entry);
    }

    /// <summary>Chuẩn bị biểu tượng đệm cho một mục; nếu thất bại thì giữ nguyên không có biểu tượng, không ảnh hưởng tới bản thân mục đó.</summary>
    public void CacheIcon(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cached = _icons.EnsureFor(entry.IconSource);

        if (cached is null)
        {
            return;
        }

        if (cached.CreatedFile)
        {
            IconsWritten++;
        }
        else
        {
            IconsReused++;
        }

        // Giữ nguyên nguồn biểu tượng gốc, không thay bằng đường dẫn tệp đệm: bộ đệm có thể bị xóa,
        // giữ nguồn gốc thì sau khi dọn bộ đệm mới có thể trích xuất lại.
    }
}

/// <summary>
/// Nhận thông báo khi thư viện ứng dụng thay đổi.
/// </summary>
/// <remarks>
/// Giúp dịch vụ không cần biết đến sự tồn tại của Dock và Launchpad. Giai đoạn 3 dùng giao diện này để báo thay đổi cho máy chủ;
/// khi các cửa sổ của giai đoạn 4/5 tiếp quản việc làm mới thì sẽ nối vào cùng đầu mối này.
/// </remarks>
public interface IImportObserver
{
    void OnLibraryChanged();
}
