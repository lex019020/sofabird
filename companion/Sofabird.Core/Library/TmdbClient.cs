using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sofabird.Core.Library;

/// <summary>What TMDB knows about a movie or a series.</summary>
/// <param name="Id">TMDB id (unique per <paramref name="Kind"/>).</param>
/// <param name="Kind"><c>movie</c> or <c>tv</c>.</param>
/// <param name="Poster">TMDB image path (<c>/abc.jpg</c>); see <see cref="TmdbClient.ImageUrl"/>.</param>
public sealed record TmdbMatch(int Id, string Kind, string Title, int? Year, string? Overview, string? Poster,
    string? Backdrop, double? Rating);

/// <summary>An episode's own name, description and still, from TMDB's season details.</summary>
public sealed record TmdbEpisode(int Season, int Episode, string? Title, string? Overview, string? Still);

/// <summary>
/// The TMDB API v3 with a read access token (<c>Authorization: Bearer</c>). Searches in the given language
/// (<c>ru-RU</c>, …); a title not found with its year is searched again without it. Attribution and the 6-month cache
/// limit of the TMDB terms are the caller's job (<see cref="MetadataCache"/>).
/// </summary>
public sealed class TmdbClient : IMetadataSource
{
    public const string ImageBase = "https://image.tmdb.org/t/p/";
    private static readonly Uri ApiBase = new("https://api.themoviedb.org/3/");

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _language;

    /// <param name="http">Its lifetime belongs to the caller; the token is set on each request.</param>
    public TmdbClient(HttpClient http, string token, string language)
    {
        _http = http;
        _token = token;
        _language = language;
    }

    /// <summary>A full image URL: <c>w342</c> for posters on the phone, <c>w780</c>/<c>w1280</c> for backdrops.</summary>
    public static string? ImageUrl(string? path, string size) => string.IsNullOrEmpty(path) ? null : ImageBase + size + path;

    public Task<TmdbMatch?> FindMovieAsync(string title, int? year, CancellationToken ct) =>
        FindAsync("movie", title, year, ct);

    public Task<TmdbMatch?> FindShowAsync(string title, int? year, CancellationToken ct) =>
        FindAsync("tv", title, year, ct);

    /// <summary>Episode names, descriptions and stills of one season; empty when TMDB has none.</summary>
    public async Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int showId, int season, CancellationToken ct)
    {
        using var doc = await GetAsync($"tv/{showId}/season/{season}?language={_language}", ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("episodes", out var episodes)) return [];
        var result = new List<TmdbEpisode>();
        foreach (var e in episodes.EnumerateArray())
        {
            result.Add(new TmdbEpisode(
                Int(e, "season_number") ?? season, Int(e, "episode_number") ?? 0,
                Str(e, "name"), Str(e, "overview"), Str(e, "still_path")));
        }
        return result;
    }

    private async Task<TmdbMatch?> FindAsync(string kind, string title, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var yearParam = kind == "movie" ? "primary_release_year" : "first_air_date_year";
        var query = $"search/{kind}?query={Uri.EscapeDataString(title)}&language={_language}&include_adult=false";
        var match = year is int y ? await SearchAsync(kind, $"{query}&{yearParam}={y}", ct).ConfigureAwait(false) : null;
        // The year in a release name is sometimes off by one (a festival premiere vs the release): try without it.
        return match ?? await SearchAsync(kind, query, ct).ConfigureAwait(false);
    }

    private async Task<TmdbMatch?> SearchAsync(string kind, string query, CancellationToken ct)
    {
        using var doc = await GetAsync(query, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results)) return null;
        foreach (var r in results.EnumerateArray())
        {
            var title = Str(r, kind == "movie" ? "title" : "name");
            var id = Int(r, "id");
            if (id is null || title is null) continue;
            var date = Str(r, kind == "movie" ? "release_date" : "first_air_date");
            int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                ? y : null;
            double? rating = r.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            return new TmdbMatch(id.Value, kind, title, year, Str(r, "overview"), Str(r, "poster_path"), Str(r, "backdrop_path"),
                rating is > 0 ? Math.Round(rating.Value, 1) : null);
        }
        return null;
    }

    /// <returns><c>null</c> for 404; throws on other failures (the caller retries later).</returns>
    private async Task<JsonDocument?> GetAsync(string relative, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
