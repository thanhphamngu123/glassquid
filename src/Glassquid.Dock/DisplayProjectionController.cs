using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Glassquid.Dock;

internal enum DisplayProjectionMode : uint
{
    PcScreenOnly = 1,
    Duplicate = 2,
    Extend = 3,
    SecondScreenOnly = 4,
}

/// <summary>Reads and applies the Windows display topology used by the Win+P selector.</summary>
internal static class DisplayProjectionController
{
    private const uint QueryDatabaseCurrent = 0x00000004;
    private const uint SetApply = 0x00000080;
    private const int DisplayConfigPathInfoSize = 72;
    private const int DisplayConfigModeInfoSize = 64;
    private const int ErrorInsufficientBuffer = 122;

    public static DisplayProjectionMode? GetCurrentMode()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(QueryDatabaseCurrent, out var pathCount, out var modeCount);
            if (result != 0) throw new Win32Exception(result);

            var pathBuffer = Marshal.AllocHGlobal(checked((int)Math.Max(pathCount, 1u) * DisplayConfigPathInfoSize));
            var modeBuffer = Marshal.AllocHGlobal(checked((int)Math.Max(modeCount, 1u) * DisplayConfigModeInfoSize));
            try
            {
                var actualPathCount = pathCount;
                var actualModeCount = modeCount;
                result = QueryDisplayConfig(
                    QueryDatabaseCurrent,
                    ref actualPathCount,
                    pathBuffer,
                    ref actualModeCount,
                    modeBuffer,
                    out var topology);

                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) throw new Win32Exception(result);

                return topology switch
                {
                    1 => DisplayProjectionMode.PcScreenOnly,
                    2 => DisplayProjectionMode.Duplicate,
                    3 => DisplayProjectionMode.Extend,
                    4 => DisplayProjectionMode.SecondScreenOnly,
                    _ => null,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuffer);
                Marshal.FreeHGlobal(modeBuffer);
            }
        }

        throw new InvalidOperationException("Windows display topology changed while it was being read.");
    }

    public static void Apply(DisplayProjectionMode mode)
    {
        var topologyFlag = mode switch
        {
            DisplayProjectionMode.PcScreenOnly => 0x00000001u,
            DisplayProjectionMode.Duplicate => 0x00000002u,
            DisplayProjectionMode.Extend => 0x00000004u,
            DisplayProjectionMode.SecondScreenOnly => 0x00000008u,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var result = SetDisplayConfig(
            0,
            0,
            0,
            0,
            SetApply | topologyFlag);
        if (result != 0) throw new Win32Exception(result);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        nint pathArray,
        ref uint numModeInfoArrayElements,
        nint modeInfoArray,
        out uint currentTopologyId);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        nint pathArray,
        uint numModeInfoArrayElements,
        nint modeInfoArray,
        uint flags);
}
