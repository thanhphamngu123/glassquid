using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Glassquid.Dock;

internal sealed record WifiNetworkInfo(
    string Name,
    string ProfileName,
    byte[] Ssid,
    uint SignalQuality,
    bool IsSecure,
    bool IsConnected,
    bool IsConnectable,
    uint AuthenticationAlgorithm,
    uint CipherAlgorithm);

/// <summary>Small Native Wi-Fi wrapper for live SSIDs, connection state and user-selected network actions.</summary>
internal static class NativeWifi
{
    private const uint WlanClientVersion = 2;
    private const uint AvailableNetworkConnected = 1;
    private const uint WlanConnectionModeProfile = 0;
    private const uint WlanBssTypeInfrastructure = 1;
    private const uint ErrorSuccess = 0;

    public static IReadOnlyList<WifiNetworkInfo> ScanAndReadNetworks()
    {
        using var client = new WlanClient();
        var interfaceInfo = client.GetFirstInterface();
        if (interfaceInfo is null) return [];

        var guid = interfaceInfo.Value.InterfaceGuid;
        ThrowOnError(WlanScan(client.Handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        // WlanScan is asynchronous. Keep the UI responsive while the driver updates its cache.
        Thread.Sleep(2200);
        return ReadNetworks(client.Handle, guid);
    }

    public static IReadOnlyList<WifiNetworkInfo> ReadNetworks()
    {
        using var client = new WlanClient();
        var interfaceInfo = client.GetFirstInterface();
        return interfaceInfo is null ? [] : ReadNetworks(client.Handle, interfaceInfo.Value.InterfaceGuid);
    }

    public static bool Disconnect()
    {
        using var client = new WlanClient();
        var interfaceInfo = client.GetFirstInterface();
        if (interfaceInfo is null) return false;
        var guid = interfaceInfo.Value.InterfaceGuid;
        var result = WlanDisconnect(client.Handle, ref guid, IntPtr.Zero);
        return result == ErrorSuccess;
    }

    public static bool Connect(WifiNetworkInfo network, string? password, out string status)
    {
        using var client = new WlanClient();
        var interfaceInfo = client.GetFirstInterface();
        if (interfaceInfo is null)
        {
            status = "Không tìm thấy bộ điều hợp Wi-Fi.";
            return false;
        }

        var profileName = network.ProfileName;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            if (RequiresPassword(network) && string.IsNullOrEmpty(password))
            {
                status = "Mạng này cần mật khẩu.";
                return false;
            }

            if (IsEnterpriseAuth(network.AuthenticationAlgorithm) || !IsSupportedPersonalAuth(network))
            {
                status = "Kiểu bảo mật này cần thiết lập riêng. Hãy dùng Cài đặt Wi-Fi của Windows.";
                return false;
            }

            profileName = network.Name;
            var xml = CreateProfile(network, password);
            var guid = interfaceInfo.Value.InterfaceGuid;
            var setResult = WlanSetProfile(client.Handle, ref guid, 0, xml, null, true, IntPtr.Zero, out var reasonCode);
            if (setResult != ErrorSuccess)
            {
                status = $"Không tạo được hồ sơ mạng (mã {setResult}, lý do {reasonCode}).";
                return false;
            }
        }

        var parameters = new WlanConnectionParameters
        {
            ConnectionMode = WlanConnectionModeProfile,
            ProfileName = profileName,
            BssType = WlanBssTypeInfrastructure,
        };
        var interfaceGuid = interfaceInfo.Value.InterfaceGuid;
        var connectResult = WlanConnect(client.Handle, ref interfaceGuid, ref parameters, IntPtr.Zero);
        if (connectResult != ErrorSuccess)
        {
            status = $"Windows không thể kết nối với {network.Name} (mã {connectResult}).";
            return false;
        }

        status = $"Đang kết nối với {network.Name}…";
        return true;
    }

    public static bool RequiresPassword(WifiNetworkInfo network) =>
        network.IsSecure && network.AuthenticationAlgorithm != 10 && !IsEnterpriseAuth(network.AuthenticationAlgorithm);

    public static bool WaitForConnection(string ssid, bool shouldConnect, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                var current = ReadNetworks().FirstOrDefault(network => network.IsConnected);
                if (shouldConnect && current?.Name.Equals(ssid, StringComparison.Ordinal) == true) return true;
                if (!shouldConnect && current is null) return true;
            }
            catch (Win32Exception)
            {
            }
            Thread.Sleep(350);
        }
        return false;
    }

    private static WifiNetworkInfo[] ReadNetworks(IntPtr handle, Guid interfaceGuid)
    {
        var result = WlanGetAvailableNetworkList(handle, ref interfaceGuid, 0, IntPtr.Zero, out var listPointer);
        ThrowOnError(result);
        try
        {
            if (listPointer == IntPtr.Zero) return [];
            var count = unchecked((uint)Marshal.ReadInt32(listPointer));
            var elementOffset = Marshal.SizeOf<WlanAvailableNetworkListHeader>();
            var elementSize = Marshal.SizeOf<WlanAvailableNetwork>();
            var bySsid = new Dictionary<string, WifiNetworkInfo>(StringComparer.Ordinal);

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<WlanAvailableNetwork>(IntPtr.Add(listPointer, elementOffset + checked((int)i * elementSize)));
                var ssidBytes = item.Dot11Ssid.Bytes?.Take(checked((int)Math.Min(item.Dot11Ssid.Length, 32))).ToArray() ?? [];
                var name = Encoding.UTF8.GetString(ssidBytes);
                if (string.IsNullOrWhiteSpace(name)) name = "Mạng ẩn";
                var entry = new WifiNetworkInfo(name, item.ProfileName ?? string.Empty, ssidBytes,
                    Math.Min(item.SignalQuality, 100), item.SecurityEnabled != 0,
                    (item.Flags & AvailableNetworkConnected) != 0, item.NetworkConnectable != 0,
                    item.DefaultAuthAlgorithm, item.DefaultCipherAlgorithm);
                var key = Convert.ToBase64String(ssidBytes);
                if (!bySsid.TryGetValue(key, out var current)
                    || (entry.IsConnected && !current.IsConnected)
                    || (entry.HasProfile() && !current.HasProfile())
                    || (entry.SignalQuality > current.SignalQuality && entry.IsConnected == current.IsConnected))
                    bySsid[key] = entry;
            }

            return bySsid.Values.OrderByDescending(network => network.IsConnected)
                .ThenByDescending(network => network.SignalQuality)
                .ThenBy(network => network.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            WlanFreeMemory(listPointer);
        }
    }

    private static string CreateProfile(WifiNetworkInfo network, string? password)
    {
        var ssid = SecurityElement.Escape(network.Name) ?? string.Empty;
        var profileName = SecurityElement.Escape(network.Name) ?? string.Empty;
        var auth = !network.IsSecure ? "open" : network.AuthenticationAlgorithm switch
        {
            4 => "WPAPSK",
            7 => "WPA2PSK",
            9 => "WPA3SAE",
            10 => "OWE",
            _ => "WPA2PSK",
        };
        var cipher = !network.IsSecure ? "none" : network.CipherAlgorithm == 2 ? "TKIP" : "AES";
        var sharedKey = RequiresPassword(network)
            ? $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password ?? string.Empty)}</keyMaterial></sharedKey>"
            : string.Empty;
        return $"""
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{profileName}</name>
              <SSIDConfig><SSID><name>{ssid}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType><connectionMode>manual</connectionMode>
              <MSM><security><authEncryption><authentication>{auth}</authentication><encryption>{cipher}</encryption><useOneX>false</useOneX></authEncryption>{sharedKey}</security></MSM>
            </WLANProfile>
            """;
    }

    private static bool IsEnterpriseAuth(uint auth) => auth is 3 or 6 or 8 or 11;
    private static bool IsSupportedPersonalAuth(WifiNetworkInfo network) =>
        !network.IsSecure || network.AuthenticationAlgorithm is 4 or 7 or 9 or 10;
    private static void ThrowOnError(uint result)
    {
        if (result != ErrorSuccess) throw new Win32Exception(unchecked((int)result));
    }

    private sealed class WlanClient : IDisposable
    {
        public IntPtr Handle { get; }

        public WlanClient()
        {
            var result = WlanOpenHandle(WlanClientVersion, IntPtr.Zero, out _, out var handle);
            ThrowOnError(result);
            Handle = handle;
        }

        public WlanInterfaceInfo? GetFirstInterface()
        {
            ThrowOnError(WlanEnumInterfaces(Handle, IntPtr.Zero, out var listPointer));
            try
            {
                var count = unchecked((uint)Marshal.ReadInt32(listPointer));
                if (count == 0) return null;
                var offset = Marshal.SizeOf<WlanInterfaceInfoListHeader>();
                return Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(listPointer, offset));
            }
            finally
            {
                WlanFreeMemory(listPointer);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) _ = WlanCloseHandle(Handle, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceInfoListHeader { public uint NumberOfItems; public uint Index; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAvailableNetworkListHeader { public uint NumberOfItems; public uint Index; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Dot11Ssid;
        public int BssType;
        public uint NumberOfBssids;
        public int NetworkConnectable;
        public uint NotConnectableReason;
        public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
        public int MorePhyTypes;
        public uint SignalQuality;
        public int SecurityEnabled;
        public uint DefaultAuthAlgorithm;
        public uint DefaultCipherAlgorithm;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.U1)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public uint ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProfileName;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public uint BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr availableNetworkList);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, string profileXml, string? allUserSecurity, [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reasonCode);
    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref WlanConnectionParameters connectionParameters, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);
}

internal static class WifiNetworkInfoExtensions
{
    public static bool HasProfile(this WifiNetworkInfo network) => !string.IsNullOrWhiteSpace(network.ProfileName);
}
