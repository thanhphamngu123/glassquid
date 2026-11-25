using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Serialization;

namespace Glassquid.Core.Storage;

/// <summary>Kết quả của một lần sao lưu hoặc khôi phục, có thể hiển thị trực tiếp cho người dùng.</summary>
public sealed class TransferResult
{
    public bool Succeeded { get; init; }

    /// <summary>Thư mục đích được ghi vào (khi sao lưu) hoặc thư mục nguồn được đọc từ (khi khôi phục).</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Tên tương đối của các tệp đã sao chép.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Vị trí lưu bản chụp nhanh trước khi khôi phục, nếu bản chụp nhanh thực sự đã được tạo.</summary>
    public string? SafetyCopyDirectory { get; init; }

    /// <summary>
    /// Số lượng tham chiếu biểu tượng được quản lý đã được trỏ lại về thư mục dữ liệu mới sau khi khôi phục.
    /// </summary>
    /// <remarks>
    /// Khi bản sao lưu đến từ thư mục cũ, các tham chiếu trong kho lưu trữ vẫn trỏ về đó; quá trình khôi phục phải cập nhật chúng sang thư mục mới,
    /// nếu không tệp đã được sao chép sang nhưng biểu tượng vẫn bị lỗi. Giá trị 0 nghĩa là không có tham chiếu nào cần sửa.
    /// </remarks>
    public int RepairedIconReferences { get; init; }

    /// <summary>Thông báo dành cho người dùng khi <see cref="Succeeded"/> là false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Sao chép dữ liệu của chính sản phẩm này sang một thư mục khác và có thể sao chép ngược lại từ đó. Lớp này không bao giờ đọc hay ghi
/// bộ nhớ riêng của sản phẩm tham chiếu — phương án xác định rõ phiên bản đầu tiên chỉ giới hạn trong
/// dữ liệu của chính Glassquid, cộng với việc nhập lại từ Start Menu.
/// </summary>
public sealed class BackupService
{
    private const string ManifestFileName = "manifest.json";

    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;

    public BackupService(AppPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Ghi một bản sao tự mô tả gồm cài đặt, thư viện ứng dụng và bố cục vào
    /// <paramref name="destinationDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Các biểu tượng tùy chỉnh cũng được ghi kèm và giữ nguyên tầng thư mục con <c>icons/</c>, để khi khôi phục có thể đặt lại đúng vị trí ban đầu.
    /// </remarks>
    public TransferResult BackupTo(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            var copied = new List<string>();

            foreach (var relativePath in SourcesToArchive())
            {
                CopyPreservingRelativePath(
                    Path.Combine(_paths.RootDirectory, relativePath),
                    destinationDirectory,
                    relativePath);

                copied.Add(relativePath);
            }

            var manifest = new BackupManifest
            {
                ProductVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(),
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                Files = copied,
            };

            File.WriteAllText(
                Path.Combine(destinationDirectory, ManifestFileName),
                JsonSerializer.Serialize(manifest, GlassquidJsonContext.Default.BackupManifest));

            return new TransferResult
            {
                Succeeded = true,
                Directory = destinationDirectory,
                Files = copied,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = destinationDirectory,
                Error = $"Tạo bản sao lưu thất bại: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Khôi phục từ thư mục đã được tạo trước đó bởi <see cref="BackupTo"/>.
    /// </summary>
    /// <remarks>
    /// Dữ liệu hiện tại sẽ được chụp nhanh vào <c>backup/pre-restore-*</c> trước, nhờ đó thao tác
    /// khôi phục bấm nhầm vẫn có thể hoàn tác được.
    /// </remarks>
    public TransferResult RestoreFrom(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "Thư mục đã chọn không tồn tại.",
            };
        }

        var manifestPath = Path.Combine(sourceDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "Không tìm thấy tệp kê khai bản sao lưu Glassquid trong thư mục đã chọn nên không thể xác minh nguồn.",
            };
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                GlassquidJsonContext.Default.BackupManifest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = $"Không thể đọc tệp kê khai bản sao lưu: {ex.Message}",
            };
        }

        if (manifest is null || !string.Equals(manifest.Product, "Glassquid", StringComparison.Ordinal))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "Bản sao lưu này không thuộc Glassquid.",
            };
        }

        if (manifest.SchemaVersion > GlassquidSettings.CurrentSchemaVersion)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "Bản sao lưu được tạo bằng phiên bản mới hơn. Hãy cập nhật ứng dụng trước khi khôi phục.",
            };
        }

        // Đọc qua toàn bộ bản sao lưu trước khi đụng vào bất kỳ dữ liệu hiện có nào.
        //
        // Chỉ kiểm tra tệp kê khai là chưa đủ: tệp kê khai có thể nguyên vẹn trong khi tệp cài đặt bên trong là JSON hỏng. Khi đó việc sao chép từng tệp
        // sẽ ghi đè cấu hình đang hoạt động bình thường của người dùng bằng dữ liệu hỏng, rồi vẫn báo "khôi phục thành công" — người dùng vừa mất
        // dữ liệu gốc, vừa tưởng mọi thứ bình thường. Vì vậy thà từ chối toàn bộ bản sao lưu còn hơn.
        var unreadable = FindUnreadableDocuments(sourceDirectory);
        if (unreadable.Count > 0)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = $"Không thể đọc các tệp sau trong bản sao lưu; đã hủy khôi phục (dữ liệu hiện tại không thay đổi): {string.Join(", ", unreadable)}",
            };
        }

        var safetyCopy = SnapshotCurrent();

        try
        {
            _paths.EnsureCreated();
            var restored = new List<string>();

            // Danh sách khôi phục căn cứ vào **những gì có trong kho lưu trữ**, chứ không phải những gì hiện có trong thư mục dữ liệu:
            // trước khi khôi phục bộ nhớ đệm thường đã bị xóa trống, liệt kê theo hiện trạng sẽ không tìm thấy bất kỳ biểu tượng nào.
            foreach (var relativePath in RestoreCandidates(sourceDirectory))
            {
                CopyPreservingRelativePath(
                    Path.Combine(sourceDirectory, relativePath),
                    _paths.RootDirectory,
                    relativePath);

                restored.Add(relativePath);
            }

            // Các tham chiếu trong kho lưu trữ trỏ tới thư mục dữ liệu tại thời điểm sao lưu. Tệp đã được sao chép đúng chỗ,
            // nhưng nếu không sửa lại tham chiếu thì khi khôi phục sang máy khác hoặc thư mục khác, biểu tượng vẫn bị lỗi —
            // đây chính là phần việc mà việc sao chép tệp không tự giải quyết được.
            var repaired = RepairManagedIconReferences();

            return new TransferResult
            {
                Succeeded = true,
                Directory = sourceDirectory,
                Files = restored,
                SafetyCopyDirectory = safetyCopy,
                RepairedIconReferences = repaired,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                SafetyCopyDirectory = safetyCopy,
                Error = $"Khôi phục thất bại: {ex.Message}",
            };
        }
    }

    /// <summary>Thử phân tích từng tài liệu trong bản sao lưu và trả về tên các tệp không đọc được.</summary>
    /// <remarks>
    /// Tra cứu trình xác thực theo đường dẫn thực tế của tài liệu trên đĩa thay vì đoán theo tên tệp, tránh trường hợp sau này đổi tên tệp
    /// khiến bước xác thực bị bỏ qua âm thầm. Nếu không tìm thấy trình xác thực thì mặc định coi là "không thể xác thực" và cũng từ chối khôi phục — thà để khôi phục
    /// báo lỗi rõ ràng, còn hơn âm thầm ghi đè dữ liệu người dùng mà không xác thực.
    ///
    /// Chỉ vài tệp JSON đó nằm trong danh sách này. Biểu tượng tùy chỉnh là tệp nhị phân, không có cấu trúc tài liệu để phân tích,
    /// nên sẽ không vì "không giải mã được" mà từ chối cả bản sao lưu.
    /// </remarks>
    private static List<string> FindUnreadableDocuments(string sourceDirectory)
    {
        var validators = new Dictionary<string, Func<string, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings.json"] = text => CanDeserialize(text, GlassquidJsonContext.Default.GlassquidSettings),
            ["apps.json"] = text => CanDeserialize(text, GlassquidJsonContext.Default.AppLibraryData),
            ["dock-layout.json"] = text => CanDeserialize(text, GlassquidJsonContext.Default.DockLayout),
            ["launcher-layout.json"] = text => CanDeserialize(text, GlassquidJsonContext.Default.LauncherLayout),
        };

        var unreadable = new List<string>();

        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            var candidate = Path.Combine(sourceDirectory, relativePath);

            // Thiếu tệp không phải lỗi: bản sao lưu chụp khi người dùng chưa tùy chỉnh gì vốn dĩ sẽ thiếu vài tệp.
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (!validators.TryGetValue(relativePath, out var canDeserialize) || !CanRead(candidate, canDeserialize))
            {
                unreadable.Add(relativePath);
            }
        }

        return unreadable;
    }

    private static bool CanRead(string path, Func<string, bool> canDeserialize)
    {
        try
        {
            return canDeserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanDeserialize<T>(string text, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize(text, typeInfo) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private string? SnapshotCurrent()
    {
        try
        {
            var stamp = _timeProvider.GetUtcNow().UtcDateTime
                .ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var directory = Path.Combine(_paths.BackupDirectory, $"pre-restore-{stamp}");
            Directory.CreateDirectory(directory);

            foreach (var relativePath in SourcesToArchive())
            {
                CopyPreservingRelativePath(
                    Path.Combine(_paths.RootDirectory, relativePath),
                    directory,
                    relativePath);
            }

            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Việc thiếu bản sao an toàn không nên chặn thao tác khôi phục mà người dùng yêu cầu; bên gọi
            // chỉ báo cáo đường dẫn này khi nó thực sự tồn tại.
            return null;
        }
    }

    /// <summary>
    /// Sửa các tham chiếu biểu tượng được quản lý trong thư viện ứng dụng vừa khôi phục vốn đang trỏ về thư mục dữ liệu cũ sang thư mục hiện tại và ghi xuống đĩa.
    /// </summary>
    /// <remarks>
    /// Logic tự phục hồi chỉ có một bản duy nhất nằm trong <see cref="AppLibraryStore.Load"/>;
    /// ở đây chỉ **ghi đĩa một lần cho nó** — Load ở tầng dữ liệu không bao giờ tự ghi tệp,
    /// mà nếu khôi phục xong không ghi lại thì tệp apps.json trên đĩa vẫn trỏ về thư mục cũ,
    /// phải đợi đến lần khởi động tiếp theo mới được sửa.
    ///
    /// <para>
    /// Nếu không có tham chiếu nào cần sửa thì <strong>không ghi</strong>: tài liệu vừa được sao chép nguyên vẹn sang,
    /// không có lý do gì để ghi đè lại một lần nữa. Ghi thất bại cũng không làm cả quá trình khôi phục báo lỗi — tệp đã nằm đúng chỗ,
    /// tham chiếu sẽ được sửa lại một lần nữa vào lần khởi động tiếp theo.
    /// </para>
    /// </remarks>
    private int RepairManagedIconReferences()
    {
        try
        {
            var store = new AppLibraryStore(_paths.AppLibraryFile);
            store.Load();

            if (store.RepairedIconReferenceCount == 0)
            {
                return 0;
            }

            var repaired = store.RepairedIconReferenceCount;
            store.Save();
            return repaired;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Các tệp cần mang theo khi sao lưu: bốn tài liệu (thiếu tệp nào bỏ qua tệp đó) cộng với các biểu tượng tùy chỉnh hiện có trên đĩa.
    /// </summary>
    private IEnumerable<string> SourcesToArchive()
    {
        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            // Tài liệu này chưa từng được lưu; tệp không tồn tại không tính là lỗi.
            if (File.Exists(Path.Combine(_paths.RootDirectory, relativePath)))
            {
                yield return relativePath;
            }
        }

        foreach (var relativePath in _paths.CustomIconRelativePaths())
        {
            yield return relativePath;
        }
    }

    /// <summary>
    /// Các tệp cần ghi lại khi khôi phục: những tài liệu có trong kho lưu trữ, cộng với các biểu tượng tùy chỉnh đi kèm trong kho lưu trữ.
    /// </summary>
    /// <remarks>
    /// <b>Danh sách lấy theo kho lưu trữ làm chuẩn</b>, không lấy theo hiện trạng thư mục dữ liệu — trước khi khôi phục bộ nhớ đệm thường đã bị xóa trống,
    /// liệt kê theo hiện trạng sẽ không tìm thấy bất kỳ biểu tượng nào. Đây cũng là lỗi thực tế từng gặp khi rà soát lại giai đoạn 3.
    /// </remarks>
    private static IEnumerable<string> RestoreCandidates(string sourceDirectory)
    {
        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            if (File.Exists(Path.Combine(sourceDirectory, relativePath)))
            {
                yield return relativePath;
            }
        }

        foreach (var relativePath in CustomIconsInArchive(sourceDirectory))
        {
            yield return relativePath;
        }
    }

    /// <summary>
    /// Các biểu tượng tùy chỉnh đi kèm trong kho lưu trữ.
    /// </summary>
    /// <remarks>
    /// Chỉ nhận <c>icons/custom-*</c>, khớp với loại tệp được ghi vào khi sao lưu.
    /// Mọi tệp có nguồn gốc khác trong kho lưu trữ đều bỏ qua — đó không phải dữ liệu của chúng ta.
    /// </remarks>
    private static IReadOnlyList<string> CustomIconsInArchive(string sourceDirectory)
    {
        var directory = Path.Combine(sourceDirectory, AppPaths.IconCacheSubdirectory);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.GetFiles(directory, "custom-*")
            .Select(file => Path.Combine(AppPaths.IconCacheSubdirectory, Path.GetFileName(file)))];
    }

    /// <summary>
    /// Sao chép một tệp và khôi phục lại cấu trúc thư mục con theo <paramref name="relativePath"/> bên dưới thư mục đích.
    /// </summary>
    /// <remarks>
    /// Biểu tượng tùy chỉnh nằm trong thư mục con <c>icons/</c>, nên trong kho lưu trữ cũng phải giữ nguyên tầng này,
    /// nếu không khi khôi phục sẽ không quay về đúng vị trí cũ. Phiên bản cũ sao chép bằng tên phẳng nên đã làm mất tầng thư mục này.
    /// </remarks>
    private static void CopyPreservingRelativePath(string source, string destinationDirectory, string relativePath)
    {
        var target = Path.Combine(destinationDirectory, relativePath);
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(source, target, overwrite: true);
    }
}