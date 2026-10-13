using System.Runtime.InteropServices;
using VlcRemote.Core.Displays;

namespace VlcRemote.Companion.Interop;

/// <summary>
/// CCD API (<c>QueryDisplayConfig</c> + <c>DisplayConfigGetDeviceInfo</c>): the monitors behind the active GDI
/// sources with their EDID friendly names, stable device paths and connector types.
/// </summary>
internal static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>Active path targets; empty when the API fails (e.g. a session without a real desktop).</summary>
    public static IReadOnlyList<DisplayTarget> QueryActiveTargets(out int error)
    {
        error = ERROR_SUCCESS;
        DISPLAYCONFIG_PATH_INFO[] paths;
        uint pathCount;
        for (var attempt = 0; ; attempt++)
        {
            error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out var modeCount);
            if (error != ERROR_SUCCESS) return [];
            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == ERROR_SUCCESS) break;
            // The topology changed between the two calls — retry a few times.
            if (error != ERROR_INSUFFICIENT_BUFFER || attempt >= 3) return [];
        }

        var result = new List<DisplayTarget>((int)pathCount);
        for (var i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != ERROR_SUCCESS || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id,
                },
            };
            var ok = DisplayConfigGetDeviceInfo(ref target) == ERROR_SUCCESS;
            result.Add(new DisplayTarget(
                source.viewGdiDeviceName,
                ok ? target.monitorDevicePath : null,
                ok ? target.monitorFriendlyDeviceName : null,
                ok ? target.outputTechnology : p.targetInfo.outputTechnology));
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    /// <summary>72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>64 bytes; the mode union (target/source/desktop image) is not needed and kept opaque.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    /// <summary>Layout self-check (sizes must match the Windows SDK) — logged at startup if wrong.</summary>
    public static string? CheckLayout()
    {
        var problems = new List<string>();
        void Check<T>(int expected) where T : struct
        {
            var actual = Marshal.SizeOf<T>();
            if (actual != expected) problems.Add($"{typeof(T).Name}={actual} (expected {expected})");
        }
        Check<DISPLAYCONFIG_PATH_INFO>(72);
        Check<DISPLAYCONFIG_MODE_INFO>(64);
        Check<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(84);
        Check<DISPLAYCONFIG_TARGET_DEVICE_NAME>(420);
        return problems.Count == 0 ? null : string.Join(", ", problems);
    }
}
