using System.Reflection;

namespace Sofabird.Companion;

/// <summary>Build version: <c>0.2.0</c> for a release, <c>0.2.0-beta0042</c> for a test build (from the VERSION file + the CI suffix).</summary>
internal static class AppVersion
{
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
