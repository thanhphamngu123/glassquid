using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using Glassquid.Core.Storage;
using Glassquid.Platform.Storage;

namespace Glassquid.AppServices;

public enum UserDataDeletionStatus
{
    Cancelled,
    NotFound,
    Busy,
    UnsafePath,
    Failed,
    Residual,
    Deleted,
}

public sealed record UserDataDeletionResult(
    UserDataDeletionStatus Status,
    int DeletedEntries = 0,
    string? ResidualDirectory = null,
    string? Error = null);

/// <summary>Xóa dữ liệu người dùng hiện tại của Glassquid thông qua hợp đồng thuê độc quyền liên tiến trình và thư mục cách ly ngang cấp.</summary>
public sealed class UserDataDeletionService
{
    private readonly string _dataRoot;
    private readonly string _expectedDataRoot;
    private readonly IReadOnlyList<string> _protectedRoots;
    private readonly IUserDataDeletionFileSystem _fileSystem;
    private readonly string? _portableInstallationDirectory;

    internal UserDataDeletionService(
        string dataRoot,
        string expectedDataRoot,
        IReadOnlyList<string> protectedRoots,
        IUserDataDeletionFileSystem? fileSystem = null,
        string? portableInstallationDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDataRoot);
        ArgumentNullException.ThrowIfNull(protectedRoots);

        _dataRoot = Path.GetFullPath(dataRoot);
        _expectedDataRoot = Path.GetFullPath(expectedDataRoot);
        _protectedRoots = protectedRoots.Select(Path.GetFullPath).ToArray();
        _fileSystem = fileSystem ?? new UserDataDeletionFileSystem();
        if (!string.IsNullOrWhiteSpace(portableInstallationDirectory)
            && PathsEqual(_dataRoot, Path.Combine(portableInstallationDirectory, "Data"))
            && PathsEqual(_dataRoot, _expectedDataRoot))
        {
            _portableInstallationDirectory = Path.GetFullPath(portableInstallationDirectory);
        }
    }

    /// <summary>Tạo dịch vụ xóa phiên bản chính thức gắn cố định với thư mục dữ liệu của phiên bản đang chạy hiện tại.</summary>
    public static UserDataDeletionService ForCurrentUser(string? installationDirectory = null)
    {
        var paths = AppPaths.ForCurrentUser();
        var protectedRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            protectedRoots.Add(installationDirectory);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                protectedRoots.Add(processDirectory);
            }
        }

        return CreateForPaths(paths, AppContext.BaseDirectory, protectedRoots);
    }

    internal static UserDataDeletionService CreateForPaths(
        AppPaths paths, string applicationDirectory, IReadOnlyList<string> protectedRoots)
        => new(paths.RootDirectory, paths.RootDirectory, [.. protectedRoots, applicationDirectory],
            portableInstallationDirectory: applicationDirectory);

    /// <summary>Không đọc hoặc sửa dữ liệu trước khi xác nhận; sau khi xác nhận sẽ đổi tên để cách ly và xóa từng mục.</summary>
    public UserDataDeletionResult DeleteAll(bool confirmed)
    {
        if (!confirmed)
        {
            return new UserDataDeletionResult(UserDataDeletionStatus.Cancelled);
        }

        if (!IsConfiguredPathSafe(out var pathError))
        {
            return new UserDataDeletionResult(UserDataDeletionStatus.UnsafePath, Error: pathError);
        }

        if (!UserDataLease.TryAcquireExclusive(_dataRoot, out var lease) || lease is null)
        {
            return new UserDataDeletionResult(
                UserDataDeletionStatus.Busy,
                Error: "Dữ liệu Glassquid đang được phiên bản khác sử dụng hoặc không thể khóa riêng thư mục này. Hãy thoát các phiên bản Glassquid khác rồi thử lại.");
        }

        using (lease)
        {
            var moved = false;
            string? quarantine = null;
            var deletedEntries = 0;
            try
            {
                if (!_fileSystem.Exists(_dataRoot))
                {
                    return new UserDataDeletionResult(UserDataDeletionStatus.NotFound);
                }

                ValidateDirectoryTree(_dataRoot);
                ValidatePhysicalIdentity(_dataRoot, _expectedDataRoot);
                ValidateProtectedRoots(_dataRoot);

                // Giữ tay cầm thư mục nguồn xuyên suốt quá trình đổi tên; FILE_SHARE_DELETE cho phép thao tác này đổi tên, còn việc kiểm tra lại danh tính giúp phát hiện đường dẫn bị thay thế.
                using var sourceIdentity = OpenDirectoryIdentity(_dataRoot);
                var sourceId = ReadDirectoryIdentity(sourceIdentity);
                var sourceFinalPath = GetFinalPath(sourceIdentity);
                var expectedFinalPath = ResolveFinalPath(_expectedDataRoot);
                if (!PathsEqual(sourceFinalPath, expectedFinalPath))
                {
                    throw new UnsafeDataPathException("Đường dẫn thực tế của thư mục dữ liệu không khớp với thư mục dữ liệu của người dùng hiện tại.");
                }

                quarantine = CreateQuarantinePath();
                _fileSystem.MoveDirectory(_dataRoot, quarantine);
                moved = true;

                using var quarantineIdentity = OpenDirectoryIdentity(quarantine);
                if (ReadDirectoryIdentity(quarantineIdentity) != sourceId
                    || !PathsEqual(GetFinalPath(quarantineIdentity), quarantine))
                {
                    throw new UnsafeDataPathException("Thư mục sau khi đổi tên không khớp với thư mục dữ liệu đã xác minh.");
                }

                ValidateDirectoryTree(quarantine);
                ValidateProtectedRoots(quarantine, quarantine);
                DeleteDirectoryContents(quarantine, sourceId, ref deletedEntries);
                return new UserDataDeletionResult(UserDataDeletionStatus.Deleted, deletedEntries);
            }
            catch (UnsafeDataPathException ex)
            {
                return moved
                    ? new UserDataDeletionResult(UserDataDeletionStatus.Residual, deletedEntries, quarantine, ex.Message)
                    : new UserDataDeletionResult(UserDataDeletionStatus.UnsafePath, Error: ex.Message);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                return moved
                    ? new UserDataDeletionResult(UserDataDeletionStatus.Residual, deletedEntries, quarantine, ex.Message)
                    : new UserDataDeletionResult(UserDataDeletionStatus.Failed, Error: ex.Message);
            }
        }
    }

    private bool IsConfiguredPathSafe(out string? error)
    {
        error = null;
        if (!PathsEqual(_dataRoot, _expectedDataRoot))
        {
            error = "Đã từ chối xóa: thư mục đích không phải thư mục dữ liệu được cấu hình cho người dùng hiện tại.";
            return false;
        }

        var volumeRoot = Path.GetPathRoot(_dataRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || PathsEqual(_dataRoot, volumeRoot)
            || IsSameOrAncestor(_dataRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            error = "Đã từ chối xóa: thư mục đích không thể là thư mục gốc ổ đĩa hoặc thư mục hồ sơ người dùng.";
            return false;
        }

        foreach (var protectedRoot in _protectedRoots)
        {
            if (OverlapsProtectedRoot(_dataRoot, protectedRoot))
            {
                error = "Đã từ chối xóa: thư mục đích trùng với thư mục cài đặt ứng dụng hoặc một đường dẫn được bảo vệ.";
                return false;
            }
        }

        return true;
    }

    private void ValidateProtectedRoots(string targetRoot, string? quarantinePath = null)
    {
        foreach (var protectedRoot in _protectedRoots)
        {
            if (OverlapsProtectedRoot(targetRoot, protectedRoot, quarantinePath))
            {
                throw new UnsafeDataPathException("Thư mục dữ liệu trùng với thư mục cài đặt ứng dụng hoặc một đường dẫn được bảo vệ.");
            }

            if (!_fileSystem.Exists(protectedRoot))
            {
                continue;
            }

            var physical = ResolveFinalPath(protectedRoot);
            if (OverlapsProtectedRoot(ResolveFinalPath(targetRoot), physical, quarantinePath))
            {
                throw new UnsafeDataPathException("Vị trí thực tế của thư mục dữ liệu trùng với một đường dẫn được bảo vệ.");
            }
        }
    }

    private bool OverlapsProtectedRoot(string targetRoot, string protectedRoot, string? quarantinePath = null)
    {
        if (!PathsOverlap(targetRoot, protectedRoot)) return false;

        // The capture edition owns precisely <application>/Data. Its parent stays protected;
        // only that child and this operation's allocated sibling quarantine may be removed.
        // Physical identity, reparse-point and hard-link checks still apply before deletion.
        return !(_portableInstallationDirectory is not null
            && PathsEqual(protectedRoot, _portableInstallationDirectory)
            && PathsEqual(Path.GetDirectoryName(targetRoot) ?? targetRoot, _portableInstallationDirectory)
            && (PathsEqual(targetRoot, _dataRoot)
                || (quarantinePath is not null && PathsEqual(targetRoot, quarantinePath))));
    }

    private static void ValidatePhysicalIdentity(string actualPath, string expectedPath)
    {
        RejectReparseAncestors(actualPath);
        RejectReparseAncestors(expectedPath);
        if (!PathsEqual(ResolveFinalPath(actualPath), ResolveFinalPath(expectedPath)))
        {
            throw new UnsafeDataPathException("Đường dẫn thực tế cuối cùng của thư mục dữ liệu không khớp với đường dẫn đã cấu hình.");
        }
    }

    private void ValidateDirectoryTree(string root)
    {
        var pending = new Stack<(string Path, bool IsRoot)>();
        pending.Push((root, true));
        while (pending.Count > 0)
        {
            var (path, isRoot) = pending.Pop();
            var attributes = _fileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"Phát hiện liên kết thư mục hoặc liên kết tượng trưng; đã từ chối xóa: {path}");
            }

            if (isRoot && (attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("Đường dẫn dữ liệu không phải là thư mục.");
            }

            if (isRoot) RejectReparseAncestors(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var directory = OpenDirectoryForDeletion(path);
                foreach (var entry in directory.Entries)
                {
                    pending.Push((entry, false));
                }
            }
        }
    }

    private void DeleteDirectoryContents(string directory, DirectoryIdentity expectedRootIdentity, ref int deletedEntries)
    {
        var pending = new Stack<DirectoryTraversal>();
        pending.Push(OpenDirectoryForDeletion(directory, expectedRootIdentity));
        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Peek();
                if (current.NextEntry == current.Entries.Length)
                {
                    _fileSystem.DeleteDirectoryByHandle(current.Handle);
                    pending.Pop().Dispose();
                    deletedEntries++;
                    continue;
                }

                var path = current.Entries[current.NextEntry++];
                var attributes = _fileSystem.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnsafeDataPathException($"Phát hiện liên kết thư mục hoặc liên kết tượng trưng mới trong thư mục cách ly; đã dừng thao tác xóa: {path}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(OpenDirectoryForDeletion(path));
                    continue;
                }

                using var file = OpenFileForDeletion(path);
                _fileSystem.DeleteFileByHandle(file, path);
                deletedEntries++;
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                pending.Pop().Dispose();
            }
        }
    }

    private DirectoryTraversal OpenDirectoryForDeletion(string path, DirectoryIdentity? expectedIdentity = null)
    {
        const uint Delete = 0x00010000;
        const uint FileReadAttributes = 0x80;
        const uint ShareReadWrite = 0x1 | 0x2;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;

        var handle = CreateFileW(
            path,
            Delete | FileReadAttributes,
            ShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Không thể khóa và xác minh thư mục cần xóa: {path}");
        }

        try
        {
            var info = ReadFileInformation(handle);
            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"Thư mục cần xóa đã bị thay thế bằng liên kết thư mục hoặc liên kết tượng trưng: {path}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException($"Đường dẫn cần xóa không còn là thư mục: {path}");
            }

            if (!PathsEqual(GetFinalPath(handle), path))
            {
                throw new UnsafeDataPathException($"Đường dẫn thực tế của thư mục cần xóa không khớp với đường dẫn cách ly: {path}");
            }

            var identity = ToDirectoryIdentity(info);
            if (expectedIdentity is DirectoryIdentity expected && identity != expected)
            {
                throw new UnsafeDataPathException($"Thư mục cần xóa đã bị thay thế; danh tính thư mục không khớp với thư mục đã xác minh khi đổi tên: {path}");
            }

            // Không chia sẻ FILE_SHARE_DELETE: trong thời gian giữ tay cầm thư mục, đường dẫn không thể bị đổi tên hoặc thay thế.
            // Chính tay cầm này sau đó được dùng để đánh dấu xóa, tránh xung đột cạnh tranh giữa lúc giải phóng tay cầm và xóa theo đường dẫn.
            var entries = _fileSystem.EnumerateFileSystemEntries(path).ToArray();
            return new DirectoryTraversal(handle, entries, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenFileForDeletion(string path)
    {
        const uint Delete = 0x00010000;
        const uint FileReadAttributes = 0x80;
        const uint FileWriteAttributes = 0x100;
        const uint ShareNone = 0;
        const uint OpenExisting = 3;
        const uint OpenReparsePoint = 0x00200000;

        var handle = CreateFileW(
            path,
            Delete | FileReadAttributes | FileWriteAttributes,
            ShareNone,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Không thể khóa và xác minh tệp cần xóa: {path}");
        }

        try
        {
            var info = ReadFileInformation(handle);
            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"Phát hiện liên kết thư mục hoặc liên kết tượng trưng mới trong thư mục cách ly; đã dừng thao tác xóa: {path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new UnsafeDataPathException($"Đường dẫn tệp cần xóa đã bị thay thế bằng một thư mục: {path}");
            }

            if (info.NumberOfLinks != 1)
            {
                throw new UnsafeDataPathException($"Phát hiện tệp có liên kết cứng; đã dừng thao tác xóa để tránh ảnh hưởng đến cùng tệp ở ngoài thư mục dữ liệu: {path}");
            }

            if (!PathsEqual(GetFinalPath(handle), path))
            {
                throw new UnsafeDataPathException($"Đường dẫn thực tế của tệp cần xóa không khớp với đường dẫn cách ly: {path}");
            }

            // Tay cầm độc quyền: trong thời gian nắm giữ không thể đổi tên, thay thế, mở hoặc tạo thêm liên kết mới trỏ tới tệp này.
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private string CreateQuarantinePath()
    {
        var parent = Path.GetDirectoryName(_dataRoot)
            ?? throw new UnsafeDataPathException("Không thể xác định vị trí cách ly ngang cấp với thư mục dữ liệu.");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(parent, "Glassquid.deleted-" + Guid.NewGuid().ToString("N"));
            if (!_fileSystem.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Không thể tạo thư mục cách ly riêng cho lần xóa dữ liệu này.");
    }

    private static string ResolveFinalPath(string path)
    {
        using var handle = OpenDirectoryIdentity(path);
        return GetFinalPath(handle);
    }

    private static void RejectReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnsafeDataPathException($"Chuỗi đường dẫn chứa liên kết thư mục hoặc liên kết tượng trưng: {current}");
                }
            }
            catch (FileNotFoundException) when (!PathsEqual(current, path))
            {
                // Path chains are checked up to their existing root; a missing expected path is handled by Exists.
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    private static SafeFileHandle OpenDirectoryIdentity(string path)
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x1 | 0x2 | 0x4;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        var handle = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Không thể mở và xác minh thư mục: {path}");
        }

        return handle;
    }

    private static DirectoryIdentity ReadDirectoryIdentity(SafeFileHandle handle)
        => ToDirectoryIdentity(ReadFileInformation(handle));

    private static DirectoryIdentity ToDirectoryIdentity(ByHandleFileInformation info)
        => new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);

    private static ByHandleFileInformation ReadFileInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể đọc danh tính tệp hoặc thư mục.");
        }

        return info;
    }

    internal static void MarkDirectoryForDeletion(SafeFileHandle handle)
    {
        var information = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref information, (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể xóa thư mục qua tay cầm thư mục đã xác minh.");
        }
    }

    internal static void DeleteFileByHandle(SafeFileHandle handle, string path)
    {
        var info = ReadFileInformation(handle);
        if (info.NumberOfLinks != 1)
        {
            throw new UnsafeDataPathException($"Tệp cần xóa phát sinh liên kết cứng sau khi bị khóa; đã dừng thao tác xóa: {path}");
        }

        if (((FileAttributes)info.FileAttributes & FileAttributes.ReadOnly) != 0)
        {
            var attributes = info.FileAttributes & ~(uint)FileAttributes.ReadOnly;
            if (attributes == 0) attributes = (uint)FileAttributes.Normal;
            var basicInformation = new FileBasicInformation
            {
                CreationTime = -1,
                LastAccessTime = -1,
                LastWriteTime = -1,
                ChangeTime = -1,
                FileAttributes = attributes,
            };
            if (!SetFileInformationByHandle(
                handle,
                0,
                ref basicInformation,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không thể bỏ thuộc tính chỉ đọc của tệp cần xóa: {path}");
            }
        }

        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
            handle,
            4,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không thể xóa tệp qua tay cầm tệp đã xác minh: {path}");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể xác minh đường dẫn thực tế cuối cùng của thư mục.");
        }

        var path = new string(buffer, 0, checked((int)length));
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            path = "\\\\" + path[8..];
        }
        else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException or Win32Exception;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathsOverlap(string left, string right) => IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);

    private static bool IsSameOrAncestor(string possibleAncestor, string child)
    {
        if (string.IsNullOrWhiteSpace(child)) return false;
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(possibleAncestor));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(ancestor, candidate, comparison)) return true;
        return candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }

    private readonly record struct DirectoryIdentity(uint VolumeSerial, uint FileIndexHigh, uint FileIndexLow);

    private sealed class DirectoryTraversal(SafeFileHandle handle, string[] entries, DirectoryIdentity identity) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public string[] Entries { get; } = entries;
        public DirectoryIdentity Identity { get; } = identity;
        public int NextEntry { get; set; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class UnsafeDataPathException(string message) : IOException(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}

internal interface IUserDataDeletionFileSystem
{
    bool Exists(string path);
    FileAttributes GetAttributes(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void MoveDirectory(string source, string destination);
    void DeleteFileByHandle(SafeFileHandle handle, string path);
    void DeleteDirectoryByHandle(SafeFileHandle handle);
}

internal sealed class UserDataDeletionFileSystem : IUserDataDeletionFileSystem
{
    public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteFileByHandle(SafeFileHandle handle, string path)
        => UserDataDeletionService.DeleteFileByHandle(handle, path);

    public void DeleteDirectoryByHandle(SafeFileHandle handle)
        => UserDataDeletionService.MarkDirectoryForDeletion(handle);
}
