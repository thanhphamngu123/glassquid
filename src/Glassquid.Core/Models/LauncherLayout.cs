using System.Text.Json.Serialization;

namespace Glassquid.Core.Models;

/// <summary>
/// Bố cục Launcher: danh sách trang có thứ tự, mỗi trang giữ một danh sách ô có thứ tự.
/// </summary>
/// <remarks>
/// Về mặt cấu trúc không thể xuất hiện lồng nhau. Một ô hoặc tham chiếu trực tiếp tới ứng dụng,
/// hoặc là một nhóm; nhóm chỉ giữ id ứng dụng, tuyệt đối không giữ các ô khác. Điều này đáp ứng ngay từ cấu trúc
/// quy tắc "nhóm chỉ có một tầng" của tài liệu kế hoạch, thay vì phụ thuộc vào mã kiểm tra
/// để bắt vòng lặp sau khi đã xảy ra.
/// </remarks>
public sealed class LauncherLayout
{
    public List<LauncherPage> Pages { get; set; } = [];

    /// <summary>Version 2 records explicit launcher membership, independently of Dock.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Read-only migration input for pre-version-2 layouts; never persisted again.</summary>
    [JsonIgnore]
    public List<string> ExcludedAppIds { get; set; } = [];

    [JsonPropertyName("excludedAppIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyExcludedAppIds
    {
        get => null;
        set => ExcludedAppIds = value ?? [];
    }
}

public sealed class LauncherPage
{
    public string Id { get; set; } = string.Empty;

    public List<LauncherSlot> Slots { get; set; } = [];
}

public enum LauncherSlotKind
{
    /// <summary>Ô này hiển thị một ứng dụng đơn lẻ.</summary>
    App = 0,

    /// <summary>Ô này hiển thị một nhóm ứng dụng dạng thư mục.</summary>
    Group = 1,
}

public sealed class LauncherSlot
{
    public LauncherSlotKind Kind { get; set; } = LauncherSlotKind.App;

    /// <summary>Id thư viện ứng dụng khi <see cref="Kind"/> là <see cref="LauncherSlotKind.App"/>.</summary>
    public string? ItemId { get; set; }

    /// <summary>Nội dung nhóm khi <see cref="Kind"/> là <see cref="LauncherSlotKind.Group"/>.</summary>
    public LauncherGroup? Group { get; set; }
}

/// <summary>
/// Một nhóm ứng dụng có tên hiển thị phía sau một ô lưới đơn lẻ.
/// </summary>
public sealed class LauncherGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Id thư viện ứng dụng của các ứng dụng thành viên. Chỉ gồm id — nhóm không thể chứa nhóm khác.
    /// </summary>
    public List<string> ItemIds { get; set; } = [];
}
