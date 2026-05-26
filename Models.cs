using System.Text.Json.Serialization;

namespace Lyrictified.Server;

public sealed record LyrictifiedSettings
{
    public int Port { get; init; } = 32145;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string LyricsDirectory { get; init; } = "lyrics";
    public string CatalogPath { get; init; } = "data/catalog.json";
    public string AdminPassword { get; init; } = "change-me";
}

public sealed record SearchRequest(string? Query, string? Song, string? Artist, string? Album, int Limit)
{
    public bool HasQuery =>
        !string.IsNullOrWhiteSpace(Query)
        || !string.IsNullOrWhiteSpace(Song)
        || !string.IsNullOrWhiteSpace(Artist)
        || !string.IsNullOrWhiteSpace(Album);

    public bool HasInvalidAlbumSearch =>
        !string.IsNullOrWhiteSpace(Album)
        && string.IsNullOrWhiteSpace(Song);

    public bool HasInvalidArtistOnlySearch =>
        string.IsNullOrWhiteSpace(Query)
        && string.IsNullOrWhiteSpace(Song)
        && !string.IsNullOrWhiteSpace(Artist);
}

public sealed record LyricFile(
    string Id,
    string Title,
    string Artist,
    string Album,
    string Format,
    string RelativePath,
    [property: JsonIgnore]
    string AbsolutePath,
    int Rating,
    IReadOnlyList<WeightedTag> Tags,
    bool Exact,
    bool Ignore,
    bool Reverse,
    string IgnorePatterns)
{
    [JsonIgnore]
    public string TitleSearchText => LyricsIndex.Normalize(Title);
    [JsonIgnore]
    public string ArtistSearchText => LyricsIndex.Normalize(Artist);
    [JsonIgnore]
    public string AlbumSearchText => LyricsIndex.Normalize(Album);
    [JsonIgnore]
    public string TagsSearchText => LyricsIndex.Normalize(string.Join(' ', Tags.Select(tag => tag.Name)));
    [JsonIgnore]
    public string SearchText => LyricsIndex.Normalize($"{Artist} {Title} {Album} {RelativePath} {string.Join(' ', Tags.Select(tag => tag.Name))}");
    [JsonIgnore]
    public string ExactSearchText => LyricsIndex.Normalize($"{Artist} {Title} {string.Join(' ', Tags.Select(tag => tag.Name))}");
}

public sealed record SearchResult(
    string Id,
    string Title,
    string Artist,
    string Album,
    string Format,
    string RelativePath,
    int Rating,
    IReadOnlyList<WeightedTag> Tags,
    bool Exact,
    bool Ignore,
    bool Reverse,
    string IgnorePatterns,
    int Score);

public sealed record LyricMetadataUpdate(
    string? Title,
    string? Artist,
    string? Album,
    int Rating,
    IReadOnlyList<WeightedTag> Tags,
    bool Exact,
    bool Ignore,
    bool Reverse,
    string? IgnorePatterns);

[JsonConverter(typeof(WeightedTagJsonConverter))]
public sealed record WeightedTag(string Name, int Score);

public sealed class CatalogFile
{
    public List<CatalogEntry> Entries { get; set; } = [];
}

public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int Rating { get; set; }
    public List<WeightedTag> Tags { get; set; } = [];
    public bool Exact { get; set; }
    public bool Ignore { get; set; }
    public bool Reverse { get; set; }
    public string? IgnorePatterns { get; set; }
}
