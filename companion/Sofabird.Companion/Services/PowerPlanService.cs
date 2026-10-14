using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sofabird.Core.Power;

namespace Sofabird.Companion.Services;

/// <summary>
/// Reads and writes the active Windows power plan through powrprof (the same settings as <c>powercfg</c>, without
/// parsing its localized output) to keep the "media station" policy (<see cref="MediaStationPower"/>).
/// </summary>
public sealed class PowerPlanService
{
    private readonly ILogger _log;

    public PowerPlanService(ILogger<PowerPlanService> log) => _log = log;

    /// <summary>Settings of the active plan that break the policy; <c>null</c> — the plan could not be read.</summary>
    public IReadOnlyList<PowerDeviation>? CheckMediaStation()
    {
        try
        {
            var scheme = ActiveScheme();
            return MediaStationPower.Check((setting, source) => Read(scheme, setting, source));
        }
        catch (Win32Exception ex)
        {
            _log.LogWarning("Reading the power plan failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>Writes the policy into the active plan and re-applies it; <c>null</c> on success, else the error text.</summary>
    public string? ApplyMediaStation()
    {
        try
        {
            var scheme = ActiveScheme();
            var deviations = MediaStationPower.Check((setting, source) => Read(scheme, setting, source));
            foreach (var d in deviations)
            {
                var sub = d.Setting.Subgroup;
                var setting = d.Setting.Setting;
                var error = d.Source == PowerSource.Ac
                    ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, d.Wanted)
                    : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, d.Wanted);
                if (error != 0) throw new Win32Exception((int)error);
                _log.LogInformation("Power plan: {Setting} ({Source}) {From} → {To}", d.Setting.Key, d.Source, d.Current, d.Wanted);
            }
            // Written values take effect once the plan is (re)activated.
            var applied = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (applied != 0) throw new Win32Exception((int)applied);
            return null;
        }
        catch (Win32Exception ex)
        {
            _log.LogWarning("Changing the power plan failed: {Error}", ex.Message);
            return ex.Message;
        }
    }

    private static Guid ActiveScheme()
    {
        var error = PowerGetActiveScheme(IntPtr.Zero, out var ptr);
        if (error != 0) throw new Win32Exception((int)error);
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    private static uint? Read(Guid scheme, PowerSetting s, PowerSource source)
    {
        var sub = s.Subgroup;
        var setting = s.Setting;
        var error = source == PowerSource.Ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
        return error == 0 ? value : null; // e.g. ERROR_FILE_NOT_FOUND: the PC does not have this setting
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
