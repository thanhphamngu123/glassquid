using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using Glassquid.Core.Storage;
using Glassquid.Launcher;

internal static partial class Program
{
    private static void VerifyInputDeviceProbe(LauncherWindow window, UniformGrid grid, GlassquidDataStore data, string evidence)
    {
        var buffer = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.Copy(new byte[48], 0, buffer, 48);
            Marshal.WriteInt32(buffer, 4, 48);
            Marshal.WriteIntPtr(buffer, 8, 0x123);
            Marshal.WriteInt16(buffer, 28, 1);
            Marshal.WriteInt32(buffer, 36, -17);
            Marshal.WriteInt32(buffer, 40, 29);
            var decoded = InputDeviceProbe.TryDecodeMouse(buffer, 48, out var packet);
            Check("Gói chuột thô giữ nguyên thông tin thiết bị, độ dịch chuyển và nút bấm", "diagnostics/input-device",
                decoded && packet.Device == 0x123 && packet.X == -17 && packet.Y == 29 && packet.ButtonFlags == 1,
                "device=0x123,x=-17,y=29,buttons=1", $"decoded={decoded},packet={packet}");
            var truncated = InputDeviceProbe.TryDecodeMouse(buffer, 32, out _);
            Marshal.WriteInt32(buffer, 0, 1);
            var keyboard = InputDeviceProbe.TryDecodeMouse(buffer, 48, out _);
            Check("Gói bị cắt cụt và đầu vào không phải chuột không được tính vào tần suất chuột", "diagnostics/input-device", !truncated && !keyboard,
                "Cả hai loại đầu vào đều bị bỏ qua", $"truncated={truncated},keyboard={keyboard}");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        using var probe = new InputDeviceProbe(window, evidence);
        Check("Trình quan sát chuột thô đăng ký thành công trên luồng riêng", "diagnostics/input-device", probe.Ready,
            "Đăng ký thành công trên luồng riêng", probe.Ready.ToString());
        var before = probe.BackgroundPulses;
        Thread.Sleep(350); // Deliberately block only the UI; the observation thread must keep advancing.
        var pulses = probe.BackgroundPulses - before;
        Check("Đồng hồ quan sát đầu vào nền vẫn chạy tiếp khi giao diện bị nghẽn", "diagnostics/input-device", pulses >= 2,
            "Ít nhất 2 nhịp đồng hồ nền", pulses.ToString());
        VerifyCaptureDragInput(window, grid, data, evidence);
        Check("Phản hồi kéo OLE xuyên cửa sổ được ghi nhận và thao tác thả tay cục bộ để hủy hoạt động bình thường", "diagnostics/input-device",
            probe.FeedbackCount > 0 && probe.QueryCount > 0,
            "Đã nhận phản hồi và sự kiện tiếp tục kéo, ca kiểm thử kéo hiện tại đạt", $"feedback={probe.FeedbackCount},query={probe.QueryCount}");
        probe.Dispose();
        Check("Sau khi chẩn đoán kết thúc, luồng quan sát riêng thoát và lưu báo cáo", "diagnostics/input-device",
            probe.Stopped && File.Exists(Path.Combine(evidence, "input-device-timing.json")),
            "Luồng đã thoát và báo cáo tồn tại", $"stopped={probe.Stopped}");
    }
}
