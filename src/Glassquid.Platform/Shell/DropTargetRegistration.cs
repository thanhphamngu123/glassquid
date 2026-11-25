using System.Runtime.InteropServices;
using Glassquid.Platform.Interop;

namespace Glassquid.Platform.Shell;

/// <summary>
/// Truy vấn xem một handle cửa sổ đã đăng ký mục tiêu kéo thả OLE (<c>IDropTarget</c>) hay chưa.
/// </summary>
/// <remarks>
/// Hành vi công khai của <c>RegisterDragDrop</c>: khi đăng ký thành công, để lại atom
/// <c>OleDropTargetInterface</c> trong bảng thuộc tính của handle cửa sổ. WPF tự hoàn tất đăng ký khi <see cref="System.Windows.UIElement.AllowDrop"/>
/// được đặt là true — truy vấn này giúp sản phẩm <strong>xác chứng</strong> điều đó,
/// qua đó đặt quy tắc "cùng một handle chỉ có thể có một bên đăng ký kéo thả" dựa trên bằng chứng
/// (phương thức đo thực tế của đầu dò C giai đoạn 4, xem <c>docs/evidence/giai-doan-4-ket-qua-do-kiem-thuc-te.txt</c>).
/// </remarks>
public static class DropTargetRegistration
{
    /// <summary>Tên atom mà OLE để lại trong bảng thuộc tính cửa sổ.</summary>
    private const string OleDropTargetInterfaceAtom = "OleDropTargetInterface";

    /// <summary>Cửa sổ này đã đăng ký mục tiêu kéo thả OLE hay chưa.</summary>
    public static bool IsOleDropTargetRegistered(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return User32.GetProp(window, OleDropTargetInterfaceAtom) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
