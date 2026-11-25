using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Glassquid.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace Glassquid.AppServices.Tests;

/// <summary>Ranh giới hệ thống tệp thực tế: mọi sự từ chối đều không được ghi đè dữ liệu hiện có.</summary>
public sealed class AcceptanceWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Glassquid-AcceptanceTests-" + Guid.NewGuid().ToString("N"));

    public AcceptanceWorkspaceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CreateNewDataAndNamedOutputsAndHoldExclusiveOutputsUntilDisposed()
    {
        var request = Request();
        using (var workspace = AcceptanceWorkspace.Create(request))
        {
            Assert.Equal(request.NewDataRoot, workspace.DataRoot);
            Assert.True(Directory.Exists(workspace.DataRoot));
            workspace.GetOutput("report").Write(Encoding.UTF8.GetBytes("Báo cáo nghiệm thu"));
            Assert.Throws<IOException>(() => File.WriteAllText(request.Outputs[0].Path, "Ghi đè"));
            Assert.Throws<IOException>(() => File.Delete(request.Outputs[0].Path));
        }
        Assert.Equal("Báo cáo nghiệm thu", File.ReadAllText(request.Outputs[0].Path));
        Assert.False(Directory.Exists(request.ProtectedDataRoot));
    }

    [Fact]
    public void CreateChildDataRootUnderHeldWorkspaceHandleAndPreventRenameUntilDisposed()
    {
        var request = Request();
        using var workspace = AcceptanceWorkspace.Create(request);

        var childRoot = workspace.CreateChildDataRoot("cross-directory-restore");

        Assert.Equal(Path.Combine(workspace.DataRoot, "cross-directory-restore"), childRoot);
        Assert.True(Directory.Exists(childRoot));
        Assert.Throws<IOException>(() => Directory.Move(childRoot, childRoot + "-moved"));
        Assert.Throws<IOException>(() => { workspace.CreateChildDataRoot("cross-directory-restore"); });
        Assert.Throws<IOException>(() => { workspace.CreateChildDataRoot("..\\outside"); });
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(workspace.DataRoot)!, "outside")));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("parent")]
    [InlineData("case")]
    public void RejectOverlappingDataRootAndProtectedDirectoryWithoutSideEffects(string relationship)
    {
        var request = Request();
        var candidate = relationship switch
        {
            "same" => request.ProtectedDataRoot,
            "child" => Path.Combine(request.ProtectedDataRoot, "child"),
            "parent" => _root,
            _ => request.ProtectedDataRoot.ToUpperInvariant(),
        };
        RejectWithoutChanges(request with { NewDataRoot = candidate });
    }

    [Fact]
    public void AllowAdjacentSamePrefixWithoutCreatingProtectedDirectory()
    {
        var request = Request();
        using var workspace = AcceptanceWorkspace.Create(request with { NewDataRoot = request.ProtectedDataRoot + "-test" });
        Assert.True(Directory.Exists(workspace.DataRoot));
        Assert.False(Directory.Exists(request.ProtectedDataRoot));
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("data")]
    [InlineData("duplicate")]
    [InlineData("ancestor")]
    public void ValidateAllOutputsBeforeCreatingDataRoot(string target)
    {
        var request = Request();
        var outputs = target switch
        {
            "protected" => new[] { new AcceptanceOutput("report", Path.Combine(request.ProtectedDataRoot, "report.txt")) },
            "data" => [new AcceptanceOutput("report", Path.Combine(request.NewDataRoot, "report.txt"))],
            "duplicate" => [new AcceptanceOutput("report", Path.Combine(_root, "a")), new AcceptanceOutput("image", Path.Combine(_root, "a"))],
            _ => [new AcceptanceOutput("report", Path.Combine(_root, "a")), new AcceptanceOutput("image", Path.Combine(_root, "a", "b"))],
        };
        RejectWithoutChanges(request with { Outputs = outputs });
    }

    [Theory]
    [InlineData("data-directory")]
    [InlineData("data-file")]
    [InlineData("report-file")]
    [InlineData("report-directory")]
    public void NeverReuseOrOverwriteExistingDataRootOrOutputs(string kind)
    {
        var request = Request();
        var target = kind.StartsWith("data", StringComparison.Ordinal) ? request.NewDataRoot : request.Outputs[0].Path;
        if (kind.EndsWith("directory", StringComparison.Ordinal)) Directory.CreateDirectory(target);
        else File.WriteAllText(target, "Giữ nguyên bản gốc");
        RejectWithoutChanges(request);
    }

    [Fact]
    public void RejectExistingHardLinkOutputAndKeepExternalSentinelUnchanged()
    {
        var request = Request();
        var sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "Lính canh bên ngoài");
        Assert.True(CreateHardLink(request.Outputs[0].Path, sentinel, IntPtr.Zero), Marshal.GetLastPInvokeError().ToString());
        RejectWithoutChanges(request);
        Assert.Equal("Lính canh bên ngoài", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("dotdot")]
    [InlineData("dot")]
    [InlineData("trailing-dot")]
    [InlineData("trailing-space")]
    [InlineData("ads")]
    [InlineData("device")]
    [InlineData("reserved")]
    [InlineData("missing-parent")]
    public void RejectNonCanonicalOrUnsafePathBeforeAnyWrite(string kind)
    {
        var request = Request();
        var path = kind switch
        {
            "relative" => "relative-report.txt",
            "dotdot" => Path.Combine(_root, "a", "..", "report.txt"),
            "dot" => Path.Combine(_root, ".", "report.txt"),
            "trailing-dot" => Path.Combine(_root, "report."),
            "trailing-space" => Path.Combine(_root, "report "),
            "ads" => Path.Combine(_root, "report:stream"),
            "device" => @"\\?\" + Path.Combine(_root, "report.txt"),
            "reserved" => Path.Combine(_root, "CON.txt"),
            _ => Path.Combine(_root, "missing", "report.txt"),
        };
        RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", path)] });
    }

    [Fact]
    public void RejectJunctionAncestorWithoutAddingFilesToExternalDirectory()
    {
        var request = Request();
        var target = Path.Combine(_root, "external");
        var junction = Path.Combine(_root, "junction");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" + target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null");
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000));
        Assert.True(process.ExitCode == 0, error);
        try
        {
            RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", Path.Combine(junction, "report.txt"))] });
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void PreventReplacingDataRootAndOutputAncestorsWhileRunning()
    {
        var parent = Path.Combine(_root, "output-parent");
        Directory.CreateDirectory(parent);
        var request = Request() with { Outputs = [new AcceptanceOutput("report", Path.Combine(parent, "report.txt"))] };
        using (var workspace = AcceptanceWorkspace.Create(request))
        {
            Assert.Throws<IOException>(() => Directory.Move(workspace.DataRoot, workspace.DataRoot + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
            File.WriteAllText(Path.Combine(workspace.DataRoot, "settings.json"), "{}");
            workspace.GetOutput("report").WriteByte(42);
        }
        Directory.Move(parent, parent + "-moved");
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(parent + "-moved", "report.txt")));
    }

    [Fact]
    public void AllowWritingFilesAndCreatingSubdirectoriesInProtectedRootWhileHoldingWorkspace()
    {
        var request = Request();
        Directory.CreateDirectory(request.ProtectedDataRoot);
        var settings = Path.Combine(request.ProtectedDataRoot, "settings.json");
        File.WriteAllText(settings, "Cài đặt cũ");
        using var workspace = AcceptanceWorkspace.Create(request);
        File.WriteAllText(settings, "Cài đặt mới");
        Directory.CreateDirectory(Path.Combine(request.ProtectedDataRoot, "icons"));
        Directory.CreateDirectory(Path.Combine(request.ProtectedDataRoot, "logs"));
        File.WriteAllText(Path.Combine(request.ProtectedDataRoot, "logs", "active.log"), "Tiếp tục chạy");
        Directory.CreateDirectory(Path.Combine(workspace.DataRoot, "icons"));
        Directory.CreateDirectory(Path.Combine(workspace.DataRoot, "logs"));
        Assert.Equal("Cài đặt mới", File.ReadAllText(settings));
        Assert.Equal("Tiếp tục chạy", File.ReadAllText(Path.Combine(request.ProtectedDataRoot, "logs", "active.log")));
        Assert.True(Directory.Exists(Path.Combine(workspace.DataRoot, "icons")));
        Assert.True(Directory.Exists(Path.Combine(workspace.DataRoot, "logs")));
        Assert.Throws<IOException>(() => Directory.Move(request.ProtectedDataRoot, request.ProtectedDataRoot + "-moved"));
    }

    [Fact]
    public void AllowOtherDirectoryWriteHandlesWhileHoldingProtectedRootButRejectRename()
    {
        var request = Request();
        Directory.CreateDirectory(request.ProtectedDataRoot);
        using var workspace = AcceptanceWorkspace.Create(request);
        using var writer = OpenDirectoryForWrite(request.ProtectedDataRoot, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert.False(writer.IsInvalid, "Handle ghi thư mục gốc được bảo vệ bị từ chối, Win32=" + Marshal.GetLastPInvokeError());
        Assert.Throws<IOException>(() => Directory.Move(request.ProtectedDataRoot, request.ProtectedDataRoot + "-moved"));
    }

    [Fact]
    public void RejectDuplicateOutputNamesAndPreventGettingOutputsAfterDispose()
    {
        var request = Request();
        RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", Path.Combine(_root, "a.txt")), new AcceptanceOutput("report", Path.Combine(_root, "b.txt"))] });
        var workspace = AcceptanceWorkspace.Create(request);
        Assert.Throws<KeyNotFoundException>(() => workspace.GetOutput("absent"));
        workspace.Dispose();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.GetOutput("report"));
    }

    private AcceptanceRequest Request() => new(Path.Combine(_root, "protected"), Path.Combine(_root, "data"), [new AcceptanceOutput("report", Path.Combine(_root, "report.txt"))]);

    private void RejectWithoutChanges(AcceptanceRequest request)
    {
        var before = Snapshot();
        Assert.Throws<IOException>(() => AcceptanceWorkspace.Create(request));
        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
        .Select(path => Directory.Exists(path) ? path + "|directory" : path + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() => Directory.Delete(_root, true);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectoryForWrite(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);
}
