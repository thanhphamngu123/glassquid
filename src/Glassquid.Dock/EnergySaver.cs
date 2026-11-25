using System.Runtime.InteropServices;

namespace Glassquid.Dock;

/// <summary>Reads and toggles the Windows Energy Saver override through its WNF state.</summary>
internal static class EnergySaver
{
    private const ulong WnfPoEnergySaverOverride = 0x41C6013DA3BC3075UL;

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryWnfStateData(
        ref ulong StateName,
        IntPtr TypeId,
        IntPtr ExplicitScope,
        out int ChangeStamp,
        byte[] Buffer,
        ref int BufferSize);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtUpdateWnfStateData(
        ref ulong StateName,
        byte[] Buffer,
        int Length,
        IntPtr TypeId,
        IntPtr ExplicitScope,
        int MatchingChangeStamp,
        int CheckChangeStamp);

    /// <summary>Returns true when Energy Saver is on, false when off, or null if unreadable.</summary>
    internal static bool? IsEnabled()
    {
        try
        {
            var stateName = WnfPoEnergySaverOverride;
            var buffer = new byte[16];
            var bufferSize = buffer.Length;
            var status = NtQueryWnfStateData(
                ref stateName,
                IntPtr.Zero,
                IntPtr.Zero,
                out _,
                buffer,
                ref bufferSize);

            if (status != 0 || bufferSize < sizeof(int)) return null;
            return BitConverter.ToInt32(buffer, 0) == 1;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Toggles Energy Saver and returns the requested state, or null on failure.</summary>
    internal static bool? Toggle()
    {
        var current = IsEnabled();
        if (current is null) return null;

        var target = !current.Value;
        var data = BitConverter.GetBytes(target ? 1 : 2);
        var stateName = WnfPoEnergySaverOverride;
        var status = NtUpdateWnfStateData(
            ref stateName,
            data,
            data.Length,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            0);

        return status == 0 ? target : null;
    }
}
