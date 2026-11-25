using System.Text.Json;
using System.Text.Json.Serialization;
using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;

namespace Glassquid.Core.Serialization;

/// <summary>
/// Mọi tài liệu được lưu trữ bền vững đều đã biết trước tại thời điểm biên dịch.
/// </summary>
/// <remarks>
/// Các tùy chọn ưu tiên khả năng chỉnh sửa thủ công vì người dùng nâng cao sẽ mở các tệp này:
/// đặt tên camelCase, ghi enum bằng từ thay vì số, chấp nhận chú thích và dấu phẩy cuối khi đọc,
/// bỏ qua thành phần null khi ghi.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GlassquidSettings))]
[JsonSerializable(typeof(AppLibraryData))]
[JsonSerializable(typeof(DockLayout))]
[JsonSerializable(typeof(LauncherLayout))]
[JsonSerializable(typeof(BackupManifest))]
public sealed partial class GlassquidJsonContext : JsonSerializerContext
{
}