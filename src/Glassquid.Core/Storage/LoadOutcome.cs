namespace Glassquid.Core.Storage;

/// <summary>Kết quả của một lần thử tải dữ liệu.</summary>
public enum LoadStatus
{
    /// <summary>Tài liệu được đọc thành công từ đĩa.</summary>
    Loaded = 0,

    /// <summary>Chưa có nội dung trên đĩa, đã tạo giá trị mặc định.</summary>
    FirstRun = 1,

    /// <summary>
    /// Tài liệu chính không khả dụng, nhưng bản sao lưu song song vẫn đọc được. Người dùng tối đa
    /// chỉ mất thay đổi gần nhất.
    /// </summary>
    RecoveredFromBackup = 2,

    /// <summary>
    /// Cả tài liệu và bản sao lưu đều không thể đọc. Đã tạo giá trị mặc định và giữ lại
    /// tệp bị hỏng để phục vụ kiểm tra.
    /// </summary>
    Unrecoverable = 3,
}

/// <summary>
/// Kết quả tải tài liệu lưu trữ, bao gồm thông tin mà bên gọi cần thông báo cho người dùng.
/// </summary>
public sealed class LoadOutcome<T>
    where T : class
{
    internal LoadOutcome(T value, LoadStatus status, string? preservedFile, Exception? error)
    {
        Value = value;
        Status = status;
        PreservedFile = preservedFile;
        Error = error;
    }

    /// <summary>Luôn khả dụng: hoặc là nội dung trên đĩa, hoặc là giá trị mặc định mới tạo.</summary>
    public T Value { get; }

    public LoadStatus Status { get; }

    /// <summary>
    /// Vị trí tệp bị hỏng được di chuyển tới khi phát hiện lỗi. Giá trị khác null cho biết cần thông báo cho người dùng,
    /// đồng thời đảm bảo không có dữ liệu nào bị âm thầm bỏ đi.
    /// </summary>
    public string? PreservedFile { get; }

    /// <summary>Ngoại lệ dẫn đến <see cref="LoadStatus.Unrecoverable"/>, có thể là null.</summary>
    public Exception? Error { get; }

    /// <summary>Khi là true, bên gọi nên đưa ra lời giải thích thay vì âm thầm tiếp tục.</summary>
    public bool NeedsUserAttention => Status is LoadStatus.RecoveredFromBackup or LoadStatus.Unrecoverable;
}