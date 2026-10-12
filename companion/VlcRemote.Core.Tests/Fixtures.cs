namespace VlcRemote.Core.Tests;

internal static class Fixtures
{
    private static readonly string Dir = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Read(string name) => File.ReadAllText(System.IO.Path.Combine(Dir, name));

    public static string[] ReadLines(string name) =>
        File.ReadAllLines(System.IO.Path.Combine(Dir, name)).Select(l => l.TrimEnd('\r')).ToArray();

    public static string PathOf(string name) => System.IO.Path.Combine(Dir, name);
}
