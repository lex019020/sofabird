using System.Text;
using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public class VlcrcConfiguratorTests : IDisposable
{
    private static readonly VlcrcSettings S = new(8080, "Secret123", 4212);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vlcremote-vlcrc-" + Guid.NewGuid().ToString("N"));

    public VlcrcConfiguratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Фрагмент настоящего vlcrc VLC 3.0.x: закомментированные умолчания, в [lua] тоже есть rc-host.</summary>
    private const string Sample =
        "###\r\n" +
        "###  vlc 3.0.23\r\n" +
        "###\r\n" +
        "\r\n" +
        "[lua] # Lua interpreter\r\n" +
        "\r\n" +
        "# Lua interface (string)\r\n" +
        "#intf=\r\n" +
        "\r\n" +
        "# Password (string)\r\n" +
        "#http-password=\r\n" +
        "\r\n" +
        "# TCP command host (string)\r\n" +
        "#rc-host=\r\n" +
        "\r\n" +
        "[oldrc] # Remote control interface\r\n" +
        "\r\n" +
        "# Do not open a DOS command box interface (boolean)\r\n" +
        "#rc-quiet=0\r\n" +
        "\r\n" +
        "# TCP command input (string)\r\n" +
        "#rc-host=\r\n" +
        "\r\n" +
        "[core] # core program\r\n" +
        "\r\n" +
        "# HTTP server address (string)\r\n" +
        "#http-host=\r\n" +
        "\r\n" +
        "# HTTP server port (integer)\r\n" +
        "#http-port=8080\r\n" +
        "\r\n" +
        "# Extra interface modules (string)\r\n" +
        "#extraintf=\r\n" +
        "\r\n";

    private static List<string> Section(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        var inside = false;
        foreach (var l in lines)
        {
            if (l.StartsWith('[')) { inside = l.StartsWith($"[{name}]"); continue; }
            if (inside) result.Add(l);
        }
        return result;
    }

    [Fact]
    public void Apply_UncommentsKeysInTheirSections()
    {
        var result = VlcrcEditor.Apply(Sample, S, out var changed);
        Assert.True(changed);
        Assert.Contains("extraintf=http:rc", Section(result, "core"));
        Assert.Contains("http-port=8080", Section(result, "core"));
        Assert.DoesNotContain("#extraintf=", result);
        Assert.Contains("#http-host=", result); // http-host не трогаем

        var lua = Section(result, "lua");
        Assert.Contains("http-password=Secret123", lua);
        Assert.Contains("#rc-host=", lua); // rc-host в [lua] — чужой ключ, не трогаем
        Assert.DoesNotContain("rc-host=127.0.0.1:4212", lua);

        var oldrc = Section(result, "oldrc");
        Assert.Contains("rc-host=127.0.0.1:4212", oldrc);
        Assert.Contains("rc-quiet=1", oldrc);

        // Ключ заменён на месте, комментарий-описание над ним сохранён, окончания строк — CRLF.
        Assert.Contains("# Extra interface modules (string)\r\nextraintf=http:rc\r\n", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
        Assert.Equal(Sample.Split("\r\n").Length, result.Split("\r\n").Length);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var once = VlcrcEditor.Apply(Sample, S, out _);
        var twice = VlcrcEditor.Apply(once, S, out var changed);
        Assert.False(changed);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("extraintf=qt", "extraintf=qt:http:rc")]
    [InlineData("extraintf=http", "extraintf=http:rc")]
    [InlineData("extraintf=rc:http", "extraintf=rc:http")]
    [InlineData("extraintf=luahttp:oldrc", "extraintf=luahttp:oldrc")]
    [InlineData("extraintf=", "extraintf=http:rc")]
    public void Apply_MergesExistingExtraIntf(string existing, string expected)
    {
        var text = "[core] # core program\n" + existing + "\nhttp-port=8080\n";
        var result = VlcrcEditor.Apply(text, S, out _);
        Assert.Contains(expected + "\n", result);
        Assert.Single(Section(result, "core"), l => l.StartsWith("extraintf="));
    }

    [Fact]
    public void Apply_ChangesWrongValues()
    {
        var text = "[core]\nhttp-port=9090\n[lua]\nhttp-password=old\n[oldrc]\nrc-quiet=0\nrc-host=localhost:1234\n";
        var result = VlcrcEditor.Apply(text, S, out var changed);
        Assert.True(changed);
        Assert.Contains("http-port=8080", result);
        Assert.Contains("http-password=Secret123", result);
        Assert.Contains("rc-quiet=1", result);
        Assert.Contains("rc-host=127.0.0.1:4212", result);
        Assert.DoesNotContain("9090", result);
        Assert.DoesNotContain("old\n", result);
    }

    [Fact]
    public void Apply_AppendsMissingKeyAtEndOfSection_AndMissingSectionAtEndOfFile()
    {
        var text = "[core] # core program\n# something\n#volume=256\n\n[qt] # Qt interface\n#qt-privacy-ask=1\n";
        var result = VlcrcEditor.Apply(text, S, out var changed);
        Assert.True(changed);
        var core = Section(result, "core");
        Assert.Contains("extraintf=http:rc", core);
        Assert.Contains("http-port=8080", core);
        // Ключи вставлены до пустой строки перед [qt], а не после неё.
        Assert.Contains("#volume=256\nextraintf=http:rc\nhttp-port=8080\n\n[qt]", result);
        Assert.Contains("#qt-privacy-ask=1", Section(result, "qt"));
        Assert.EndsWith("[lua]\nhttp-password=Secret123\n\n[oldrc]\nrc-host=127.0.0.1:4212\nrc-quiet=1\n", result);
    }

    [Fact]
    public void MergeExtraIntf_KeepsUserInterfaces()
    {
        Assert.Equal("http:rc", VlcrcEditor.MergeExtraIntf(null));
        Assert.Equal("qt:http:rc", VlcrcEditor.MergeExtraIntf("qt"));
        Assert.Equal("ntservice:http:rc", VlcrcEditor.MergeExtraIntf("ntservice,http"));
        Assert.Equal("HTTP:rc", VlcrcEditor.MergeExtraIntf("HTTP"));
    }

    [Fact]
    public void Ensure_PreservesBom_MakesBackupOnce_AndIsIdempotent()
    {
        var path = Path.Combine(_dir, "vlcrc");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();
        File.WriteAllBytes(path, original);
        var cfg = new VlcrcConfigurator(path);

        Assert.Equal(VlcrcResult.Updated, cfg.Ensure(S));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.NotEqual(0xEF, bytes[3]); // BOM не задвоен
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("extraintf=http:rc\r\n", text);
        Assert.Equal(original, File.ReadAllBytes(cfg.BackupPath));
        Assert.False(File.Exists(path + ".vlcremote-tmp"));

        Assert.Equal(VlcrcResult.Unchanged, cfg.Ensure(S));
        Assert.Equal(bytes, File.ReadAllBytes(path));

        // Пароль поменялся — файл обновляется, но резервная копия остаётся первой (оригинал пользователя).
        Assert.Equal(VlcrcResult.Updated, cfg.Ensure(S with { HttpPassword = "Other" }));
        Assert.Contains("http-password=Other", File.ReadAllText(path));
        Assert.Equal(original, File.ReadAllBytes(cfg.BackupPath));
    }

    [Fact]
    public void Ensure_WithoutBom_WritesWithoutBom()
    {
        var path = Path.Combine(_dir, "vlcrc");
        File.WriteAllText(path, "[core]\n#extraintf=\n", new UTF8Encoding(false));
        Assert.Equal(VlcrcResult.Updated, new VlcrcConfigurator(path).Ensure(S));
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.StartsWith("[core]\nextraintf=http:rc\nhttp-port=8080\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Ensure_MissingFile_CreatesMinimalConfig()
    {
        var path = Path.Combine(_dir, "sub", "vlcrc");
        var cfg = new VlcrcConfigurator(path);
        Assert.Equal(VlcrcResult.Created, cfg.Ensure(S));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(
            "[core]\r\nextraintf=http:rc\r\nhttp-port=8080\r\n\r\n[lua]\r\nhttp-password=Secret123\r\n\r\n[oldrc]\r\nrc-host=127.0.0.1:4212\r\nrc-quiet=1\r\n",
            text);
        Assert.False(File.Exists(cfg.BackupPath));
        Assert.Equal(VlcrcResult.Unchanged, cfg.Ensure(S));
    }

    [Fact]
    public void Ensure_InvalidUtf8_IsLeftUntouched()
    {
        var path = Path.Combine(_dir, "vlcrc");
        var bytes = Encoding.ASCII.GetBytes("[core]\n#extraintf=\n").Concat(new byte[] { 0xC3, 0x28, 0x0A }).ToArray();
        File.WriteAllBytes(path, bytes);
        Assert.Equal(VlcrcResult.Skipped, new VlcrcConfigurator(path).Ensure(S));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
