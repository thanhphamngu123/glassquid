using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glassquid.Platform.Storage;

/// <summary>Đầu ra có tên cho một lần nghiệm thu; chỉ cho phép tạo tệp mới.</summary>
public sealed record AcceptanceOutput(string Name, string Path);

/// <summary>Gốc bảo vệ chỉ dùng để so sánh ranh giới; gốc dữ liệu phải hoàn toàn mới và mọi thư mục cha đều phải đã tồn tại.</summary>
public sealed record AcceptanceRequest(string ProtectedDataRoot, string NewDataRoot, IReadOnlyList<AcceptanceOutput> Outputs);

/// <summary>Giữ danh tính thư mục nghiệm thu và đầu ra độc quyền, tránh bị phân giải sang nơi khác sau khi kiểm tra theo đường dẫn.</summary>
/// <remarks>
/// Chỉ hỗ trợ đường dẫn tuyệt đối chuẩn trên ổ NTFS cố định cục bộ. Mọi từ chối tĩnh diễn ra trước khi tạo.
/// Điều kiện tranh chấp hoặc lỗi I/O trong quá trình cấp phát có thể để lại thư mục/tệp rỗng của lần này; chúng sẽ không bị thu hồi đệ quy.
/// Giữ tay cầm thư mục theo từng đoạn, từ chối điểm phân giải lại (reparse point) khi mở; thư mục cho phép chia sẻ đọc ghi nhưng cấm chia sẻ xóa,
/// nhờ đó giữ nguyên danh tính đối tượng mà không chặn chương trình khác ghi vào thư mục. Không ngăn việc sửa siêu dữ liệu thư mục hoặc
/// thuộc tính reparse về sau; các truy cập theo đường dẫn ngoài lớp này không nhận được đảm bảo cô lập tranh chấp từ đây.
/// Tạo mới sử dụng FILE_CREATE tương đối theo tay cầm cha đang giữ
/// (tức CreateNew), thay vì kiểm tra trước rồi mở theo đường dẫn đầy đủ. Đầu ra có ShareAccess=0 (FileShare.None).
/// Lớp này không ràng buộc các truy cập đường dẫn tùy ý về sau của bên gọi vào các mục con bên trong DataRoot, cũng không phải hộp cát đa dụng để chống quản trị viên,
/// trình điều khiển nhân hoặc tiến trình độc hại cùng người dùng sửa đổi toàn bộ cây dữ liệu. Không dựa vào đây để thực hiện xóa dữ liệu thực.
/// Bên gọi không được đóng sớm luồng đầu ra; sau khi Dispose mọi bảo vệ về danh tính và chia sẻ đều kết thúc.
/// </remarks>
public sealed partial class AcceptanceWorkspace : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x100000;
    private const uint OpenReparsePoint = 0x200000;
    private const uint SynchronousNonAlert = 0x20;
    private const int NameNotFound = unchecked((int)0xC0000034);
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stream> _outputs = new(StringComparer.Ordinal);
    private bool _disposed;

    private AcceptanceWorkspace(string dataRoot) => DataRoot = dataRoot;

    public string DataRoot { get; }

    /// <summary>Xác thực toàn bộ đầu vào và thư mục cha hiện có trước, rồi mới tạo nguyên tử các nút lá mới; thất bại sẽ không ghi đè mục hiện có.</summary>
    public static AcceptanceWorkspace Create(AcceptanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Thư mục nghiệm thu chỉ hỗ trợ Windows.");
        var protectedRoot = CanonicalPath(request.ProtectedDataRoot);
        var dataRoot = CanonicalPath(request.NewDataRoot);
        if (request.Outputs is null) throw new IOException("Thiếu danh sách đầu ra nghiệm thu.");
        var outputs = request.Outputs.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<string> { dataRoot };
        foreach (var output in outputs)
        {
            if (output is null || string.IsNullOrWhiteSpace(output.Name) || !names.Add(output.Name))
                throw new IOException("Tên đầu ra nghiệm thu bị trống hoặc trùng lặp.");
            targets.Add(CanonicalPath(output.Path));
        }
        for (var i = 0; i < targets.Count; i++)
        {
            if (Overlaps(targets[i], protectedRoot)) throw new IOException("Đường dẫn nghiệm thu chồng lấn với thư mục dữ liệu người dùng thực.");
            for (var j = 0; j < i; j++)
                if (Overlaps(targets[i], targets[j])) throw new IOException("Đường dẫn dữ liệu và đầu ra nghiệm thu chồng lấn lẫn nhau.");
        }

        var workspace = new AcceptanceWorkspace(dataRoot);
        try
        {
            // Khi gốc bảo vệ không tồn tại chỉ giữ các thư mục tiền bối hiện có; không tạo, đọc tài liệu hay tự khôi phục dữ liệu thực.
            workspace.PinDirectoryChain(protectedRoot, allowMissing: true);
            foreach (var target in targets)
            {
                var parent = workspace.PinDirectoryChain(Path.GetDirectoryName(target)!, allowMissing: false)!;
                using var existing = OpenRelative(parent, Path.GetFileName(target), create: false, directory: false, probe: true);
                if (existing is not null) throw new IOException("Đích nghiệm thu đã tồn tại, từ chối tái sử dụng hoặc ghi đè: " + target);
            }

            var dataParent = workspace._directories[Path.GetDirectoryName(dataRoot)!];
            var dataHandle = OpenRelative(dataParent, Path.GetFileName(dataRoot), create: true, directory: true)!;
            workspace._directories.Add(dataRoot, dataHandle);
            VerifyDirectory(dataHandle, dataRoot);
            for (var i = 0; i < outputs.Length; i++)
            {
                var path = targets[i + 1];
                var handle = OpenRelative(workspace._directories[Path.GetDirectoryName(path)!], Path.GetFileName(path), create: true, directory: false)!;
                try { workspace._outputs.Add(outputs[i].Name, new FileStream(handle, FileAccess.Write)); }
                catch { handle.Dispose(); throw; }
            }
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public Stream GetOutput(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _outputs[name];
    }

    /// <summary>Tạo mới và cố định một gốc dữ liệu con nghiệm thu độc lập dưới tay cầm gốc dữ liệu đang giữ.</summary>
    public string CreateChildDataRoot(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.IsPathRooted(name) ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new IOException("Gốc dữ liệu con nghiệm thu phải là một tên thư mục đơn lẻ.");

        var path = CanonicalPath(Path.Combine(DataRoot, name));
        if (!Path.GetDirectoryName(path)!.Equals(DataRoot, StringComparison.OrdinalIgnoreCase) || _directories.ContainsKey(path))
            throw new IOException("Gốc dữ liệu con nghiệm thu phải là thư mục trực thuộc hoàn toàn mới trong không gian làm việc này.");

        var parent = _directories[DataRoot];
        var handle = OpenRelative(parent, name, create: true, directory: true)!;
        try
        {
            VerifyDirectory(handle, path);
            _directories.Add(path, handle);
            return path;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        foreach (var stream in _outputs.Values)
        {
            try { stream.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= ex; }
        }
        foreach (var handle in _directories.Values.Reverse()) handle.Dispose();
        if (failure is not null) throw new IOException("Đóng đầu ra nghiệm thu thất bại.", failure);
    }

    private SafeFileHandle? PinDirectoryChain(string path, bool allowMissing)
    {
        if (_directories.TryGetValue(path, out var cached)) return cached;
        var root = Path.GetPathRoot(path)!;
        if (!_directories.TryGetValue(root, out var parent))
        {
            parent = OpenRelative(null, @"\??\" + root, create: false, directory: true)!;
            _directories.Add(root, parent);
            VerifyDirectory(parent, root);
        }
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (_directories.TryGetValue(current, out var pinned)) { parent = pinned; continue; }
            var next = OpenRelative(parent, segment, create: false, directory: true, probe: allowMissing);
            if (next is null) return null;
            _directories.Add(current, next);
            VerifyDirectory(next, current);
            parent = next;
        }
        return parent;
    }

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/'))
            throw new IOException("Đường dẫn nghiệm thu phải là đường dẫn tuyệt đối chuẩn trên ổ đĩa cố định cục bộ.");
        var normalized = path.TrimEnd('\\');
        if (normalized.Length == 2 || !string.Equals(Path.GetFullPath(normalized), normalized, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Từ chối gốc ổ đĩa hoặc đường dẫn nghiệm thu không chuẩn.");
        foreach (var segment in normalized[3..].Split('\\'))
        {
            var stem = segment.Split('.')[0];
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
                throw new IOException("Đường dẫn nghiệm thu chứa tên không an toàn.");
        }
        var drive = new DriveInfo(normalized[..3]);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Thư mục nghiệm thu chỉ hỗ trợ ổ NTFS cố định cục bộ.");
        return normalized;
    }

    private static bool Overlaps(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase);

    private static unsafe SafeFileHandle? OpenRelative(SafeFileHandle? parent, string name, bool create, bool directory, bool probe = false)
    {
        fixed (char* buffer = name)
        {
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = (IntPtr)buffer };
            var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes), RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero, ObjectName = (IntPtr)(&unicode), Attributes = 0x40 };
            // FILE_LIST_DIRECTORY tham gia kiểm tra truy cập chia sẻ; tay cầm chỉ có FILE_READ_ATTRIBUTES
            // không thể ngăn chặn đáng tin cậy việc đổi tên thư mục (đã kiểm chứng bởi ca kiểm thử hồi quy Directory.Move thực tế).
            var access = Synchronize | (create && !directory ? 0x40000000u : ReadAttributes | (directory && parent is not null ? 1u : 0u));
            // Thư mục cho phép chia sẻ đọc ghi, chỉ từ chối chia sẻ xóa; đầu ra độc quyền hoàn toàn; thăm dò nút lá hiện có chỉ đọc thuộc tính.
            // Gốc ổ đĩa không truy cập lại qua tên; tay cầm đang giữ là điểm neo cho các truy cập tương đối tiếp theo.
            // Không khóa chia sẻ ghi/xóa của toàn bộ gốc ổ đĩa để tránh xung đột với tay cầm ổ đĩa hệ thống.
            var share = parent is null ? 7u : directory ? 3u : probe ? 7u : 0u;
            var options = OpenReparsePoint | SynchronousNonAlert | (directory ? 1u : create ? 0x40u : 0u);
            var status = Native.NtCreateFile(out var rawHandle, access, ref attributes, out _, IntPtr.Zero, 0, share, create ? 2u : 1u, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                if (probe && status == NameNotFound) return null;
                var error = Native.RtlNtStatusToDosError(status);
                throw new IOException("Không thể lấy an toàn đối tượng đường dẫn nghiệm thu: " + name, new Win32Exception(unchecked((int)error)));
            }
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
    }

    private static unsafe void VerifyDirectory(SafeFileHandle handle, string expectedPath)
    {
        if (!Native.GetFileInformationByHandleEx(handle, 9, out var info, (uint)sizeof(AttributeTagInfo)))
            throw new IOException("Không thể xác minh thuộc tính thư mục nghiệm thu.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Từ chối điểm phân giải lại (reparse point) hoặc tiền bối nghiệm thu không phải thư mục.");
        var buffer = stackalloc char[1024];
        var length = Native.GetFinalPathNameByHandle(handle, buffer, 1024, 0);
        if (length == 0 || length >= 1024) throw new IOException("Không thể xác minh đường dẫn cuối cùng của thư mục nghiệm thu.");
        var actual = new string(buffer, 0, (int)length);
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !actual[4..].TrimEnd('\\').Equals(expectedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Đường dẫn cuối cùng của thư mục nghiệm thu không khớp với yêu cầu, từ chối bí danh hoặc chuyển hướng.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public uint Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint Tag; }

    private static partial class Native
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
        [LibraryImport("ntdll.dll")]
        internal static partial uint RtlNtStatusToDosError(int status);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTagInfo info, uint size);
        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        internal static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint length, uint flags);
    }
}
