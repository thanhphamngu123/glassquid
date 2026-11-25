using System.Runtime.InteropServices;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Các điểm vào cho vùng thông báo (khay hệ thống) của Shell, trạng thái thông báo người dùng và trạng thái thanh tác vụ, cùng các struct được trao đổi giữa chúng.
/// </summary>
/// <remarks>
/// Các struct sử dụng bộ đệm ký tự kích thước cố định thay vì
/// marshaling <c>ByValTStr</c> để giữ tính blittable. Nhờ vậy P/Invoke sinh từ mã nguồn
/// mới có thể hoạt động, đồng thời tránh chi phí cấp phát bộ nhớ ở mỗi lần gọi từ bộ marshaler thời chạy.
/// </remarks>
internal static partial class Shell32
{
    /// <summary>Thông điệp được Shell sử dụng khi gửi sự kiện khay hệ thống tới cửa sổ của chúng ta.</summary>
    internal const int WmTrayCallback = 0x0400 + 1024; // WM_APP + 1024

    // Các cờ vùng thông báo (NOTIFY_ICON_* / NIF_*).
    internal const int NifMessage = 0x00000001;
    internal const int NifIcon = 0x00000002;
    internal const int NifTip = 0x00000004;

    // Các thông điệp của Shell_NotifyIcon.
    internal const int NimAdd = 0x00000000;
    internal const int NimModify = 0x00000001;
    internal const int NimDelete = 0x00000002;
    internal const int NimSetVersion = 0x00000004;

    // Thông báo chuột được truyền qua lParam theo ngữ nghĩa phiên bản 3.
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmLButtonDblClk = 0x0203;

    /// <summary>Phiên bản biểu tượng vùng thông báo có thể đưa thông tin nhấp chuột vào lParam.</summary>
    internal const int NotifyIconVersion3 = 3;

    /// <summary>Số ký tự tối đa của văn bản chú thích biểu tượng được Shell chấp nhận.</summary>
    internal const int TipLength = 128;

    /// <summary>Truy vấn trạng thái thanh tác vụ (một trong các thông điệp của <c>SHAppBarMessage</c>).</summary>
    internal const uint AbmGetState = 0x00000004;

    /// <summary>Thiết lập trạng thái tự động ẩn của thanh tác vụ Windows.</summary>
    internal const uint AbmSetState = 0x0000000A;

    /// <summary>Thanh tác vụ đang ở chế độ tự động ẩn. Bit này trong giá trị trả về quyết định xem có phân biệt được "phủ kín toàn màn hình" với "phóng to tối đa" hay không.</summary>
    internal const uint AbsAutoHide = 0x00000001;

    /// <summary>Thanh tác vụ luôn nằm trên cùng.</summary>
    internal const uint AbsAlwaysOnTop = 0x00000002;

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    /// <summary>
    /// Hỏi Shell xem "lúc này có nên tránh làm phiền người dùng hay không", tức là đúng nhánh mà Windows dùng để xác định ứng dụng toàn màn hình.
    /// </summary>
    /// <remarks>
    /// Trả về HRESULT, giá trị được trả lại qua <paramref name="state"/>; giá trị âm biểu thị lời gọi thất bại.
    /// Đã biết hàm này có thể trả về <c>E_FAIL</c> (ví dụ khi Shell chưa khởi tạo xong), do đó bên gọi phải
    /// coi lỗi là "nhánh này chưa có kết luận", thay vì coi là ngoại lệ.
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out int state);

    /// <summary>Truy vấn trạng thái thanh tác vụ. <see cref="AppBarData.Size"/> phải được điền trước.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    internal static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    // ---- Thư mục đã biết ----

    /// <summary>Lấy đường dẫn của thư mục đã biết. Bộ nhớ trả về phải được giải phóng bằng <see cref="Ole32.CoTaskMemFree"/>.</summary>
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    /// <summary>Thư mục chương trình trong menu Start của người dùng hiện tại (<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>).</summary>
    internal static readonly Guid FolderIdPrograms = new("a77f5d77-2e2b-44c3-a6a2-aba601054a51");

    /// <summary>Thư mục chương trình trong menu Start cho tất cả người dùng (<c>%ProgramData%\...\Start Menu\Programs</c>).</summary>
    internal static readonly Guid FolderIdCommonPrograms = new("0139d44e-6afe-49f2-8690-3dafcae6ffb8");

    // ---- Biểu tượng ----

    /// <summary>Lấy handle biểu tượng. Handle trả về phải được giải phóng bằng <c>DestroyIcon</c>.</summary>
    internal const uint ShgfiIcon = 0x000000100;

    /// <summary>Lấy kích thước "biểu tượng lớn" của hệ thống, đo thực tế là 32×32 (co giãn theo DPI).</summary>
    internal const uint ShgfiLargeIcon = 0x000000000;

    /// <summary>Vẫn suy luận loại biểu tượng theo phần mở rộng ngay cả khi đường dẫn không tồn tại, thay vì thất bại ngay.</summary>
    internal const uint ShgfiUseFileAttributes = 0x000000010;

    /// <summary>Yêu cầu một biểu tượng 32-bit có kênh alpha, thay vì bản 16 màu hoặc bản mặt nạ.</summary>
    internal const uint ShgfiShellIconSize = 0x000000000;

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags);

    /// <summary>
    /// Lấy biểu tượng theo chỉ mục từ tệp thực thi hoặc DLL, tương ứng với dạng
    /// <c>C:\Windows\system32\shell32.dll,3</c> trong vị trí biểu tượng của lối tắt.
    /// </summary>
    /// <remarks>
    /// Cả hai handle trả về đều phải được giải phóng bằng <c>DestroyIcon</c>. Trả về 0 nghĩa là không có chỉ mục này.
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHDefExtractIcon(string file, int index, uint flags, out nint large, nint small, uint iconSize);

    // ---- Lấy mục Shell và hình ảnh ----

    /// <summary>Lấy mục Shell theo đường dẫn và yêu cầu trực tiếp interface chỉ định.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out nint instance);

    /// <summary>
    /// Yêu cầu trả về tên của mục Shell dưới dạng URL (<c>SIGDN_URL</c>).
    /// </summary>
    /// <remarks>
    /// Đây là cách duy nhất để đọc ra lối tắt có "đích là địa chỉ web". Thực tế kiểm tra cho thấy
    /// <c>IShellLink::GetPath</c> dưới cả bốn cờ <strong>đều trả về chuỗi rỗng</strong> —
    /// địa chỉ web được lưu trong danh sách ID, không nằm ở trường đường dẫn. Xem <c>ShortcutResolver</c>.
    /// </remarks>
    internal const uint SigdnUrl = 0x80068000;

    /// <summary>
    /// Chuyển PIDL thành một chuỗi tên.
    /// </summary>
    /// <remarks>
    /// Chuỗi trả về do trình cấp phát tác vụ cấp, cần giải phóng bằng <c>CoTaskMemFree</c>.
    /// PIDL truyền vào thuộc quyền sở hữu của bên gọi, hàm này không giải phóng nó.
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "SHGetNameFromIDList")]
    internal static partial int SHGetNameFromIDList(nint itemIdList, uint sigdn, out nint name);

    /// <summary>
    /// Yêu cầu Shell phân giải một tên đích (bao gồm dạng <c>shell:</c>), trả về PIDL khi thành công.
    /// </summary>
    /// <remarks>
    /// Chỉ phân giải, không mở: dùng để xác minh "Shell có nhận ra đích này không" mà không mở cửa sổ trên màn hình người dùng,
    /// cũng không bật hộp thoại lỗi khi đích không hợp lệ như <c>ShellExecute</c>.
    /// PIDL trả về phải được giải phóng bằng <see cref="ILFree"/>.
    /// </remarks>
    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(string name, nint bindContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

    /// <summary>Giải phóng PIDL do Shell cấp phát.</summary>
    /// <remarks>
    /// Bắt buộc phải dùng hàm này thay vì <c>CoTaskMemFree</c>: PIDL là cấu trúc do chính Shell tổ chức,
    /// dùng nhầm hàm giải phóng sẽ làm hỏng heap.
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial void ILFree(nint itemIdList);

    // ---- Thực thi ----

    /// <summary>Bit <c>fMask</c> của <c>SHELLEXECUTEINFO</c>.</summary>
    internal const uint SeeMaskNoCloseProcess = 0x00000040;

    /// <summary>Không để Shell tự bật hộp thoại lỗi — thông báo do chúng ta đưa ra để câu chữ thống nhất.</summary>
    internal const uint SeeMaskFlagNoUi = 0x00000400;

    /// <summary>Không bật hộp thoại "Không tìm thấy tệp" khi đích không tồn tại.</summary>
    internal const int SeErrSuccess = 0;

    /// <summary>Không tìm thấy tệp.</summary>
    internal const int SeErrFileNotFound = 2;

    /// <summary>Không tìm thấy liên kết (chúng ta coi đây là "không biết cách mở").</summary>
    internal const int SeErrNoAssociation = 31;

    /// <summary>Từ chối truy cập.</summary>
    internal const int SeErrAccessDenied = 5;

    /// <summary>Người dùng đã hủy lời nhắc UAC.</summary>
    internal const int SeErrCancelled = 1223;

    /// <summary>Mở một tệp, thư mục hoặc địa chỉ web thông qua liên kết của Shell.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellExecuteEx(ref ShellExecuteInfo info);

    /// <summary>
    /// <c>SHELLEXECUTEINFOW</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Số lượng trường phải khớp hoàn toàn với bản native.</b> Phiên bản đầu tiên đã viết thừa sau <c>hkeyClass</c>
    /// một "nửa HKLM" cùng tên — bản native <strong>không có</strong> trường này,
    /// bản thân <c>hkeyClass</c> đã là một <c>HKEY</c> (con trỏ). 8 byte dư ra đã đẩy
    /// <c>dwHotKey</c> và <c>hProcess</c> phía sau lùi xuống một ô,
    /// khiến <c>cbSize</c> báo thành 120 (đáng lẽ là 112) và handle tiến trình luôn đọc ra 0 —
    /// kết quả là "khởi chạy có vẻ thất bại, không lấy được pid", trong khi nhìn tại điểm gọi lại hoàn toàn không thấy vấn đề.
    /// </para>
    /// <para>
    /// Bố cục trên x64: cbSize 0, fMask 4, hwnd 8, lpVerb 16, lpFile 24,
    /// lpParameters 32, lpDirectory 40, nShow 48, hInstApp 56, lpIDList 64,
    /// lpClass 72, hkeyClass 80, dwHotKey 88, hIcon/hMonitor 96, hProcess 104, tổng cộng 112 byte.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal nint Window;
        internal nint Verb;
        internal nint File;
        internal nint Parameters;
        internal nint Directory;
        internal int Show;
        internal nint InstApp;
        internal nint IdList;
        internal nint ClassName;
        internal nint ClassKey;
        internal uint HotKey;
        internal nint IconOrMonitor;
        internal nint Process;
    }

    /// <summary>
    /// <c>SHFILEINFOW</c>.
    /// </summary>
    /// <remarks>
    /// Struct này <strong>không có</strong> trường <c>cbSize</c>; độ dài được truyền riêng qua
    /// tham số <c>cbFileInfo</c> của <c>SHGetFileInfo</c>. Đầu dò ở giai đoạn 3
    /// ban đầu theo thói quen của các struct khác đã thêm một <c>int Size</c> ở đầu,
    /// kết quả là toàn bộ bố cục bị dịch xuống 4 byte, handle biểu tượng đọc ra là giá trị sai rõ rệt như
    /// <c>0x4000014500000032</c>, trong khi giá trị trả về vẫn là "thành công".
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ShellFileInfo
    {
        internal nint Icon;
        internal int IconIndex;
        internal uint Attributes;

        internal fixed char DisplayName[260];
        internal fixed char TypeName[80];
    }

    /// <summary>
    /// Lấy kho lưu trữ thuộc tính của một cửa sổ, dùng để đọc <c>System.AppUserModel.ID</c> được thiết lập tường minh trên cửa sổ.
    /// </summary>
    /// <remarks>
    /// Giá trị trả về là kho thuộc tính <strong>tường minh</strong>: các thuộc tính do chính cửa sổ (hoặc framework chủ) thiết lập.
    /// "Danh tính ngầm định" do hệ thống suy ra theo danh tính gói không nằm trong kho này — việc cửa sổ ứng dụng đóng gói
    /// có vì thế mà không đọc được hay không, và khi không đọc được có phải chuyển sang dùng
    /// <c>GetApplicationUserModelId</c> phía tiến trình hay không, sẽ do kết quả đo thực tế của đầu dò giai đoạn 4 quyết định, không đoán theo cảm tính.
    /// Con trỏ interface trả về thuộc quyền sở hữu của bên gọi, dùng xong phải gọi <c>Marshal.Release</c>.
    /// </remarks>
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId, out nint store);

    /// <summary><c>APPBARDATA</c>. Khi truy vấn trạng thái thanh tác vụ chỉ dùng đến <see cref="Size"/> và giá trị trả về.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        internal uint Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal Rect Rectangle;
        internal nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NotifyIconData
    {
        internal int Size;
        internal nint WindowHandle;
        internal int Id;
        internal int Flags;
        internal int CallbackMessage;
        internal nint IconHandle;

        internal fixed char Tip[TipLength];

        internal int State;
        internal int StateMask;

        internal fixed char Info[256];

        /// <summary>
        /// Union của <c>uTimeout</c> và <c>uVersion</c>; chúng ta chỉ dùng nửa phiên bản.
        /// </summary>
        internal int VersionOrTimeout;

        internal fixed char InfoTitle[64];
        internal int InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIconHandle;

        /// <summary>
        /// Sao chép chuỗi được quản lý vào một bộ đệm cố định, cắt bớt nếu quá dài,
        /// và luôn giữ ký tự null kết thúc.
        /// </summary>
        internal static void SetText(char* destination, int capacity, string? value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

            if (string.IsNullOrEmpty(value))
            {
                destination[0] = '\0';
                return;
            }

            var length = Math.Min(value.Length, capacity - 1);
            for (var i = 0; i < length; i++)
            {
                destination[i] = value[i];
            }

            destination[length] = '\0';
        }
    }
}
