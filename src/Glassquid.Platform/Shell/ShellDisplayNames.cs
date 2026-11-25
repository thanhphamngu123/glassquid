using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Đọc tên hiển thị trong Explorer, bao gồm tên đã bản địa hóa trong desktop.ini.</summary>
public static class ShellDisplayNames
{
    public static string GetFileName(string path, bool removeExtension = false)
    {
        var name = Read(path) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(name)) return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Chỉ bỏ phần mở rộng thực tế của tệp nguồn; các đoạn chữ như v2.0 trong tên đã bản địa hóa phải được giữ nguyên.
        var extension = Path.GetExtension(path);
        if (removeExtension && extension.Length > 0 && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        return name;
    }

    private static string? Read(string path)
    {
        nint itemIdList = 0, name = 0;
        try
        {
            if (Shell32.SHParseDisplayName(path, 0, out itemIdList, 0, out _) != Ole32.SoK || itemIdList == 0)
                return null;
            // SIGDN_NORMALDISPLAY = 0: khớp với chuỗi hiển thị bên dưới biểu tượng trong Explorer.
            if (Shell32.SHGetNameFromIDList(itemIdList, 0, out name) != Ole32.SoK || name == 0)
                return null;
            var value = Marshal.PtrToStringUni(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (name != 0) Ole32.CoTaskMemFree(name);
            if (itemIdList != 0) Shell32.ILFree(itemIdList);
        }
    }
}
