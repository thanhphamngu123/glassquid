using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>Dữ liệu đọc ra từ một tệp <c>.lnk</c>.</summary>
/// <param name="Target">Đường dẫn đích, hoặc URL khi lối tắt trỏ tới trang web.</param>
/// <param name="Arguments">Tham số dòng lệnh; là chuỗi rỗng khi không có tham số.</param>
/// <param name="WorkingDirectory">Thư mục làm việc; là chuỗi rỗng khi lối tắt không ghi.</param>
/// <param name="IconSource">Nguồn biểu tượng. Khi chỉ mục là 0 thì chính là tệp đích, nếu không sẽ có dạng <c>C:\Windows\system32\shell32.dll,3</c>.</param>
/// <param name="Description">Văn bản mô tả của lối tắt, có thể rỗng.</param>
/// <param name="HasShellItem">
/// Lối tắt thực sự trỏ tới một mục shell (shell item), chỉ là mục đó không có đường dẫn tệp.
/// </param>
/// <param name="Aumid">
/// Danh tính ứng dụng được đặt tường minh trên lối tắt (<c>System.AppUserModel.ID</c>); nếu không đặt thì là null.
/// </param>
public sealed record ShortcutInfo(
    string Target,
    string Arguments,
    string WorkingDirectory,
    string IconSource,
    string Description,
    bool HasShellItem,
    string? Aumid = null)
{
    /// <summary>Đích là một địa chỉ http/https, chứ không phải đường dẫn tệp.</summary>
    public bool TargetIsUrl =>
        Uri.TryCreate(Target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Đã đọc được đường dẫn tệp hoặc URL, có thể dùng trực tiếp.
    /// </summary>
    /// <remarks>
    /// Khi là false và <see cref="HasShellItem"/> là true, bên gọi nên giữ lại chính <c>.lnk</c>
    /// làm đích — shell vẫn mở được nó, chỉ là bản thân ta không tự phân giải được nó trỏ tới đâu.
    /// </remarks>
    public bool HasResolvedTarget => !string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// Phân giải lối tắt Windows (<c>.lnk</c>).
/// </summary>
/// <remarks>
/// Đi qua <c>IShellLink</c> + <c>IPersistFile</c> của COM — để shell tự phân giải, thay vì
/// tự đi đọc định dạng nhị phân của <c>.lnk</c>. Nhờ vậy mọi trường hợp góc cạnh (đường dẫn tương đối,
/// biến môi trường, tham chiếu thư mục đã biết, đích UNC) đều do hệ thống phụ trách, ta không phải tự bảo trì một bộ phân tích định dạng riêng.
///
/// <para>
/// Khi không đọc được thì trả về <c>null</c> thay vì ném ngoại lệ: một lối tắt bị hỏng không nên làm thất bại cả đợt nhập.
/// </para>
/// </remarks>
public static class ShortcutResolver
{
    /// <summary>
    /// Dung lượng bộ đệm đường dẫn.
    /// </summary>
    /// <remarks>
    /// <c>MAX_PATH</c> là 260, nhưng đường dẫn dài (trên máy đã gỡ giới hạn 260) và kết quả sau khi
    /// mở rộng biến môi trường đều có thể dài hơn, nên chừa rộng hơn gấp đôi.
    /// </remarks>
    private const int PathCapacity = 1024;

    /// <summary>
    /// Một trình bao bọc (wrapper) dùng chung trong tiến trình.
    /// </summary>
    /// <remarks>
    /// Interop COM sinh từ mã nguồn (source-generated) cần một thực thể <c>ComWrappers</c> để biến con trỏ giao diện thành đối tượng được quản lý.
    /// Mọi lời gọi đều kèm <c>UniqueInstance</c>, nó không lưu bộ nhớ đệm, nên một thực thể có thể phục vụ toàn bộ quá trình phân giải;
    /// cứ phân giải một lối tắt lại tạo mới một thực thể thì chẳng khác nào đăng ký uổng công hàng trăm lần.
    /// </remarks>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Phân giải một <c>.lnk</c>; trả về null khi không đọc được.</summary>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        IShellLinkW? link = null;
        IPersistFile? persist = null;
        string? aumid = null;

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

            // Trình bao bọc tự AddRef một lần (đo thực tế ở đầu dò giai đoạn 3: bộ đếm tham chiếu tăng từ 1 lên 2),
            // nên tham chiếu trong tay ta vẫn giải phóng như thường, sẽ không giải phóng dư.
            link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

            try
            {
                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PersistFile, out var persistPointer);
                if (qi != Ole32.SoK || persistPointer == 0)
                {
                    return null;
                }

                persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(persistPointer, CreateObjectFlags.UniqueInstance);

                try
                {
                    persist.Load(shortcutPath, Ole32.StgmRead);
                }
                finally
                {
                    Marshal.Release(persistPointer);
                }

                // Trên ShellLink đã tải thành công, tiện thể đọc danh tính ứng dụng tường minh (System.AppUserModel.ID).
                // Cửa sổ của ứng dụng đóng gói và UWP không thuộc quyền sở hữu của tiến trình đích, danh tính này là sợi dây liên kết
                // đáng tin cậy duy nhất giữa "lối tắt ↔ cửa sổ đang chạy" (đo thực tế ở đầu dò giai đoạn 4). Không đọc được thì giữ nguyên null —
                // khớp danh tính là phần tăng cường làm hết sức có thể, tuyệt đối không phải một phần của thành bại khi nhập.
                var storeQI = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);

                if (storeQI == Ole32.SoK && storePointer != 0)
                {
                    IPropertyStore? identityStore = null;
                    try
                    {
                        identityStore = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(
                            storePointer, CreateObjectFlags.UniqueInstance);
                        aumid = WindowIdentity.ReadAumid(identityStore);
                    }
                    finally
                    {
                        Marshal.Release(storePointer);

                        if (identityStore is IDisposable disposableStore)
                        {
                            disposableStore.Dispose();
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }

            var target = Read(link, static (instance, buffer, capacity) => instance.GetPath(buffer, capacity, 0, 0));

            // Khi đích là URL thì GetPath chắc chắn trả về chuỗi rỗng — URL nằm trong danh sách ID, không nằm trong trường đường dẫn.
            // Đo thực tế cả bốn cờ (mặc định / SLGP_RAWPATH / SLGP_SHORTPATH / ưu tiên UNC) đều ra chuỗi rỗng,
            // nên chỉ có thể chuyển sang hỏi danh sách ID. Nếu không bổ sung bước này, lối tắt trang web sẽ bị coi là
            // "không đọc được đích" và bị hạ cấp, thứ người dùng thấy sẽ là một mục lối tắt tên "Trang web nào đó",
            // chứ không phải một bản ghi trang web.
            var itemIdList = ReadIdList(link);

            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = ReadUrlFromIdList(itemIdList) ?? string.Empty;
                }

                // Cả đường dẫn lẫn danh sách ID đều rỗng, chứng tỏ lối tắt này không trỏ tới bất cứ thứ gì —
                // tệp rỗng, hoặc một tệp hỏng chỉ còn lại vỏ ngoài. Thu nhận loại mục này chỉ thu được kết quả
                // "bấm vào không có phản ứng", thà báo lỗi từng mục một còn hơn.
                if (string.IsNullOrWhiteSpace(target) && itemIdList == 0)
                {
                    return null;
                }

                var arguments = Read(link, static (instance, buffer, capacity) => instance.GetArguments(buffer, capacity));
                var workingDirectory = Read(link, static (instance, buffer, capacity) => instance.GetWorkingDirectory(buffer, capacity));
                var description = Read(link, static (instance, buffer, capacity) => instance.GetDescription(buffer, capacity));

                var iconIndex = 0;
                var iconLocation = Read(link, (instance, buffer, capacity) => instance.GetIconLocation(buffer, capacity, out iconIndex));

                return new ShortcutInfo(
                    target,
                    arguments,
                    workingDirectory,
                    BuildIconSource(iconLocation, iconIndex, target),
                    description,
                    HasShellItem: itemIdList != 0,
                    Aumid: aumid);
            }
            finally
            {
                // PIDL là cấu trúc do shell cấp, bắt buộc phải giải phóng bằng ILFree, không được dùng CoTaskMemFree.
                if (itemIdList != 0)
                {
                    Shell32.ILFree(itemIdList);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ở đây cố tình hứng **tất cả** ngoại lệ, thay vì liệt kê vài kiểu cụ thể.
            //
            // Interop COM sinh từ mã nguồn sẽ ánh xạ HRESULT thất bại thành các ngoại lệ BCL cụ thể, diện ánh xạ rất rộng:
            // đo thực tế khi tệp đích không tồn tại thì <c>IPersistFile::Load</c> ném ra
            // <c>FileNotFoundException</c> (HRESULT 0x80070002), chứ không phải <c>COMException</c>.
            // Phiên bản đầu chỉ bắt bốn kiểu COMException / InvalidComObjectException / DllNotFound /
            // EntryPointNotFound, thế là ngoại lệ này xuyên thẳng qua giao ước "tuyệt đối không ném".
            //
            // Cái giá của việc bỏ sót một kiểu không phải là "báo thiếu một lỗi", mà là **cả đợt nhập bị ngoại lệ này kéo sập theo** —
            // người dùng kéo vào hai mươi tệp, một lối tắt trong đó bị hỏng, thế là mất sạch cả hai mươi mục.
            // Với một trình phân giải làm hết sức có thể mang giao ước "không đọc được thì trả về null",
            // liệt kê kiểu ngoại lệ là một canh bạc chắc chắn thua.
            //
            // Ngoại lệ duy nhất là OutOfMemoryException: lúc đó tiến trình đã không còn đáng tin cậy nữa,
            // cố nuốt nó chỉ khiến hành vi về sau càng khó lường hơn.
            _ = ex;
            return null;
        }
        finally
        {
            // Cả hai tham chiếu đều phải giải phóng: trình bao bọc giữ một lần, ta giữ một lần.
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

    /// <summary>Lấy danh sách ID của lối tắt; bên gọi chịu trách nhiệm giải phóng bằng <c>ILFree</c>.</summary>
    private static nint ReadIdList(IShellLinkW link)
    {
        link.GetIDList(out var itemIdList);
        return itemIdList;
    }

    /// <summary>
    /// Đọc URL ra từ danh sách ID của lối tắt.
    /// </summary>
    /// <remarks>
    /// Chỉ chấp nhận khi kết quả là http/https. Với lối tắt tệp thông thường, cùng một lời gọi đó sẽ trả về
    /// URL tệp dạng <c>file:///C:/...</c> — đó không phải "trang web" mà ta cần,
    /// hơn nữa tiền đề để nó xuất hiện vốn đã là trường đường dẫn đọc ra được nội dung rồi, nên ở đây chặn thêm một lớp nữa.
    /// </remarks>
    private static string? ReadUrlFromIdList(nint itemIdList)
    {
        if (itemIdList == 0)
        {
            return null;
        }

        nint name = 0;

        try
        {
            var hr = Shell32.SHGetNameFromIDList(itemIdList, Shell32.SigdnUrl, out name);
            if (hr != Ole32.SoK || name == 0)
            {
                return null;
            }

            var url = Marshal.PtrToStringUni(name);

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? url
                    : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (name != 0)
            {
                Ole32.CoTaskMemFree(name);
            }
        }
    }

    /// <summary>
    /// Ghép vị trí và chỉ mục từ <c>IShellLink::GetIconLocation</c> thành dạng có thể trích xuất lại được.
    /// </summary>
    /// <remarks>
    /// Khi chỉ mục khác 0 thì bắt buộc phải mang theo chỉ mục. Chỉ mục có nghĩa là "biểu tượng thứ mấy trong tệp đó",
    /// <c>C:\Windows\system32\shell32.dll,3</c> và <c>shell32.dll</c> là hai chuyện hoàn toàn khác nhau —
    /// cách sau sẽ lấy phải biểu tượng thứ 0 của shell32, hoàn toàn không phải biểu tượng mà người dùng nhìn thấy.
    /// </remarks>
    private static string BuildIconSource(string iconLocation, int iconIndex, string target)
    {
        var location = string.IsNullOrWhiteSpace(iconLocation) ? target : iconLocation;

        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        return iconIndex == 0 ? location : $"{location},{iconIndex}";
    }

    /// <summary>Gọi một phương thức "ghi chuỗi" trên bộ đệm không được quản lý, rồi đọc ngược lại thành chuỗi được quản lý.</summary>
    private static string Read(IShellLinkW link, Action<IShellLinkW, nint, int> read)
    {
        var byteCount = PathCapacity * sizeof(char);
        var buffer = Marshal.AllocHGlobal(byteCount);

        try
        {
            // Xóa về 0 trước, nhờ vậy dù phương thức không ghi gì cả thì đọc lại vẫn là chuỗi rỗng chứ không phải rác.
            unsafe
            {
                new Span<byte>((void*)buffer, byteCount).Clear();
            }

            read(link, buffer, PathCapacity);

            // Các phương thức này bảo đảm kết thúc bằng null, nên chỉ cần đọc tới ký tự kết thúc là được.
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}