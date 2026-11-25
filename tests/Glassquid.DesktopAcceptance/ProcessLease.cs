using System.Diagnostics;
using System.IO;
using System.Text;

namespace Glassquid.DesktopAcceptance;

/// <summary>
/// Hợp đồng thuê đối với "tiến trình do chính lượt kiểm thử này khởi chạy".
/// </summary>
/// <remarks>
/// Toàn bộ điểm cốt lõi của vệ sinh kiểm thử nằm ở đây: chỉ kết thúc tiến trình <strong>do chính mình khởi chạy</strong>, và trước khi kết thúc
/// phải đối chiếu cả ba yếu tố gồm <see cref="ProcessId"/>, thời điểm khởi chạy và đường dẫn đầy đủ của ảnh thực thi.
/// Nếu ba yếu tố này không khớp thì chứng tỏ pid đã bị hệ thống thu hồi và cấp cho tiến trình khác — khi đó việc buộc dừng sẽ giết nhầm chương trình của người khác.
/// Tuyệt đối không dọn dẹp hàng loạt theo tên tiến trình: đó chính là nguyên nhân gây ra tình trạng "bài kiểm thử tắt mất chương trình người dùng đang sử dụng".
/// </remarks>
internal sealed class ProcessLease : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _startedAt;
    private readonly List<string> _output = [];
    private readonly object _outputGate = new();
    private bool _disposed;

    private ProcessLease(Process process, DateTime startedAt, string imagePath)
    {
        _process = process;
        _startedAt = startedAt;
        ImagePath = imagePath;
    }

    internal int ProcessId => _process.Id;

    internal string ImagePath { get; }

    /// <summary>Dòng lệnh đầy đủ, ghi vào bằng chứng để thuận tiện đối chiếu lại về sau xem thực tế đã chạy gì.</summary>
    internal string CommandLine { get; private init; } = string.Empty;

    internal bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    internal static ProcessLease Launch(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        bool captureOutput = false)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"Tệp thực thi cần khởi chạy không tồn tại: {executable}", executable);
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!,
        };

        // The encoding properties are only legal together with redirection: setting
        // StandardOutputEncoding without RedirectStandardOutput makes Process.Start throw
        // InvalidOperationException, which is how every case failed the first time this driver
        // ran for real. They are therefore set together, and only when output is captured.
        if (captureOutput)
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Khởi chạy {executable} không trả về đối tượng tiến trình.");

        ProcessLease lease;

        try
        {
            // Thời điểm khởi chạy và đường dẫn ảnh thực thi phải lấy khi tiến trình còn sống,
            // sau khi kết thúc có thể sẽ không đọc được MainModule nữa.
            var startedAt = process.StartTime;
            var imagePath = process.MainModule?.FileName ?? executable;
            lease = new ProcessLease(process, startedAt, imagePath)
            {
                CommandLine = string.Join(' ', new[] { executable }.Concat(arguments)),
            };
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (Exception)
            {
                // Ở đây đã nằm trong nhánh ngoại lệ, dù không kết thúc được cũng không thể che lấp ngoại lệ gốc.
            }

            process.Dispose();
            throw;
        }

        if (captureOutput)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                {
                    return;
                }

                lock (lease._outputGate)
                {
                    lease._output.Add(eventArgs.Data);
                }
            };

            process.BeginOutputReadLine();
        }

        return lease;
    }

    /// <summary>Tất cả các dòng do tiến trình xuất ra tính đến hiện tại (giữ nguyên thứ tự).</summary>
    internal IReadOnlyList<string> Output()
    {
        lock (_outputGate)
        {
            return [.. _output];
        }
    }

    /// <summary>Chờ trong luồng đầu ra một dòng văn bản bắt đầu bằng tiền tố cho trước.</summary>
    internal string? WaitForOutputLine(string prefix, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        do
        {
            lock (_outputGate)
            {
                var match = _output.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match[prefix.Length..].Trim();
                }
            }

            Thread.Sleep(200);
        }
        while (Environment.TickCount64 < deadline);

        return null;
    }

    internal bool WaitForExit(int timeoutMs)
    {
        try
        {
            return _process.WaitForExit(timeoutMs);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Chỉ khi cả ba yếu tố pid, thời điểm khởi chạy và đường dẫn ảnh thực thi đều khớp thì mới coi tiến trình này vẫn là tiến trình do chúng ta khởi chạy.</summary>
    internal bool StillOurs()
    {
        try
        {
            if (_process.HasExited)
            {
                return false;
            }

            return _process.StartTime == _startedAt
                && string.Equals(_process.MainModule?.FileName, ImagePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Kết thúc tiến trình này. Nếu đối chiếu không đạt thì không làm gì cả và trả lý do về cho bên gọi.</summary>
    internal string Stop(string reason)
    {
        if (HasExited)
        {
            return $"{reason}: Tiến trình {ProcessId} đã thoát.";
        }

        if (!StillOurs())
        {
            return $"{reason}: Tiến trình {ProcessId} không vượt qua kiểm tra ba yếu tố (thời điểm khởi chạy hoặc đường dẫn ảnh đã thay đổi), "
                + "không thể xác nhận đây vẫn là tiến trình được khởi chạy trong lần này, do đó <không kết thúc>.";
        }

        try
        {
            _process.Kill(entireProcessTree: false);
            _ = _process.WaitForExit(10000);
            return $"{reason}: Đã kết thúc tiến trình tự khởi chạy {ProcessId} ({ImagePath}).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"{reason}: Kết thúc tiến trình {ProcessId} thất bại: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!HasExited)
        {
            _ = Stop("Dọn dẹp cuối");
        }

        _process.Dispose();
    }
}