namespace Glassquid.Platform.Shell;

/// <summary>Một lối tắt trong menu Start.</summary>
/// <param name="Path">Vị trí của bản thân tệp <c>.lnk</c>.</param>
/// <param name="Name">Tên không gồm phần mở rộng, tức là nhãn hiển thị trên menu.</param>
/// <param name="Group">Thư mục con chứa lối tắt (ví dụ “Phụ kiện”); là chuỗi rỗng nếu nằm trực tiếp ở thư mục gốc.</param>
/// <param name="FromCurrentUser">Đến từ menu của người dùng hiện tại thay vì mục dùng chung cho mọi người dùng.</param>
public sealed record StartMenuShortcut(string Path, string Name, string Group, bool FromCurrentUser);

/// <summary>Kết quả của một lần quét.</summary>
/// <param name="Shortcuts">Danh sách lối tắt sau khi khử trùng lặp.</param>
/// <param name="ScannedFileCount">Tổng số tệp <c>.lnk</c> tìm thấy (trước khi khử trùng lặp).</param>
/// <param name="DuplicateNameCount">Số mục bị nhường chỗ cho phiên bản của người dùng hiện tại do trùng tên.</param>
/// <param name="Roots">Các thư mục gốc đã thực tế quét, thuận tiện cho việc chẩn đoán.</param>
public sealed record StartMenuScanResult(
    IReadOnlyList<StartMenuShortcut> Shortcuts,
    int ScannedFileCount,
    int DuplicateNameCount,
    IReadOnlyList<string> Roots);

/// <summary>
/// Quét các lối tắt chương trình trong menu Start.
/// </summary>
/// <remarks>
/// Hai thư mục gốc: <c>Programs</c> của mọi người dùng và <c>Programs</c> của người dùng hiện tại. Điều này khớp với phạm vi mà
/// chính menu Start hiển thị — chỉ quét một nơi sẽ bỏ sót một nửa số ứng dụng.
///
/// <para>
/// <b>Khử trùng lặp theo tên ưu tiên “người dùng hiện tại trước”.</b> Đây chính là quy tắc của Windows: mục trùng tên trong menu người dùng
/// ghi đè mục trùng tên ở phạm vi toàn máy. Nếu không làm vậy, mục do trình cài đặt tạo cho mọi người dùng và mục cài riêng cho mình sẽ xuất hiện cùng lúc,
/// trên giao diện sẽ có hai mục “Notepad” giống hệt nhau.
/// </para>
///
/// <para>
/// Việc khử trùng lặp chỉ xét tên tệp, không xét đích trỏ — khử trùng lặp theo đích là nhiệm vụ của <c>AppEntryKey</c> khi nhập vào thư viện,
/// hai nơi giải quyết hai vấn đề khác nhau: ở đây giải quyết “trùng lặp hiển thị trên menu”, còn ở kia giải quyết “trùng lặp lưu trữ trong thư viện”.
/// </para>
/// </remarks>
public static class StartMenuScanner
{
    private const string ShortcutExtension = ".lnk";

    /// <summary>Quét hai thư mục menu Start mặc định.</summary>
    public static StartMenuScanResult Scan()
    {
        var roots = new List<string>();
        var candidates = new List<StartMenuShortcut>();

        // Quét phạm vi toàn máy trước rồi tới thư mục của người dùng; mục trùng tên sau sẽ ghi đè mục trước.
        Collect(KnownFolders.CommonPrograms, fromCurrentUser: false, roots, candidates);
        Collect(KnownFolders.Programs, fromCurrentUser: true, roots, candidates);

        var scanned = candidates.Count;

        // Dùng đường dẫn tương đối đầy đủ làm khóa để cặp “Group\Name” là duy nhất. Mục trùng tên ở các nhóm khác nhau là hai mục riêng biệt,
        // không nên gộp lại — đó chính là cách menu Start tổ chức ứng dụng.
        var byKey = new Dictionary<string, StartMenuShortcut>(StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in candidates)
        {
            var fileName = Path.GetFileNameWithoutExtension(shortcut.Path);
            var key = shortcut.Group.Length == 0 ? fileName : $"{shortcut.Group}\\{fileName}";
            byKey[key] = shortcut;
        }

        var result = byKey.Values
            .OrderBy(item => item.Group, StringComparer.CurrentCulture)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();

        return new StartMenuScanResult(result, scanned, scanned - result.Count, roots);
    }

    private static void Collect(string? root, bool fromCurrentUser, List<string> roots, List<StartMenuShortcut> into)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        roots.Add(root);

        IEnumerable<string> files;

        try
        {
            // Liệt kê đệ quy menu Start không quá sâu, nhưng liên kết thư mục bên trong có thể tạo vòng lặp,
            // vì vậy bỏ qua reparse point để tránh đệ quy vô hạn.
            files = EnumerateShortcuts(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = ShellDisplayNames.GetFileName(file, removeExtension: true);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var group = DescribeGroup(root, file);
            into.Add(new StartMenuShortcut(file, name, group, fromCurrentUser));
        }
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*" + ShortcutExtension);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] children;

            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var attributes = File.GetAttributes(child);

                // Liên kết thư mục/junction có thể làm đệ quy thành vòng lặp. Bỏ qua thay vì dựa vào bộ đếm độ sâu.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    /// <summary>Tính nhóm thuộc về của lối tắt tương đối so với thư mục gốc.</summary>
    private static string DescribeGroup(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var separator = relative.LastIndexOf(Path.DirectorySeparatorChar);

        if (separator <= 0)
        {
            return string.Empty;
        }

        // Gộp thư mục nhiều cấp thành “Cấp 1 / Cấp 2” để giao diện hiển thị trên một dòng mà vẫn giữ phân cấp.
        return relative[..separator].Replace(Path.DirectorySeparatorChar, '/');
    }
}
