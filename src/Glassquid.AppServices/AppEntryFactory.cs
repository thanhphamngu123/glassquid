using System.Globalization;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;
using Glassquid.Platform.Shell;

namespace Glassquid.AppServices;

/// <summary>Chuyển đổi đường dẫn hoặc URL thành bản ghi trong thư viện ứng dụng.</summary>
/// <remarks>
/// Chỉ phụ trách phần "suy diễn" — nhận vào một nguồn và tính ra cần lưu thành gì. Việc ghi vào kho lưu trữ, khử trùng lặp và lưu tệp do
/// <see cref="ImportService"/> xử lý thống nhất, nhờ đó việc nhập hàng loạt từ menu Start và việc người dùng kéo thả từng tệp đơn lẻ
/// đi qua cùng một logic suy diễn, không tạo ra hai kiểu bản ghi khác nhau từ hai lối vào.
/// </remarks>
public static class AppEntryFactory
{
    /// <summary>Tạo một bản ghi từ một yêu cầu nhập.</summary>
    public static (AppEntry? Entry, ImportFailureReason Reason, string Message) TryCreate(ImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.IsUrl ? CreateFromUrl(request) : CreateFromPath(request);
    }

    /// <summary>Tạo bản ghi từ một đường dẫn đã xác định sẵn, dùng lại cho tính năng nhập hàng loạt từ menu Start.</summary>
    public static (AppEntry? Entry, ImportFailureReason Reason, string Message) TryCreateFromPath(
        string path,
        string? displayNameOverride = null)
        => CreateFromPath(new ImportRequest { Source = path, DisplayName = displayNameOverride });

    private static (AppEntry?, ImportFailureReason, string) CreateFromUrl(ImportRequest request)
    {
        var url = request.Source?.Trim() ?? string.Empty;

        if (!InternetShortcutReader.IsSupportedUrl(url))
        {
            return (null, ImportFailureReason.UnsupportedType, $"Địa chỉ trang web không hợp lệ: {url}");
        }

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Url,
            Target = url,
            DisplayName = ResolveName(request.DisplayName, DeriveUrlName(url)),
            IconSource = url,
        };

        return (entry, default, string.Empty);
    }

    private static (AppEntry?, ImportFailureReason, string) CreateFromPath(ImportRequest request)
    {
        var source = request.Source?.Trim() ?? string.Empty;

        if (source.Length == 0)
        {
            return (null, ImportFailureReason.NotFound, "Chưa cung cấp đường dẫn.");
        }

        string full;

        try
        {
            full = Path.GetFullPath(source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, ImportFailureReason.NotFound, $"Không thể xác định đường dẫn: {source}");
        }

        bool isDirectory;
        bool exists;

        try
        {
            isDirectory = Directory.Exists(full);
            exists = isDirectory || File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ImportFailureReason.AccessDenied, $"Không thể đọc vị trí này: {full}");
        }

        if (!exists)
        {
            // Khi nhập vào thì nguồn phải thực sự tồn tại. Việc mục đó về sau "bị" mất hiệu lực là chuyện khác (người dùng xóa mất đích),
            // nhánh đó do IsTargetMissing phụ trách, xem LibraryMaintenance.
            return (null, ImportFailureReason.NotFound, $"Không tìm thấy: {full}");
        }

        var kind = ItemKindDetector.Detect(full, isDirectory);

        return kind switch
        {
            AppItemKind.Shortcut => CreateFromShortcut(request, full),
            AppItemKind.Url => CreateFromInternetShortcut(request, full),
            _ => (CreatePlain(request, full, kind), default, string.Empty),
        };
    }

    /// <summary>
    /// Tạo bản ghi từ <c>.lnk</c>.
    /// </summary>
    /// <remarks>
    /// Chia làm ba kết quả, khác nhau ở chỗ "shell còn mở được nó hay không":
    /// <list type="bullet">
    /// <item><b>Đọc được đích tệp</b>: lưu đích, tham số, thư mục làm việc, biểu tượng. Đây là phần lớn các lối tắt.</item>
    /// <item><b>Không đọc được đích tệp nhưng bản thân lối tắt vẫn tốt</b>: giữ nguyên <c>.lnk</c> làm đích,
    /// gán loại là lối tắt. Ứng dụng UWP và lối tắt quảng bá MSI thuộc loại này — đích của chúng là một
    /// định danh ứng dụng thay vì đường dẫn tệp, và trong menu Start loại mục này chiếm tỷ lệ khá lớn.
    /// Bỏ chúng đi chẳng khác nào đánh mất nửa menu Start của người dùng, trong khi shell vẫn mở được chúng bình thường.</item>
    /// <item><b>Thậm chí không đọc nổi</b>: báo lỗi từng mục, không nhập. Lúc này đến shell cũng không thể mở nó,
    /// nhận vào chỉ tạo ra một bản ghi bấm vào không có phản hồi.</item>
    /// </list>
    /// </remarks>
    private static (AppEntry?, ImportFailureReason, string) CreateFromShortcut(ImportRequest request, string shortcutPath)
    {
        var resolved = ShortcutResolver.Resolve(shortcutPath);
        var fallbackName = ItemKindDetector.DeriveDisplayName(shortcutPath, AppItemKind.Shortcut);

        if (resolved is null)
        {
            return (null, ImportFailureReason.ShortcutUnreadable, $"Không thể đọc lối tắt: {shortcutPath}");
        }

        if (!resolved.HasResolvedTarget)
        {
            // Đi tới đây nghĩa là danh sách ID không rỗng (nếu không Resolve đã trả về null), chỉ là thứ nó trỏ tới
            // không có đường dẫn tệp — ứng dụng UWP và lối tắt quảng bá MSI đều như vậy.
            // Trong menu Start loại mục này chiếm tỷ lệ khá lớn, bỏ chúng đi coi như mất nửa menu Start của người dùng,
            // trong khi shell vẫn mở được bình thường, nên giữ chính .lnk làm đích.
            var opaque = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.Shortcut,
                Target = shortcutPath,
                DisplayName = ResolveName(request.DisplayName, fallbackName),
                Arguments = Nullify(resolved.Arguments),
                WorkingDirectory = Nullify(resolved.WorkingDirectory),

                // Nguồn biểu tượng dùng chính đường dẫn .lnk: đo thực tế khi lấy biểu tượng cho lối tắt thì shell sẽ tôn trọng vị trí biểu tượng
                // tùy chỉnh của nó, nên thứ lấy được ở đây chính là biểu tượng mà người dùng nhìn thấy trên menu.
                IconSource = shortcutPath,

                // Việc gắn cửa sổ của ứng dụng UWP và ứng dụng đóng gói dựa vào định danh này để phân định (D-09/D-10), xem giải thích ở AppEntry.
                AppUserModelId = resolved.Aumid,
            };

            return (opaque, default, string.Empty);
        }

        if (resolved.TargetIsUrl)
        {
            var urlEntry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.Url,
                Target = resolved.Target,
                DisplayName = ResolveName(request.DisplayName, fallbackName),
                Arguments = Nullify(resolved.Arguments),
                WorkingDirectory = Nullify(resolved.WorkingDirectory),
                IconSource = ResolveShortcutIconSource(resolved, shortcutPath),
            };

            return (urlEntry, default, string.Empty);
        }

        var target = ResolveShortcutTarget(resolved.Target, resolved.WorkingDirectory, shortcutPath);

        // Đích không tồn tại vẫn nhập như thường, chỉ đánh dấu tạm là mất hiệu lực: người dùng có thể đang cắm một ổ cứng di động chưa kết nối,
        // hoặc vừa gỡ cài đặt chương trình rồi cài lại. Đây chính là tình huống R-07 yêu cầu "giữ lại mục thay vì để nó biến mất".
        var exists = ExistsQuietly(target);

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = exists ? ItemKindDetector.Detect(target, IsDirectoryQuietly(target)) : AppItemKind.Program,
            Target = target,
            DisplayName = ResolveName(request.DisplayName, fallbackName),
            Arguments = Nullify(resolved.Arguments),
            WorkingDirectory = Nullify(resolved.WorkingDirectory),
            IconSource = ResolveShortcutIconSource(resolved, shortcutPath),
            IsTargetMissing = !exists,
            AppUserModelId = resolved.Aumid,
        };

        return (entry, default, string.Empty);
    }

    private static (AppEntry?, ImportFailureReason, string) CreateFromInternetShortcut(ImportRequest request, string path)
    {
        var url = InternetShortcutReader.ReadUrl(path);

        if (url is null)
        {
            // Đuôi là .url nhưng bên trong không có địa chỉ dùng được — hoặc tệp bị hỏng, hoặc thực chất nó là một thứ khác.
            // Quay về xử lý như tệp thông thường hữu ích hơn là báo lỗi ngay.
            return (CreatePlain(request, path, AppItemKind.File), default, string.Empty);
        }

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Url,
            Target = url,
            DisplayName = ResolveName(request.DisplayName, ItemKindDetector.DeriveDisplayName(path, AppItemKind.Url)),

            // Biểu tượng của trang web giao cho shell lấy theo địa chỉ này, thay vì lấy biểu tượng của chính tệp .url kia.
            IconSource = url,
        };

        return (entry, default, string.Empty);
    }

    private static AppEntry CreatePlain(ImportRequest request, string path, AppItemKind kind)
        => new()
        {
            Id = AppLibraryStore.NewId(),
            Kind = kind,
            Target = path,
            DisplayName = ResolveName(request.DisplayName, ItemKindDetector.DeriveDisplayName(path, kind)),
            IconSource = path,
        };

    /// <summary>
    /// Đích của lối tắt có thể là đường dẫn tương đối.
    /// </summary>
    /// <remarks>
    /// <c>IShellLink::GetPath</c> thường trả về đường dẫn tuyệt đối, nhưng khi lối tắt được lưu theo đường dẫn tương đối
    /// (cách làm phổ biến của trình cài đặt), kết quả nhận được sẽ tương đối so với thư mục làm việc.
    /// Nếu không phân giải nó, một bản ghi trỏ tới <c>..\app.exe</c> sẽ mãi mãi hiển thị là mất hiệu lực.
    /// </remarks>
    private static string ResolveShortcutTarget(string target, string workingDirectory, string shortcutPath)
    {
        try
        {
            if (Path.IsPathFullyQualified(target))
            {
                return Path.GetFullPath(target);
            }

            // Ghép theo thư mục làm việc trước, rồi mới ghép theo thư mục chứa lối tắt — đây chính là thứ tự phân giải đích tương đối của Windows.
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var candidate = Path.GetFullPath(Path.Combine(workingDirectory, target));
                if (ExistsQuietly(candidate))
                {
                    return candidate;
                }
            }

            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shortcutPath) ?? string.Empty, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return target;
        }
    }

    /// <summary>
    /// Quyết định biểu tượng của bản ghi này lấy từ đâu.
    /// </summary>
    /// <remarks>
    /// Ưu tiên dùng vị trí biểu tượng do chính lối tắt ghi lại: đó mới là biểu tượng người dùng nhìn thấy trên menu.
    /// Khi tệp biểu tượng tùy chỉnh mất hiệu lực thì lùi về tệp đích; khi đích cũng không có mặt thì giao cho shell đọc từ lối tắt.
    /// </remarks>
    private static string ResolveShortcutIconSource(ShortcutInfo resolved, string shortcutPath)
    {
        if (!string.IsNullOrWhiteSpace(resolved.IconSource))
        {
            var (iconPath, _) = IconExtractor.SplitIconSource(resolved.IconSource);
            if (ExistsQuietly(iconPath)
                || string.Equals(resolved.IconSource, resolved.Target, StringComparison.OrdinalIgnoreCase))
            {
                return resolved.IconSource;
            }

            if (ExistsQuietly(resolved.Target))
            {
                return resolved.Target;
            }
        }

        return ExistsQuietly(shortcutPath) ? shortcutPath : resolved.Target;
    }

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

    private static bool IsDirectoryQuietly(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveName(string? overrideName, string derived)
        => string.IsNullOrWhiteSpace(overrideName) ? derived : overrideName.Trim();

    /// <summary>
    /// Tên mặc định của URL lấy theo tên máy chủ: lấy cả địa chỉ dài làm tên thì giao diện không hiển thị xuể.
    /// </summary>
    /// <remarks>
    /// <b>Cổng không mặc định phải đính kèm trong tên.</b> Sau khi khóa khử trùng lặp đã tính cả cổng vào,
    /// <c>http://localhost:8001/app</c> và <c>:8002</c> là hai bản ghi khác nhau;
    /// nếu tên vẫn chỉ ghi tên máy chủ thì cả hai trên giao diện đều gọi là <c>localhost</c>, coi như vứt bỏ mất
    /// thành quả "có thể phân biệt được".
    /// </remarks>
    private static string DeriveUrlName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return url;
        }

        return uri.IsDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? Nullify(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
