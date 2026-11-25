using System.Text.Json;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Serialization;

namespace Glassquid.Core.Storage;

/// <summary>
/// Quản lý tệp <c>settings.json</c>: thực hiện di chuyển schema khi tải và đảm bảo tính nguyên tử khi lưu.
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonFileStore<GlassquidSettings> _store;

    public SettingsStore(string path, TimeProvider? timeProvider = null)
    {
        _store = new JsonFileStore<GlassquidSettings>(
            path,
            GlassquidJsonContext.Default.GlassquidSettings,
            timeProvider);
    }

    /// <summary>Cài đặt trong bộ nhớ. Được thay thế bởi <see cref="Load"/>.</summary>
    public GlassquidSettings Current { get; private set; } = new();

    public string FilePath => _store.Path;

    /// <summary>
    /// Đọc cài đặt từ đĩa, di chuyển tài liệu phiên bản cũ và bổ sung các phần còn thiếu.
    /// </summary>
    public LoadOutcome<GlassquidSettings> Load()
    {
        var outcome = _store.Load(static () => new GlassquidSettings());

        var settings = outcome.Value;
        settings.Normalize();
        Migrate(settings, outcome.Status == LoadStatus.Loaded ? ReadSchemaVersionOnDisk() : settings.SchemaVersion);

        Current = settings;
        return outcome;
    }

    /// <summary>Lưu bền vững cài đặt hiện tại một cách nguyên tử.</summary>
    /// <exception cref="StorageException">Không thể ghi tệp.</exception>
    public void Save()
    {
        Current.Normalize();
        _store.Save(Current);
    }

    /// <summary>
    /// Đọc lại dấu phiên bản để <see cref="Migrate"/> thấy giá trị thực tế trên đĩa,
    /// thay vì giá trị mặc định do trình giải tuần tự điền vào.
    /// </summary>
    private int ReadSchemaVersionOnDisk()
    {
        var raw = AtomicFile.ReadAllTextOrNull(_store.Path);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return GlassquidSettings.CurrentSchemaVersion;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("schemaVersion", out var version) &&
                version.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Tài liệu đã được phân tích cú pháp qua đường dẫn định kiểu mạnh một lần, nên ở đây chỉ là cấu trúc ngoài dự kiến;
            // xử lý theo phiên bản hiện tại là cách diễn giải an toàn hơn.
        }

        return GlassquidSettings.CurrentSchemaVersion;
    }

    private static void Migrate(GlassquidSettings settings, int fromVersion)
    {
        if (fromVersion > GlassquidSettings.CurrentSchemaVersion)
        {
            // Được ghi bởi phiên bản mới hơn. Giữ lại phần có thể hiểu và gắn dấu theo phiên bản hiện tại;
            // GlassquidDataStore sẽ lưu giữ bản sao gốc trước lần lưu tiếp theo
            // để tránh làm mất trực tiếp các trường mới hơn.
            settings.SchemaVersion = GlassquidSettings.CurrentSchemaVersion;
            return;
        }

        // Phiên bản 1 là schema đầu tiên phát hành cùng sản phẩm, hiện chưa có nội dung cần di chuyển từng bước.
        // Đặt switch ở đây để lần thay đổi schema tiếp theo có điểm đặt rõ ràng.
        settings.SchemaVersion = GlassquidSettings.CurrentSchemaVersion;
    }
}