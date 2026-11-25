using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Đọc "danh tính ứng dụng" (AppUserModelID) của một cửa sổ hoặc tiến trình.
/// </summary>
/// <remarks>
/// Việc xác định cửa sổ thuộc về ứng dụng nào theo đường dẫn ảnh tiến trình (xem <see cref="WindowLocator"/>) không hiệu quả với ba loại ứng dụng:
/// ứng dụng console (cửa sổ thuộc về WindowsTerminal), stub trong System32 (cửa sổ thuộc về bản đóng gói),
/// và UWP (cửa sổ thuộc về ApplicationFrameHost). Điểm chung của chúng là danh tính không còn bằng
/// "đường dẫn ảnh tiến trình sở hữu cửa sổ", mà hệ thống cấp cho chúng danh tính AppUserModelID.
///
/// <para>
/// Danh tính có hai nguồn, việc có khớp được cả hai hay không phụ thuộc vào kết quả đo thực tế từ đầu dò:
/// <list type="number">
/// <item>Phía cửa sổ <c>SHGetPropertyStoreForWindow</c> — đọc danh tính được <strong>thiết lập tường minh</strong> trên cửa sổ;</item>
/// <item>Phía tiến trình <c>GetApplicationUserModelId</c> — chỉ có hiệu lực với tiến trình có danh tính gói.</item>
/// </list>
/// Nếu cả hai đường đều không đọc được thì trả về <c>null</c>, bên gọi sẽ quay lại khớp theo đường dẫn ảnh.
/// </para>
/// </remarks>
public static class WindowIdentity
{
    /// <summary>Trình bọc dùng chung trong một tiến trình. Xem chú thích của trường cùng tên trong <see cref="ShortcutResolver"/> để biết lý do.</summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Dung lượng ban đầu của bộ đệm chuỗi danh tính. Hầu hết AUMID ngắn hơn 128 ký tự.</summary>
    private const int IdentityCapacity = 512;

    /// <summary>
    /// Đọc danh tính ứng dụng được thiết lập tường minh trên cửa sổ; trả về <c>null</c> nếu chưa thiết lập.
    /// </summary>
    /// <remarks>
    /// Trả về <c>null</c> không có nghĩa là "ứng dụng này không có danh tính" — chỉ có nghĩa là trong kho thuộc tính của cửa sổ
    /// không có mục tường minh đó. Việc có thể đọc được danh tính ngầm định (suy ra từ danh tính gói) từ cửa sổ hay không
    /// sẽ dựa trên kết quả đo thực tế từ đầu dò.
    /// </remarks>
    public static string? TryGetWindowAumid(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        IPropertyStore? store = null;

        try
        {
            var hr = Shell32.SHGetPropertyStoreForWindow(window, in ShellComIds.PropertyStore, out var pointer);
            if (hr != Ole32.SoK || pointer == 0)
            {
                return null;
            }

            // Trình bọc sẽ tự gọi AddRef một lần (cùng mẫu với ShortcutResolver),
            // nên tham chiếu giao diện đang giữ ở đây vẫn được giải phóng bình thường.
            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);

            return ReadAumid(store);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cùng lý do với ShortcutResolver: nếu không đọc được kho thuộc tính thì trả về null,
            // để bên gọi quay lại đường dẫn khớp khác, tuyệt đối không ném ngoại lệ lên trên.
            _ = ex;
            return null;
        }
        finally
        {
            if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Đọc danh tính ứng dụng của tiến trình đóng gói; tiến trình Win32 thông thường trả về <c>null</c>.
    /// </summary>
    public static string? TryGetProcessAumid(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[IdentityCapacity];
            uint length = (uint)buffer.Length;

            var hr = Kernel32.GetApplicationUserModelId(handle, ref length, buffer);

            // Nếu dung lượng không đủ thì hỏi lại theo độ dài do hệ thống trả về, vẫn theo hướng cố gắng hết sức.
            if (hr == Ole32.EInsufficientBuffer && length is > 0 and < 4096)
            {
                buffer = new char[length];
                length = (uint)buffer.Length;
                hr = Kernel32.GetApplicationUserModelId(handle, ref length, buffer);
            }

            if (hr != Ole32.SoK)
            {
                // Tiến trình thông thường trả về APPMODEL_ERROR_NO_APPLICATION tại đây — đây không phải lỗi,
                // mà là câu trả lời chính thức rằng "nó không có danh tính", bên gọi dựa vào đó để quay lại khớp theo đường dẫn ảnh.
                return null;
            }

            var value = new string(buffer[..(int)Math.Min(length, (uint)buffer.Length)]).TrimEnd('\0');
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Đọc <c>System.AppUserModel.ID</c> được thiết lập tường minh trên <c>.lnk</c>; trả về <c>null</c> nếu chưa thiết lập.
    /// </summary>
    /// <remarks>
    /// Lối tắt trên menu Start của ứng dụng đóng gói và UWP phần lớn đều mang danh tính tường minh; đây là
    /// mối liên kết ổn định duy nhất giữa lối tắt và cửa sổ đang chạy — cửa sổ của loại đích này không thuộc về
    /// tiến trình mà lối tắt trỏ tới, nên việc khớp theo đường dẫn ảnh chắc chắn thất bại.
    /// Nếu không đọc được thì trả về <c>null</c>, không ném ngoại lệ (cùng lý do với <see cref="ShortcutResolver"/>).
    /// </remarks>
    public static string? TryGetShortcutAumid(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        IShellLinkW? link = null;
        IPropertyStore? store = null;

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
                return null;
            }

            try
            {
                link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);
                if (qi != Ole32.SoK || storePointer == 0)
                {
                    return null;
                }

                try
                {
                    store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(storePointer, CreateObjectFlags.UniqueInstance);

                    // Danh tính chỉ có ý nghĩa "sau khi tải": ShellLink chưa tải thì không chứa nội dung tệp.
                    var persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(
                        shellLinkPointer, CreateObjectFlags.UniqueInstance);

                    try
                    {
                        persist.Load(shortcutPath, Ole32.StgmRead);
                    }
                    finally
                    {
                        if (persist is IDisposable disposablePersist)
                        {
                            disposablePersist.Dispose();
                        }
                    }

                    return ReadAumid(store);
                }
                finally
                {
                    Marshal.Release(storePointer);
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return null;
        }
        finally
        {
            if (link is IDisposable disposableLink)
            {
                disposableLink.Dispose();
            }

            if (store is IDisposable disposableStore)
            {
                disposableStore.Dispose();
            }
        }
    }

    /// <summary>Đọc <c>System.AppUserModel.ID</c> từ kho thuộc tính.</summary>
    /// <remarks>
    /// internal: trình phân giải lối tắt (<see cref="ShortcutResolver"/>) dùng lại hàm này trên
    /// ShellLink đã được tải sẵn để đọc danh tính tường minh, tránh phải tải lại tệp chỉ để lấy một thuộc tính.
    /// </remarks>
    internal static string? ReadAumid(IPropertyStore store)
    {
        var hr = store.GetValue(in PropertyKeys.AppUserModelId, out var value);

        if (hr != Ole32.SoK || value.Type == VarType.Empty || value.Pointer == 0)
        {
            return null;
        }

        if (value.Type != VarType.Lpwstr)
        {
            // Kiểu dữ liệu ngoài dự kiến. Giá trị không phải chuỗi thì không dùng làm danh tính, và con trỏ cũng không thuộc quyền quản lý của ta.
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(value.Pointer);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            // Bộ đệm của VT_LPWSTR do trình cấp phát thuộc tính cấp, bắt buộc phải giải phóng bằng CoTaskMemFree.
            Ole32.CoTaskMemFree(value.Pointer);
        }
    }
}
