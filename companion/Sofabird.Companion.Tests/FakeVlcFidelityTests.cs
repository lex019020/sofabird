using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sofabird.Companion.Tests.Fake;
using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Companion.Tests;

/// <summary>
/// The fake VLC answers in the shapes of the real VLC 3.0.23 responses (<c>docs/research/samples</c>): the same fields
/// with the same JSON types, so the scenario tests exercise the parsers the way real VLC does.
/// </summary>
public sealed class FakeVlcFidelityTests : IDisposable
{
    private const string Password = "pw";
    private static readonly FakeMedia Movie = FakeMedia.Movie("file:///D:/Video/Movies/Arrival.2016.mkv", "Arrival", 6960);

    private readonly FakeVlc _vlc = new();
    private readonly HttpClient _http;

    public FakeVlcFidelityTests()
    {
        _http = new HttpClient(_vlc.CreateHttpHandler()) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Password)));
    }

    public void Dispose()
    {
        _http.Dispose();
        _vlc.Dispose();
    }

    private static JsonElement Sample(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name))).RootElement;

    private async Task<JsonElement> GetAsync(string path) => JsonDocument.Parse(await _http.GetStringAsync(path)).RootElement;

    /// <summary>Field name → JSON type (<c>true</c>/<c>false</c> are one type), sorted.</summary>
    private static List<string> Shape(JsonElement obj, params string[] except) =>
        obj.EnumerateObject()
            .Where(p => !except.Contains(p.Name))
            .Select(p => $"{p.Name}:{(p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? "Boolean" : p.Value.ValueKind.ToString())}")
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task Status_json_has_the_fields_and_types_of_real_vlc_while_playing_and_stopped()
    {
        _vlc.StartProcess(Password, null, Movie);

        var playing = await GetAsync("requests/status.json");
        var sample = Sample("status-playing.json");
        Assert.Equal(Shape(sample), Shape(playing));
        Assert.Equal(Shape(sample.GetProperty("information")), Shape(playing.GetProperty("information")));
        var sampleStream = sample.GetProperty("information").GetProperty("category").GetProperty("Stream 1");
        var stream = playing.GetProperty("information").GetProperty("category").GetProperty("Stream 1");
        Assert.Equal(Shape(sampleStream, "Sample_rate"), Shape(stream));

        var stopped = await GetAsync("requests/status.json?command=pl_stop");
        Assert.Equal(Shape(Sample("status-stopped.json")), Shape(stopped));
        Assert.Equal(JsonValueKind.Number, stopped.GetProperty("fullscreen").ValueKind); // 0, not false, without a video output
        Assert.Equal(-1, stopped.GetProperty("currentplid").GetInt32());
    }

    [Fact]
    public async Task Playlist_json_is_the_tree_of_real_vlc()
    {
        _vlc.StartProcess(Password, null, Movie);

        var root = await GetAsync("requests/playlist.json");
        var sample = Sample("playlist.json");
        Assert.Equal(Shape(sample), Shape(root));
        var nodes = root.GetProperty("children").EnumerateArray().ToList();
        var sampleNodes = sample.GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(sampleNodes.Select(n => n.GetProperty("name").GetString()), nodes.Select(n => n.GetProperty("name").GetString()));
        Assert.Equal(Shape(sampleNodes[0].GetProperty("children")[0]), Shape(nodes[0].GetProperty("children")[0]));
        Assert.Equal(3, VlcJsonParser.ParsePlaylist(root.GetRawText()).Single().PlId);
    }

    [Fact]
    public async Task Tracks_json_has_the_lists_of_our_lua_page()
    {
        _vlc.StartProcess(Password, null, Movie);

        var page = await GetAsync("requests/tracks.json");
        // The sample was taken with an older page that also listed "device"; the page now starts with its marker.
        Assert.Equal(Shape(Sample("tracks-custom-page.json"), "device").Append("sofabird:Number").Order(StringComparer.Ordinal), Shape(page));
        var tracks = VlcJsonParser.ParseTracksPage(page.GetRawText());
        Assert.Equal(1, tracks.Sofabird);
        Assert.Equal(1, tracks.Audio!.CurrentInt);
        Assert.Equal(["Disable", "Original / AC3 5.1 @ 448 kbps - [English]", "Dub / AC3 5.1 @ 448 kbps - [Russian]"], tracks.Audio.Items.Select(i => i.Name));
        Assert.Equal(-1, tracks.Spu!.CurrentInt);
    }

    [Fact]
    public async Task Http_errors_are_those_of_real_vlc()
    {
        _vlc.StartProcess("another", null, Movie);
        using var denied = await _http.GetAsync("requests/status.json");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("Basic", denied.Headers.WwwAuthenticate.Single().Scheme);

        _vlc.Exit();
        _vlc.StartProcess(Password, null, Movie);
        _vlc.LuaPages = false;
        using var missing = await _http.GetAsync("requests/tracks.json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        _vlc.Exit();
        await Assert.ThrowsAsync<HttpRequestException>(() => _http.GetAsync("requests/status.json"));
    }
}
