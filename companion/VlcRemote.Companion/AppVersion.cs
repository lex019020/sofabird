using System.Reflection;

namespace VlcRemote.Companion;

/// <summary>Версия сборки: <c>0.2.0</c> для релиза, <c>0.2.0-beta.42</c> для тестовой (из файла VERSION + суффикс CI).</summary>
internal static class AppVersion
{
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
