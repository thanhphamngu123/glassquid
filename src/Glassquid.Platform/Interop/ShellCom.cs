using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Glassquid.Platform.Interop;

/// <summary>
/// Các giao diện COM của Shell: lối tắt và lấy biểu tượng theo kích thước.
/// </summary>
/// <remarks>
/// Dùng COM interop được tạo từ nguồn (<c>[GeneratedComInterface]</c>) thay vì
/// <c>[ComImport]</c>, nhất quán với quy ước "ưu tiên tạo từ nguồn" của các khai báo còn lại trong thư mục này,
/// và cũng không yêu cầu tiến trình bật COM interop tích hợp.
///
/// <para>
/// <b>Bài học kinh nghiệm: các phương thức trả về <c>int</c> phải gắn <c>[PreserveSig]</c>.</b>
/// Đo đạc thực tế ở giai đoạn 3 cho thấy khi <c>IShellItemImageFactory.GetImage</c> không gắn thuộc tính này, giá trị trả về là
/// rác (thực tế 0x04F9C058, 0x94971370), còn tham số đầu ra <c>bitmap</c> hoàn toàn không được ghi;
/// sau khi gắn vào thì kết quả khớp từng pixel với nhóm đối chứng <c>[ComImport]</c>.
/// </para>
///
/// <para>
/// Thứ tự phương thức chính là thứ tự slot trong vtable, do đó <strong>không được chỉ khai báo những phương thức cần dùng</strong> —
/// thiếu một phương thức là các phương thức phía sau sẽ rơi nhầm vào slot sai mà không báo lỗi.
/// </para>
/// </remarks>
internal static class ShellComIds
{
    /// <summary>Lớp thành phần ShellLink.</summary>
    internal static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static readonly Guid ShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    internal static readonly Guid PersistFile = new("0000010B-0000-0000-C000-000000000046");

    internal static readonly Guid ShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Mã định danh giao diện của <c>IPropertyStore</c>.</summary>
    internal static readonly Guid PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
}

/// <summary>Khóa của hệ thống thuộc tính: ID định dạng cộng với mã số thuộc tính.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    internal PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    internal Guid FormatId;

    internal int PropertyId;
}

/// <summary>Khóa của thuộc tính <c>System.AppUserModel.ID</c>.</summary>
/// <remarks>
/// "Định danh ứng dụng" được thiết lập tường minh trên lối tắt và cửa sổ. Thanh tác vụ và menu Start
/// dùng chính thuộc tính này để gom cửa sổ, lối tắt và thông báo về cùng một ứng dụng.
/// </remarks>
internal static class PropertyKeys
{
    internal static readonly PropertyKey AppUserModelId = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
}

/// <summary>Các thẻ kiểu trong <c>VARENUM</c> mà sản phẩm này sử dụng.</summary>
internal static class VarType
{
    /// <summary>Không có giá trị.</summary>
    internal const short Empty = 0;

    /// <summary>Chuỗi Unicode kết thúc bằng null, bộ đệm do bộ cấp phát thuộc tính quản lý.</summary>
    internal const short Lpwstr = 31;
}

/// <summary>Win32 <c>PROPVARIANT</c>.</summary>
/// <remarks>
/// <b>Số lượng trường và tổng kích thước phải khớp với mã gốc.</b> Trên x64, kích thước gốc là 24 byte: 2 byte thẻ kiểu
/// cộng 6 byte dự phòng, thêm một union 16 byte. Ở đây dùng hai <see cref="nint"/>
/// để lấp đủ kích thước đầy đủ của union — nếu khai báo thiếu một nửa (ví dụ chỉ đặt một con trỏ, tổng cộng 16 byte),
/// phía được gọi vẫn ghi 24 byte, 8 byte dư sẽ ghi đè vào bộ nhớ của bên gọi,
/// trong khi GetValue vẫn trả về "thành công". Đây chính là loại lỗi về struct đã từng vấp phải ở giai đoạn 3.
/// Sau khi đọc <c>VT_LPWSTR</c>, con trỏ nằm ở <see cref="Pointer"/> và phải giải phóng bằng
/// <c>CoTaskMemFree</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    /// <summary><c>VARENUM</c>. <c>VT_EMPTY</c>=0 (không có giá trị), <c>VT_LPWSTR</c>=31 (chuỗi Unicode).</summary>
    internal short Type;

    private short _reserved1;

    private short _reserved2;

    private short _reserved3;

    /// <summary>Nửa đầu của union: con trỏ chuỗi, số nguyên 64-bit hoặc con trỏ giao diện.</summary>
    internal nint Pointer;

    /// <summary>Nửa sau của union: chỉ để lấp đủ 16 byte của union gốc, không dùng trực tiếp.</summary>
    private nint _unionTail;
}

/// <summary><c>IShellLinkW</c>: đọc đích, tham số, thư mục làm việc và vị trí biểu tượng của một tệp <c>.lnk</c>.</summary>
/// <remarks>
/// Cũng dùng để ghi lối tắt — mẫu kiểm thử cần tạo lại được một cách lặp lại, xem <c>ShortcutWriter</c>.
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint fileOut, int maxPath, nint findData, uint flags);

    void GetIDList(out nint itemIdList);

    void SetIDList(nint itemIdList);

    void GetDescription(nint nameOut, int maxName);

    void SetDescription(string name);

    void GetWorkingDirectory(nint dirOut, int maxPath);

    void SetWorkingDirectory(string dir);

    void GetArguments(nint argsOut, int maxPath);

    void SetArguments(string args);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    /// <summary>
    /// Đọc vị trí biểu tượng. Khi chỉ số bằng 0 nghĩa là "dùng biểu tượng của chính tệp đích",
    /// khác 0 nghĩa là biểu tượng thứ N trong tệp tại <paramref name="iconOut"/> (thường là dll hoặc exe).
    /// </summary>
    void GetIconLocation(nint iconOut, int maxPath, out int iconIndex);

    void SetIconLocation(string iconPath, int iconIndex);

    void SetRelativePath(string pathRel, uint reserved);

    void Resolve(nint window, uint flags);

    void SetPath(string path);
}

/// <summary><c>IPersistFile</c>: gắn kết <c>IShellLink</c> với tệp <c>.lnk</c> trên đĩa.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);

    /// <summary>Trả về <c>S_FALSE</c> nghĩa là chưa bị sửa đổi, nên phải giữ nguyên giá trị trả về gốc thay vì để nó ném ngoại lệ.</summary>
    [PreserveSig]
    int IsDirty();

    void Load(string fileName, uint mode);

    void Save(string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted(string fileName);

    void GetCurFile(out nint fileName);
}

/// <summary>
/// <c>IShellItemImageFactory</c>: lấy ảnh thu nhỏ hoặc biểu tượng Shell theo kích thước yêu cầu.
/// </summary>
/// <remarks>
/// Điểm mạnh hơn <c>SHGetFileInfo</c> là kích thước: hàm kia chỉ trả về kích thước "biểu tượng lớn" của hệ thống
/// (đo thực tế 32×32), phóng to trên thanh dock DPI cao sẽ bị mờ. Giao diện này đo thực tế cho ra được 256×256.
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    /// <summary>
    /// <c>SIZE</c> phải được truyền <strong>nguyên khối</strong>. Đo thực tế cho thấy tách nó thành hai <c>int</c> sẽ làm
    /// các tham số phía sau bị lệch toàn bộ, lời gọi kết thúc ngay với mã 0xC0000005.
    /// </summary>
    [PreserveSig]
    int GetImage(Size size, int flags, out nint bitmap);
}

/// <summary>Win32 <c>SIZE</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Size
{
    internal int Width;

    internal int Height;
}

/// <summary>
/// <c>IPropertyStore</c>: đọc một giá trị trong hệ thống thuộc tính.
/// </summary>
/// <remarks>
/// Hai mục đích: đọc <c>System.AppUserModel.ID</c> được thiết lập tường minh trên <c>.lnk</c> (bản thân đối tượng ShellLink
/// triển khai giao diện này), và đọc định danh ứng dụng được thiết lập tường minh trên cửa sổ
/// (<c>SHGetPropertyStoreForWindow</c> trả về giao diện này).
///
/// <para>
/// Khai báo bắt đầu từ slot 1 của vtable theo đúng thứ tự gốc; sản phẩm này chỉ <strong>đọc</strong> giá trị,
/// nên khai báo tới GetValue (slot 3) là dừng, các phương thức SetValue/Commit phía sau không dùng đến.
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    /// <summary>Đọc một thuộc tính. Khi không có giá trị, trả về <see cref="Ole32.SoK"/> và đặt kiểu thành <c>VT_EMPTY</c>.</summary>
    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);

    /// <summary>Ghi một thuộc tính. Giá trị phải là bản sao đầy đủ (chuỗi được sao chép theo quy tắc của bộ cấp phát thuộc tính).</summary>
    [PreserveSig]
    int SetValue(in PropertyKey key, in PropVariant value);

    /// <summary>Ghi các thay đổi đang chờ xuống đĩa. Không có bước này thì nội dung của <see cref="SetValue"/> không có hiệu lực.</summary>
    [PreserveSig]
    int Commit();
}

/// <summary>Các cờ của <c>IShellItemImageFactory::GetImage</c>.</summary>
internal static class Siigbf
{
    /// <summary>Chỉ lấy biểu tượng, không lấy ảnh thu nhỏ — thứ chúng ta cần là biểu tượng ứng dụng, không phải ảnh xem trước của .exe.</summary>
    internal const int IconOnly = 0x00000004;

    /// <summary>Cho phép trả về ảnh lớn hơn kích thước yêu cầu, bên gọi tự thu nhỏ lại.</summary>
    internal const int BiggerSizeOk = 0x00000001;

    /// <summary>Cho phép phóng to khi ảnh nguồn nhỏ hơn kích thước yêu cầu.</summary>
    internal const int ScaleUp = 0x00000100;
}