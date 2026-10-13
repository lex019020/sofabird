using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlcRemote.Core.Library;

/// <summary>One cached TMDB lookup: the match (or none found) and when and in which language it was made.</summary>
public sealed record CachedMatch(TmdbMatch? Match, DateTimeOffset FetchedAt, string Language);

/// <summary>One cached season of a series: its episodes as TMDB names them.</summary>
public sealed record CachedSeason(IReadOnlyList<TmdbEpisode> Episodes, DateTimeOffset FetchedAt, string Language);

/// <summary>The file: lookups by title and seasons by show id.</summary>
public sealed class MetadataCacheData
{
    public Dictionary<string, CachedMatch> Titles { get; set; } = new();
    public Dictionary<string, CachedSeason> Seasons { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MetadataCacheData))]
internal sealed partial class MetadataCacheJsonContext : JsonSerializerContext;

/// <summary>
/// TMDB answers kept on disk (<c>library-metadata.json</c>), so the library is not looked up again on every start.
/// The TMDB terms allow keeping data for at most 6 months; a title TMDB did not know is asked again after a week (it
/// may have been added since); a change of the language asks again. Not thread-safe: one owner (the library service).
/// </summary>
public sealed class MetadataCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);
    public static readonly TimeSpan NotFoundAge = TimeSpan.FromDays(7);

    private readonly string _path;
    private MetadataCacheData _data;
    private bool _dirty;

    public MetadataCache(string path)
    {
        _path = path;
        _data = Load(path);
    }

    public static string TitleKey(string kind, string titleKey, int? year) => $"{kind}|{titleKey}|{year}";

    public static string SeasonKey(int showId, int season) => $"tv:{showId}:{season}";

    /// <returns><c>false</c> when it must be looked up (never, too old, another language).</returns>
    public bool TryGet(string key, string language, DateTimeOffset now, out TmdbMatch? match)
    {
        match = null;
        if (!_data.Titles.TryGetValue(key, out var entry) || !IsFresh(entry.FetchedAt, entry.Language, entry.Match is not null, language, now))
            return false;
        match = entry.Match;
        return true;
    }

    public void Set(string key, string language, TmdbMatch? match, DateTimeOffset now)
    {
        _data.Titles[key] = new CachedMatch(match, now, language);
        _dirty = true;
    }

    public bool TryGetSeason(string key, string language, DateTimeOffset now, out IReadOnlyList<TmdbEpisode> episodes)
    {
        episodes = [];
        if (!_data.Seasons.TryGetValue(key, out var entry) || !IsFresh(entry.FetchedAt, entry.Language, true, language, now))
            return false;
        episodes = entry.Episodes;
        return true;
    }

    public void SetSeason(string key, string language, IReadOnlyList<TmdbEpisode> episodes, DateTimeOffset now)
    {
        _data.Seasons[key] = new CachedSeason(episodes, now, language);
        _dirty = true;
    }

    /// <summary>Writes the changes and drops the entries the TMDB terms do not allow to keep any longer.</summary>
    public void Save(DateTimeOffset now)
    {
        var expired = _data.Titles.Where(e => now - e.Value.FetchedAt > MaxAge).Select(e => e.Key).ToList();
        expired.ForEach(k => _data.Titles.Remove(k));
        var expiredSeasons = _data.Seasons.Where(e => now - e.Value.FetchedAt > MaxAge).Select(e => e.Key).ToList();
        expiredSeasons.ForEach(k => _data.Seasons.Remove(k));
        if (!_dirty && expired.Count == 0 && expiredSeasons.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, MetadataCacheJsonContext.Default.MetadataCacheData));
        File.Move(tmp, _path, overwrite: true);
        _dirty = false;
    }

    private static bool IsFresh(DateTimeOffset fetched, string fetchedLanguage, bool found, string language, DateTimeOffset now) =>
        fetchedLanguage == language && now - fetched < (found ? MaxAge : NotFoundAge);

    private static MetadataCacheData Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), MetadataCacheJsonContext.Default.MetadataCacheData) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new(); // a broken cache is only a cache: look everything up again
        }
    }
}
