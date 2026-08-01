using System.Text.Json.Serialization;

namespace Lyrictified.Server;

public sealed record LyrictifiedSettings
{
    public int Port { get; init; } = 32145;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string LyricsDirectory { get; init; } = "lyrics";
    public string CatalogPath { get; init; } = "data/catalog.json";
    public string PendingSubmissionsPath { get; init; } = "data/pending-submissions.json";
    public string LrclibCachePath { get; init; } = "data/lrclib-cache.json";
    public string LrclibCacheDirectory { get; init; } = "lrclib-cache";
    public int LrclibCacheAutoCleanMaxAgeDays { get; init; } = 3;
    public int LrclibCacheAutoCleanCheckIntervalHours { get; init; } = 1;
    public string AdminPasswordHash { get; init; } = "";
    public string AdminPassword { get; init; } = "";
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
    bool Ignore,
    bool Reverse,
    string IgnorePatterns,
    double Offset)
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
    bool Ignore,
    bool Reverse,
    string IgnorePatterns,
    double Offset,
    int Score);

public sealed record LyricMetadataUpdate(
    string? Title,
    string? Artist,
    string? Album,
    int Rating,
    IReadOnlyList<WeightedTag> Tags,
    bool Ignore,
    bool Reverse,
    string? IgnorePatterns,
    double Offset);

public abstract record MetadataUpdateOutcome;

public sealed record MetadataUpdateSuccess(LyricFile File) : MetadataUpdateOutcome;

public sealed record MetadataUpdateConflict(ArtistRenameConflictResponse Conflicts) : MetadataUpdateOutcome;

public sealed record ArtistRenameConflict(
    string ConflictId,
    string SourceRelativePath,
    string TargetRelativePath);

public sealed record ArtistRenameConflictResponse(
    string ConflictKey,
    IReadOnlyList<ArtistRenameConflict> Conflicts);

public sealed record RenameResolveRequest(
    string ConflictKey,
    IReadOnlyDictionary<string, string> Decisions);

public sealed record ArtistRenamePlan(
    string ConflictKey,
    string LyricId,
    LyricMetadataUpdate Update,
    IReadOnlyList<RenameMove> Moves,
    DateTimeOffset CreatedAt);

public sealed class RenameMove
{
    public required string SourceRelativePath { get; init; }
    public required string TargetRelativePath { get; init; }
    public bool ConflictSame { get; init; }
    public bool ConflictDifferent { get; init; }
    public string ConflictId { get; init; } = "";
}

public sealed record LyricSubmissionRequest(
    string? Title,
    string? Artist,
    string? Album,
    string? Format,
    string? Lyrics);

public sealed record PendingLyricSubmission(
    string Id,
    string Title,
    string Artist,
    string Album,
    string Format,
    string Lyrics,
    string SubmitterKey,
    DateTimeOffset SubmittedAt,
    string SuggestedRelativePath);

public sealed record SubmissionApprovalResult(
    PendingLyricSubmission Submission,
    string RelativePath);

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
    public bool Ignore { get; set; }
    public bool Reverse { get; set; }
    public string? IgnorePatterns { get; set; }
    public double Offset { get; set; }
}

public sealed class PendingSubmissionsFile
{
    public List<PendingLyricSubmission> Submissions { get; set; } = [];
    public List<SubmissionRateLimitEntry> RateLimits { get; set; } = [];
}

public sealed class SubmissionRateLimitEntry
{
    public string SubmitterKey { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
}

public sealed record LrclibCachedTrack(
    string Id,
    string Title,
    string Artist,
    string Album,
    double? Duration,
    string Format,
    string RelativePath,
    string AbsolutePath,
    DateTimeOffset CachedAt,
    string SearchQuery);

public sealed class LrclibCacheFile
{
    public List<LrclibCachedTrack> Tracks { get; set; } = [];
}

public sealed class LrclibApiTrack
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public double? Duration { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
}
