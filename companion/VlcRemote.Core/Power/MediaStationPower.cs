namespace VlcRemote.Core.Power;

/// <summary>A setting of a Windows power plan: subgroup and setting GUIDs as used by powrprof / <c>powercfg</c>.</summary>
public sealed record PowerSetting(string Key, Guid Subgroup, Guid Setting);

public enum PowerSource
{
    /// <summary>Plugged in.</summary>
    Ac,
    /// <summary>On battery.</summary>
    Dc,
}

public sealed record PowerDeviation(PowerSetting Setting, PowerSource Source, uint Current, uint Wanted);

/// <summary>
/// "Media station" power policy for the PC at the TV: it keeps working with the lid closed and never goes to sleep
/// or hibernates by itself — on mains and on battery (the user's choice: a power cut must not put it to sleep).
/// The screen timeout is not touched: a TV may go to standby on its own.
/// </summary>
public static class MediaStationPower
{
    private static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    /// <summary>Lid close action: 0 do nothing, 1 sleep, 2 hibernate, 3 shut down.</summary>
    public static readonly PowerSetting LidClose = new("lidClose", SubButtons, new("5ca83367-6e45-459f-a27b-476b1d01c936"));
    /// <summary>Sleep after N seconds of inactivity: 0 never.</summary>
    public static readonly PowerSetting SleepAfter = new("sleepAfter", SubSleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));
    /// <summary>Hibernate after N seconds: 0 never.</summary>
    public static readonly PowerSetting HibernateAfter = new("hibernateAfter", SubSleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"));

    public static IReadOnlyList<(PowerSetting Setting, uint Wanted)> Wanted { get; } =
    [
        (LidClose, 0),
        (SleepAfter, 0),
        (HibernateAfter, 0),
    ];

    private static readonly PowerSource[] Sources = [PowerSource.Ac, PowerSource.Dc];

    /// <summary>
    /// Settings of the active plan that differ from the policy. <paramref name="read"/> returns <c>null</c> for a
    /// setting the PC does not have (it is skipped).
    /// </summary>
    public static IReadOnlyList<PowerDeviation> Check(Func<PowerSetting, PowerSource, uint?> read)
    {
        var list = new List<PowerDeviation>();
        foreach (var (setting, wanted) in Wanted)
        {
            foreach (var source in Sources)
            {
                if (read(setting, source) is uint current && current != wanted)
                    list.Add(new PowerDeviation(setting, source, current, wanted));
            }
        }
        return list;
    }

    /// <summary>Stable text of a set of deviations: the tray warns once per distinct set, not on every check.</summary>
    public static string Signature(IReadOnlyList<PowerDeviation> deviations) =>
        string.Join(";", deviations.Select(d => $"{d.Setting.Key}/{d.Source}={d.Current}").Order(StringComparer.Ordinal));
}
