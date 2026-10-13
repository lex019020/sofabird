using System.Runtime.InteropServices;
using VlcRemote.Core.Setup;

namespace VlcRemote.Companion.Setup;

/// <summary>
/// Windows Firewall through COM <c>HNetCfg.FwPolicy2</c> (<c>INetFwPolicy2</c>), late-bound. Reading works without
/// administrator rights; <see cref="Remove"/> and <see cref="Add"/> need an elevated process
/// (<see cref="FirewallElevation"/>).
/// </summary>
public sealed class ComFirewallRuleSource : IFirewallRuleSource
{
    private readonly dynamic _policy;
    private readonly List<object> _native = new();
    private readonly HashSet<string>? _fileNames;

    /// <param name="programs">
    /// Only rules for programs with these file names are returned (each property read is a COM call, and a typical PC
    /// has over a thousand rules); <c>null</c> — all rules bound to a program.
    /// </param>
    public ComFirewallRuleSource(IEnumerable<FirewallProgram>? programs = null)
    {
        if (programs is not null)
            _fileNames = new HashSet<string>(programs.Select(p => Path.GetFileName(p.Path)), StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        _policy = Activator.CreateInstance(type)!;
    }

    public bool IsEnabled(FwProfiles profile) => (bool)_policy.FirewallEnabled[(int)profile];

    public IReadOnlyList<FirewallRule> ReadRules()
    {
        _native.Clear();
        var result = new List<FirewallRule>();
        foreach (var item in Enumerate(_policy.Rules))
        {
            dynamic r = item;
            string? app = r.ApplicationName;
            var index = _native.Count;
            _native.Add(item);
            // Only rules bound to a program matter here; skip the rest cheaply (each property is a COM call).
            if (string.IsNullOrWhiteSpace(app)) continue;
            app = Environment.ExpandEnvironmentVariables(app);
            if (_fileNames is not null && !_fileNames.Contains(SafeFileName(app))) continue;
            result.Add(new FirewallRule(
                index,
                (string?)r.Name ?? "",
                app,
                (FwDirection)(int)r.Direction,
                (FwAction)(int)r.Action,
                (bool)r.Enabled,
                (FwProfiles)(int)r.Profiles,
                (int)r.Protocol));
        }
        return result;
    }

    public void Remove(FirewallRule rule)
    {
        if (rule.Index < 0 || rule.Index >= _native.Count) throw new ArgumentOutOfRangeException(nameof(rule));
        // INetFwRules.Remove takes a name and rules often share one (the Windows prompt names all of them after the
        // exe, allow and block alike): give this rule a unique name first, then remove it by that name.
        dynamic native = _native[rule.Index];
        var unique = "VlcRemote-remove-" + Guid.NewGuid().ToString("N");
        native.Name = unique;
        _policy.Rules.Remove(unique);
    }

    public void Add(FirewallRuleSpec rule)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
        dynamic native = Activator.CreateInstance(type)!;
        native.Name = rule.Name;
        native.Description = rule.Description;
        native.ApplicationName = rule.Program;
        native.Protocol = rule.Protocol; // must be set before ports (none here)
        native.Direction = (int)rule.Direction;
        native.Action = (int)rule.Action;
        native.Profiles = (int)rule.Profiles;
        native.InterfaceTypes = "All";
        if (rule.Grouping is not null) native.Grouping = rule.Grouping;
        native.Enabled = true;
        _policy.Rules.Add(native);
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path.Trim().Trim('"')); }
        catch (ArgumentException) { return path; }
    }

    /// <summary>Enumerates a COM collection via its <c>_NewEnum</c> (<c>IEnumVARIANT</c>).</summary>
    internal static IEnumerable<object> Enumerate(object collection)
    {
        var enumObj = ((dynamic)collection)._NewEnum;
        if (enumObj is not System.Runtime.InteropServices.ComTypes.IEnumVARIANT e)
        {
            foreach (var o in (System.Collections.IEnumerable)collection) yield return o;
            yield break;
        }
        var buffer = new object[1];
        while (e.Next(1, buffer, IntPtr.Zero) == 0)
        {
            if (buffer[0] is { } o) yield return o;
            buffer[0] = null!;
        }
    }

    /// <summary>Reads the state for the given programs; <c>null</c> plus a message when the firewall cannot be read.</summary>
    public static (FirewallStatus? Status, string? Error) TryInspect(IReadOnlyList<FirewallProgram> programs)
    {
        try
        {
            return (FirewallPlanner.Inspect(new ComFirewallRuleSource(programs), programs), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or UnauthorizedAccessException or TypeLoadException)
        {
            return (null, ex.Message);
        }
    }
}
