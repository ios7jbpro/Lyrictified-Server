using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lyrictified.Server;

public sealed class LyricsIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lock _lock = new();
    private readonly LyrictifiedSettings _settings;
    private readonly ILogger<LyricsIndex> _logger;
    private List<LyricFile> _lyrics = [];
    private CatalogFile _catalog = new();

    public LyricsIndex(LyrictifiedSettings settings, ILogger<LyricsIndex> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<LyricFile> All
    {
        get
        {
            lock (_lock)
            {
                return _lyrics.ToArray();
            }
        }
    }

    public void Refresh()
    {
        lock (_lock)
        {
            _catalog = LoadCatalog();
            var metadataById = _catalog.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);

            _lyrics = Directory
                .EnumerateFiles(_settings.LyricsDirectory, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedLyricFile)
                .Select(path => ToLyricFile(path, metadataById))
                .OrderBy(file => file.Artist)
                .ThenBy(file => file.Title)
                .ThenBy(file => file.RelativePath)
                .ToList();

            foreach (var file in _lyrics)
            {
                LyricOffsetHelper.SyncOffsetFile(file.AbsolutePath, file.Offset);
            }

            _logger.LogInformation(
                "Loaded {EntryCount} catalog entrie(s) from {CatalogPath}; indexed {LyricCount} lyric file(s) from {LyricsDirectory}",
                _catalog.Entries.Count,
                _settings.CatalogPath,
                _lyrics.Count,
                _settings.LyricsDirectory);
        }
    }

    public LyricFile? Find(string id)
    {
        lock (_lock)
        {
            return _lyrics.FirstOrDefault(file => file.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<SearchResult> Search(SearchRequest request)
    {
        lock (_lock)
        {
            return _lyrics
                .Select(file => new SearchResult(
                    file.Id,
                    file.Title,
                    file.Artist,
                    file.Album,
                    file.Format,
                    file.RelativePath,
                    file.Rating,
                    file.Tags,
                    file.Exact,
                    file.Ignore,
                    file.Reverse,
                    file.IgnorePatterns,
                    file.Offset,
                    Score(file, request)))
                .Where(result => result.Score > 0)
                .OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.Rating)
                .ThenBy(result => result.Artist)
                .ThenBy(result => result.Title)
                .Take(request.Limit)
                .ToArray();
        }
    }

    public LyricFile? UpdateMetadata(string id, LyricMetadataUpdate update)
    {
        lock (_lock)
        {
            var current = _lyrics.FirstOrDefault(file => file.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            var entry = _catalog.Entries.FirstOrDefault(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new CatalogEntry { Id = current.Id };
                _catalog.Entries.Add(entry);
            }

            entry.Title = EmptyToNull(update.Title);
            entry.Artist = EmptyToNull(update.Artist);
            entry.Album = EmptyToNull(update.Album);
            entry.Rating = Math.Clamp(update.Rating, 0, 100);
            entry.Exact = update.Exact;
            entry.Ignore = update.Ignore;
            entry.Reverse = update.Reverse;
            entry.IgnorePatterns = EmptyToNull(update.IgnorePatterns);
            entry.Offset = Math.Round(Math.Clamp(update.Offset, -2.0, 2.0), 1);
            entry.Tags = update.Tags
                .Select(tag => new WeightedTag(tag.Name.Trim(), Math.Clamp(tag.Score, 0, 100)))
                .Where(tag => tag.Name.Length > 0)
                .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(tag => tag.Score).First())
                .OrderBy(tag => tag.Name)
                .ToList();

            SaveCatalog();
        }

        Refresh();
        return Find(id);
    }

    private LyricFile ToLyricFile(string absolutePath, IReadOnlyDictionary<string, CatalogEntry> metadataById)
    {
        var relativePath = Path.GetRelativePath(_settings.LyricsDirectory, absolutePath);
        var fileName = Path.GetFileNameWithoutExtension(absolutePath);
        var extension = Path.GetExtension(absolutePath).TrimStart('.').ToLowerInvariant();
        var id = CreateStableId(relativePath);
        var pathParts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var inferredAlbum = InferAlbumFromPath(pathParts);
        var inferredArtist = InferArtistFromPath(pathParts);
        var inferredTitle = InferTitle(fileName, inferredArtist, pathParts.Length >= 3);

        metadataById.TryGetValue(id, out var metadata);

        return new LyricFile(
            Id: id,
            Title: metadata?.Title ?? inferredTitle,
            Artist: metadata?.Artist ?? inferredArtist,
            Album: metadata?.Album ?? inferredAlbum,
            Format: extension,
            RelativePath: relativePath.Replace('\\', '/'),
            AbsolutePath: absolutePath,
            Rating: metadata?.Rating ?? 0,
            Tags: metadata?.Tags ?? [],
            Exact: metadata?.Exact ?? false,
            Ignore: metadata?.Ignore ?? false,
            Reverse: metadata?.Reverse ?? false,
            IgnorePatterns: metadata?.IgnorePatterns ?? "",
            Offset: metadata?.Offset ?? 0);
    }

    private static string InferTitle(string fileName, string pathArtist, bool hasArtistAlbumFolders)
    {
        var fileNameParts = fileName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (fileNameParts.Length != 2)
        {
            return fileName;
        }

        if (!hasArtistAlbumFolders)
        {
            return fileNameParts[1];
        }

        if (IsSameSearchText(fileNameParts[0], pathArtist))
        {
            return fileNameParts[1];
        }

        if (IsSameSearchText(fileNameParts[1], pathArtist))
        {
            return fileNameParts[0];
        }

        return fileName;
    }

    private static string InferArtistFromPath(IReadOnlyList<string> pathParts)
    {
        if (pathParts.Count >= 3)
        {
            return pathParts[^3];
        }

        var fileName = pathParts.Count == 1 ? Path.GetFileNameWithoutExtension(pathParts[0]) : "";
        var fileNameParts = fileName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return fileNameParts.Length == 2 ? fileNameParts[0] : "";
    }

    private static string InferAlbumFromPath(IReadOnlyList<string> pathParts)
    {
        return pathParts.Count >= 3 ? pathParts[^2] : "";
    }

    private static bool IsSameSearchText(string left, string right)
    {
        return Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    }

    private CatalogFile LoadCatalog()
    {
        if (!File.Exists(_settings.CatalogPath))
        {
            _logger.LogInformation("Catalog file does not exist yet: {CatalogPath}", _settings.CatalogPath);
            return new CatalogFile();
        }

        var json = File.ReadAllText(_settings.CatalogPath);
        return JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions) ?? new CatalogFile();
    }

    private void SaveCatalog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settings.CatalogPath)!);

        var json = JsonSerializer.Serialize(_catalog, JsonOptions);
        var tempPath = $"{_settings.CatalogPath}.tmp";
        var backupPath = $"{_settings.CatalogPath}.bak";

        File.WriteAllText(tempPath, json);

        if (File.Exists(_settings.CatalogPath))
        {
            File.Copy(_settings.CatalogPath, backupPath, overwrite: true);
        }

        File.Move(tempPath, _settings.CatalogPath, overwrite: true);
        _logger.LogInformation("Saved {EntryCount} catalog entrie(s) to {CatalogPath}", _catalog.Entries.Count, _settings.CatalogPath);
    }

    private static bool IsSupportedLyricFile(string path)
    {
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(path);
        return (extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".elrc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttml", StringComparison.OrdinalIgnoreCase))
            && !fileName.Contains(".offset.", StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(LyricFile file, SearchRequest request)
    {
        var score = 0;
        var requestSearchText = RequestSearchText(request);

        if (!PassesAdminSearchRules(file, requestSearchText))
        {
            return 0;
        }

        var songScore = string.IsNullOrWhiteSpace(request.Song)
            ? 0
            : Math.Max(FieldScore(file.TitleSearchText, request.Song), BestTagScore(file, request.Song));
        var artistScore = string.IsNullOrWhiteSpace(request.Artist)
            ? 0
            : Math.Max(FieldScore(file.ArtistSearchText, request.Artist), BestTagScore(file, request.Artist));
        var albumScore = string.IsNullOrWhiteSpace(request.Album)
            ? 0
            : FieldScore(file.AlbumSearchText, request.Album);

        if (!string.IsNullOrWhiteSpace(request.Song) && songScore == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(request.Artist) && artistScore == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(request.Album) && albumScore == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var effectiveQuery = EffectiveQueryForScoring(file, request.Query);
            var queryScore = FieldScore(file.SearchText, effectiveQuery);
            if (queryScore == 0 || !QueryTouchesTitleOrTags(file, effectiveQuery))
            {
                return 0;
            }

            score += queryScore * 2;
            score += BestTagScore(file, effectiveQuery);
        }

        if (!string.IsNullOrWhiteSpace(request.Song))
        {
            score += songScore * 4;
        }

        if (!string.IsNullOrWhiteSpace(request.Artist))
        {
            score += artistScore * 4;
        }

        if (!string.IsNullOrWhiteSpace(request.Album))
        {
            score += albumScore * 3;
        }

        return score > 0 ? score + file.Rating : 0;
    }

    private static bool PassesAdminSearchRules(LyricFile file, string requestSearchText)
    {
        if (file.Exact && HasTokenOutsideAllowedText(requestSearchText, file.ExactSearchText))
        {
            return false;
        }

        if (!file.Ignore)
        {
            return true;
        }

        var hasPatternMatch = SplitPatterns(file.IgnorePatterns)
            .Any(pattern => WildcardMatches(requestSearchText, pattern));

        return file.Reverse ? hasPatternMatch : !hasPatternMatch;
    }

    private static bool QueryTouchesTitleOrTags(LyricFile file, string query)
    {
        var titleAndTags = $"{file.TitleSearchText} {file.TagsSearchText}";
        return NormalizeQuery(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => titleAndTags.Contains(word, StringComparison.Ordinal));
    }

    private static string EffectiveQueryForScoring(LyricFile file, string query)
    {
        if (!file.Ignore)
        {
            return query;
        }

        var patterns = SplitPatterns(file.IgnorePatterns).ToArray();
        if (patterns.Length == 0)
        {
            return query;
        }

        var keptWords = NormalizeQuery(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !patterns.Any(pattern => WildcardMatches(word, pattern)));

        return string.Join(' ', keptWords);
    }

    private static string RequestSearchText(SearchRequest request)
    {
        return NormalizeQuery($"{request.Query} {request.Song} {request.Artist} {request.Album}");
    }

    private static bool HasTokenOutsideAllowedText(string requestSearchText, string allowedSearchText)
    {
        var allowedTokens = allowedSearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        return requestSearchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => !allowedTokens.Contains(token));
    }

    private static IEnumerable<string> SplitPatterns(string patterns)
    {
        return patterns
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => pattern.Length > 0);
    }

    private static bool WildcardMatches(string requestSearchText, string pattern)
    {
        var normalizedPattern = NormalizeWildcardPattern(pattern);
        if (normalizedPattern.Length == 0)
        {
            return false;
        }

        var regexPattern = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(requestSearchText, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeWildcardPattern(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '*' ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int BestTagScore(LyricFile file, string query)
    {
        return file.Tags
            .Select(tag =>
            {
                var matchScore = FieldScore(Normalize(tag.Name), query);
                return matchScore > 0 ? matchScore + tag.Score : 0;
            })
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int FieldScore(string field, string query)
    {
        var normalizedQuery = NormalizeQuery(query);
        if (normalizedQuery.Length == 0)
        {
            return 0;
        }

        if (field == normalizedQuery)
        {
            return 100;
        }

        if (field.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 75;
        }

        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return queryWords.All(word => field.Contains(word, StringComparison.Ordinal))
            ? 50 + queryWords.Length
            : 0;
    }

    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeQuery(string value)
    {
        return Normalize(RemoveFeatureCredits(value));
    }

    private static string RemoveFeatureCredits(string value)
    {
        var withoutBracketedCredits = Regex.Replace(
            value,
            @"[\(\[\{]\s*(?:feat(?:uring)?\.?|ft\.)\s+[^)\]\}]*[\)\]\}]",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return Regex.Replace(
            withoutBracketedCredits,
            @"(?:^|[\s\-–—])(?:feat(?:uring)?\.?|ft\.)\s+.+$",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CreateStableId(string relativePath)
    {
        var normalizedPath = relativePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string? EmptyToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
