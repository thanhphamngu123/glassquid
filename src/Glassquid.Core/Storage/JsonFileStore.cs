using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Glassquid.Core.Storage;

/// <summary>
/// Tải và lưu một tài liệu JSON đơn lẻ, đồng thời hỗ trợ khôi phục từ bản sao lưu.
/// </summary>
/// <remarks>
/// Quá trình tuần tự hóa dùng <see cref="JsonTypeInfo{T}"/> được sinh từ mã nguồn thay vì
/// reflection, nên cấu trúc chính xác của từng tài liệu lưu trữ đều rõ ràng ngay từ lúc biên dịch
/// và trình cắt gọn (trimmer) cũng không thể âm thầm loại bỏ các thuộc tính mà ta phụ thuộc.
/// </remarks>
public sealed class JsonFileStore<T>
    where T : class
{
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly TimeProvider _timeProvider;

    public JsonFileStore(string path, JsonTypeInfo<T> typeInfo, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);

        Path = path;
        _typeInfo = typeInfo;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Đường dẫn tài liệu mà kho lưu trữ này phụ trách.</summary>
    public string Path { get; }

    /// <summary>
    /// Đọc tài liệu; chuyển sang dùng bản sao lưu khi tệp chính không khả dụng.
    /// Tuyệt đối không ném ngoại lệ khi tệp bị hỏng: trả về giá trị mặc định và báo cáo tình trạng thực tế đã xảy ra.
    /// </summary>
    public LoadOutcome<T> Load(Func<T> createDefault)
    {
        ArgumentNullException.ThrowIfNull(createDefault);

        var raw = TryRead(Path);
        if (raw is not null)
        {
            if (TryDeserialize(raw, out var parsed))
            {
                return new LoadOutcome<T>(parsed!, LoadStatus.Loaded, null, null);
            }

            // Không khả dụng. Giữ lại bằng chứng trước, rồi thử đọc bản sao lưu song song.
            var preserved = AtomicFile.PreserveCorrupt(Path, _timeProvider.GetUtcNow());
            return RecoverOrFail(createDefault, preserved);
        }

        // Không có tệp chính. Bản sao lưu vẫn có thể tồn tại, ví dụ tài liệu bị xóa thủ công,
        // hoặc lần thay thế trước bị gián đoạn trước khi đổi tên hoàn tất.
        if (File.Exists(Path + AtomicFile.BackupSuffix))
        {
            return RecoverOrFail(createDefault, preservedFile: null);
        }

        return new LoadOutcome<T>(createDefault(), LoadStatus.FirstRun, null, null);
    }

    /// <summary>Ghi tài liệu một cách nguyên tử và chuyển nội dung cũ thành bản sao lưu.</summary>
    /// <exception cref="StorageException">Ném ra khi ghi tài liệu thất bại.</exception>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, _typeInfo);
        AtomicFile.WriteAllText(Path, json);
    }

    private LoadOutcome<T> RecoverOrFail(Func<T> createDefault, string? preservedFile)
    {
        var backupPath = Path + AtomicFile.BackupSuffix;
        var backupRaw = TryRead(backupPath);

        if (backupRaw is not null && TryDeserialize(backupRaw, out var recovered))
        {
            return new LoadOutcome<T>(recovered!, LoadStatus.RecoveredFromBackup, preservedFile, null);
        }

        if (preservedFile is null && !File.Exists(Path))
        {
            // Trên đĩa thực sự không có gì cả và cũng không có nội dung nào có thể cứu vãn.
            return new LoadOutcome<T>(createDefault(), LoadStatus.FirstRun, null, null);
        }

        return new LoadOutcome<T>(
            createDefault(),
            LoadStatus.Unrecoverable,
            preservedFile,
            new JsonException("Không thể đọc tệp cấu hình hoặc bản sao lưu của tệp."));
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Với chúng ta, "không đọc được" và "bị hỏng" không có gì khác biệt: chuyển thẳng sang nhánh sao lưu,
            // thay vì để toàn bộ quá trình tải bị thất bại.
            return null;
        }
    }

    private bool TryDeserialize(string raw, out T? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(raw, _typeInfo);
            return value is not null;
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
}