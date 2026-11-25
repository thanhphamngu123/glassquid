using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Nội dung cần điền để ghi một tệp <c>.lnk</c>.</summary>
/// <param name="TargetPath">Đường dẫn đích hoặc URL.</param>
/// <param name="Arguments">Tham số dòng lệnh; để trống nếu không có.</param>
/// <param name="WorkingDirectory">Thư mục làm việc; để trống nếu không có.</param>
/// <param name="IconLocation">Vị trí biểu tượng, dạng <c>đường_dẫn,chỉ_mục</c>; để trống nếu không có (dùng biểu tượng của đích).</param>
/// <param name="Description">Văn bản mô tả; để trống nếu không có.</param>
/// <param name="Aumid">
/// Danh tính ứng dụng ghi tường minh (<c>System.AppUserModel.ID</c>); để trống nếu không có.
/// Mẫu nghiệm thu giai đoạn 4 cần dùng nó để tạo bản thay thế cho "lối tắt ứng dụng đóng gói".
/// </param>
public sealed record ShortcutDefinition(
    string TargetPath,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconLocation = "",
    string Description = "",
    string Aumid = "");

/// <summary>
/// Tạo một tệp <c>.lnk</c>.
/// </summary>
/// <remarks>
/// Luồng chạy chính của sản phẩm <strong>không ghi</strong> lối tắt — việc nhập chỉ đọc không ghi. Kiểu này dùng để
/// tạo mẫu kiểm thử: các mẫu yêu cầu nghiệm thu như "lối tắt chứa chữ Hán, khoảng trắng, đường dẫn không hợp lệ, có tham số"
/// phải có thể được tạo lặp lại được, còn tạo thủ công bằng chuột phải trong Explorer vừa không tái lập được,
/// vừa không thể giao cho script.
///
/// <para>
/// Đi qua cùng một bộ giao diện COM với <see cref="ShortcutResolver"/>, chỉ khác là gọi <c>Set*</c> và
/// <c>IPersistFile::Save</c>. Nhờ vậy cách ghi và cách đọc mẫu dùng chung một khai báo,
/// không xảy ra tình trạng "mẫu kiểm thử được tạo bằng cách khác nên không kiểm tra được tình huống thực tế".
/// </para>
/// </remarks>
public static class ShortcutWriter
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Ghi một định nghĩa thành <paramref name="shortcutPath"/>; thất bại trả về false.</summary>
    public static bool Write(string shortcutPath, ShortcutDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentNullException.ThrowIfNull(definition);

        IShellLinkW? link = null;
        IPersistFile? persist = null;

        try
        {
            var hr = Ole32.CoCreateInstance(
                in ShellComIds.ShellLinkClass,
                0,
                Ole32.ClsCtxInprocServer,
                in ShellComIds.ShellLinkW,
                out var shellLinkPointer);

            if (hr != Ole32.SoK || shellLinkPointer == 0)
            {
                return false;
            }

            link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

            try
            {
                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PersistFile, out var persistPointer);
                if (qi != Ole32.SoK || persistPointer == 0)
                {
                    return false;
                }

                persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(persistPointer, CreateObjectFlags.UniqueInstance);

                try
                {
                    link.SetPath(definition.TargetPath);

                    if (!string.IsNullOrEmpty(definition.Arguments))
                    {
                        link.SetArguments(definition.Arguments);
                    }

                    if (!string.IsNullOrEmpty(definition.WorkingDirectory))
                    {
                        link.SetWorkingDirectory(definition.WorkingDirectory);
                    }

                    if (!string.IsNullOrEmpty(definition.Description))
                    {
                        link.SetDescription(definition.Description);
                    }

                    if (!string.IsNullOrEmpty(definition.IconLocation))
                    {
                        // Vị trí biểu tượng có dạng "đường_dẫn,chỉ_mục", nhưng SetIconLocation cần hai phần tách rời.
                        var (iconPath, iconIndex) = IconExtractor.SplitIconSource(definition.IconLocation);
                        link.SetIconLocation(iconPath, iconIndex ?? 0);
                    }

                    if (!string.IsNullOrEmpty(definition.Aumid))
                    {
                        // Danh tính tường minh đi qua kho thuộc tính: sau SetValue bắt buộc phải Commit, nếu không nội dung sẽ không được ghi xuống đĩa.
                        var storeQI = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);
                        if (storeQI != Ole32.SoK || storePointer == 0)
                        {
                            return false;
                        }

                        IPropertyStore? store = null;
                        try
                        {
                            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(
                                storePointer, CreateObjectFlags.UniqueInstance);

                            var text = Marshal.StringToCoTaskMemUni(definition.Aumid);
                            if (text == 0)
                            {
                                return false;
                            }

                            try
                            {
                                var value = new PropVariant
                                {
                                    Type = VarType.Lpwstr,
                                    Pointer = text,
                                };

                                // Quy ước của SetValue: trước khi Commit, kho chỉ tham chiếu vùng đệm này,
                                // quyền sở hữu vẫn thuộc về bên gọi, nên giải phóng thống nhất trong finally.
                                if (store.SetValue(in PropertyKeys.AppUserModelId, in value) != Ole32.SoK
                                    || store.Commit() != Ole32.SoK)
                                {
                                    return false;
                                }
                            }
                            finally
                            {
                                Ole32.CoTaskMemFree(text);
                            }
                        }
                        finally
                        {
                            Marshal.Release(storePointer);

                            if (store is IDisposable disposableStore)
                            {
                                disposableStore.Dispose();
                            }
                        }
                    }

                    // Tham số thứ hai true biểu thị ghi ngay xuống đĩa, thay vì đợi đối tượng được giải phóng.
                    persist.Save(shortcutPath, true);
                }
                finally
                {
                    Marshal.Release(persistPointer);
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tương tự ShortcutResolver: các kiểu ngoại lệ của lớp COM không thể liệt kê hết, mà ở đây là
            // câu hỏi đúng/sai "có ghi được mẫu hay không", dùng giá trị trả về để biểu đạt là đủ.
            _ = ex;
            return false;
        }
        finally
        {
            if (persist is IDisposable disposablePersist)
            {
                disposablePersist.Dispose();
            }

            if (link is IDisposable disposableLink)
            {
                disposableLink.Dispose();
            }
        }
    }
}