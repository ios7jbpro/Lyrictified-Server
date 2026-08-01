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
    private readonly Dictionary<string, ArtistRenamePlan> _pendingRenames = new(StringComparer.Ordinal);

    private static readonly TimeSpan PendingRenameLifetime = TimeSpan.FromMinutes(15);

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

    public MetadataUpdateOutcome? UpdateMetadata(string id, LyricMetadataUpdate update)
    {
        string? newId;
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

            var newArtist = EmptyToNull(update.Artist);
            var moves = newArtist is not null ? PlanArtistRename(current, newArtist, update) : [];
            var conflicts = moves.Where(move => move.ConflictSame).ToArray();

            if (conflicts.Length > 0)
            {
                var plan = CreatePendingRenamePlan(id, update, moves);
                return new MetadataUpdateConflict(new ArtistRenameConflictResponse(
                    plan.ConflictKey,
                    conflicts.Select(conflict => new ArtistRenameConflict(
                        conflict.ConflictId,
                        conflict.SourceRelativePath,
                        conflict.TargetRelativePath)).ToArray()));
            }

            ApplyMetadata(entry, update);

            string? editedTarget = null;
            if (moves.Count > 0)
            {
                var actualTargets = ExecuteArtistRename(moves, null);
                RekeyCatalogEntries(actualTargets);
                actualTargets.TryGetValue(current.RelativePath.Replace('\\', '/'), out editedTarget);
            }

            newId = editedTarget is not null ? CreateStableId(editedTarget) : id;
            SaveCatalog();
        }

        Refresh();
        return new MetadataUpdateSuccess(Find(newId)!);
    }

    public MetadataUpdateOutcome? ResolveArtistRename(string conflictKey, IReadOnlyDictionary<string, string> decisions)
    {
        string? newId;
        lock (_lock)
        {
            PruneExpiredPlans();
            if (!_pendingRenames.TryGetValue(conflictKey, out var plan))
            {
                return null;
            }

            _pendingRenames.Remove(conflictKey);

            var current = _lyrics.FirstOrDefault(file => file.Id.Equals(plan.LyricId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            var entry = _catalog.Entries.FirstOrDefault(entry => entry.Id.Equals(plan.LyricId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new CatalogEntry { Id = plan.LyricId };
                _catalog.Entries.Add(entry);
            }

            ApplyMetadata(entry, plan.Update);

            var actualTargets = ExecuteArtistRename(plan.Moves, decisions);
            RekeyCatalogEntries(actualTargets);

            actualTargets.TryGetValue(current.RelativePath.Replace('\\', '/'), out var editedTarget);
            newId = editedTarget is not null ? CreateStableId(editedTarget) : plan.LyricId;
            SaveCatalog();
            _logger.LogInformation("Resolved artist rename for {LyricId} (conflict key {ConflictKey})", plan.LyricId, plan.ConflictKey);
        }

        Refresh();
        return new MetadataUpdateSuccess(Find(newId)!);
    }

    private IReadOnlyList<RenameMove> PlanArtistRename(LyricFile current, string newArtist, LyricMetadataUpdate update)
    {
        var safeNewArtist = SafePathPart(newArtist);
        var currentRelative = current.RelativePath.Replace('\\', '/');
        var pathParts = currentRelative.Split('/');
        var oldFolderName = pathParts[0];

        if (oldFolderName.Equals(safeNewArtist, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var moves = new List<RenameMove>();
        if (pathParts.Length >= 2)
        {
            foreach (var file in _lyrics)
            {
                var relative = file.RelativePath.Replace('\\', '/');
                if (!relative.StartsWith(oldFolderName + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var subPath = relative[(oldFolderName.Length + 1)..];
                moves.Add(CreateMove(relative, Path.Combine(safeNewArtist, subPath).Replace('\\', '/')));
            }
        }
        else
        {
            moves.Add(CreateMove(currentRelative, ComputeTopLevelTargetRelativePath(current, safeNewArtist, update)));
        }

        return moves;
    }

    private RenameMove CreateMove(string sourceRelative, string targetRelative)
    {
        var root = Path.GetFullPath(_settings.LyricsDirectory);
        var sourceAbsolute = Path.GetFullPath(Path.Combine(root, sourceRelative));
        var targetAbsolute = Path.GetFullPath(Path.Combine(root, targetRelative));

        if (!sourceAbsolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !targetAbsolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artist rename path escaped the lyrics directory.");
        }

        if (!sourceAbsolute.Equals(targetAbsolute, StringComparison.OrdinalIgnoreCase)
            && File.Exists(targetAbsolute))
        {
            var sameContent = HasSameLyricsContent(sourceAbsolute, targetAbsolute);
            return new RenameMove
            {
                SourceRelativePath = sourceRelative,
                TargetRelativePath = targetRelative,
                ConflictSame = sameContent,
                ConflictDifferent = !sameContent,
                ConflictId = sameContent ? CreateStableId(sourceRelative) : ""
            };
        }

        return new RenameMove
        {
            SourceRelativePath = sourceRelative,
            TargetRelativePath = targetRelative
        };
    }

    private static string ComputeTopLevelTargetRelativePath(LyricFile current, string safeArtist, LyricMetadataUpdate update)
    {
        var title = EmptyToNull(update.Title) ?? EmptyToNull(current.Title);
        var album = EmptyToNull(update.Album) ?? EmptyToNull(current.Album);
        var safeTitle = SafePathPart(title);

        if (!string.IsNullOrEmpty(album))
        {
            return Path.Combine(safeArtist, SafePathPart(album), $"{safeTitle}.{current.Format}").Replace('\\', '/');
        }

        return Path.Combine(safeArtist, $"{safeTitle} - {safeArtist}.{current.Format}").Replace('\\', '/');
    }

    private IReadOnlyDictionary<string, string> ExecuteArtistRename(
        IReadOnlyList<RenameMove> moves,
        IReadOnlyDictionary<string, string>? decisions)
    {
        var root = Path.GetFullPath(_settings.LyricsDirectory);
        var actualTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (TryExecuteFolderRename(moves, root, actualTargets))
        {
            return actualTargets;
        }

        foreach (var move in moves)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(root, move.SourceRelativePath));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(root, move.TargetRelativePath));
            if (move.ConflictSame)
            {
                var action = decisions?.GetValueOrDefault(move.ConflictId) ?? "dedupe";
                if (!action.Equals("overwrite", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = GetAvailableFilePath(targetPath);
                }
            }
            else if (move.ConflictDifferent)
            {
                targetPath = GetAvailableFilePath(targetPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(sourcePath, targetPath, overwrite: true);

            var sourceOffset = LyricOffsetHelper.GetOffsetFilePath(sourcePath);
            if (File.Exists(sourceOffset))
            {
                var targetOffset = LyricOffsetHelper.GetOffsetFilePath(targetPath);
                File.Move(sourceOffset, targetOffset, overwrite: true);
            }

            var actualRelative = Path.GetRelativePath(root, targetPath).Replace('\\', '/');
            actualTargets[move.SourceRelativePath] = actualRelative;
            _logger.LogInformation("Moved lyric file {Source} to {Target}", move.SourceRelativePath, actualRelative);
        }

        foreach (var sourceDirectory in moves
            .Select(move => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, move.SourceRelativePath)))!)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RemoveEmptyDirectories(sourceDirectory, root);
        }

        return actualTargets;
    }

    private bool TryExecuteFolderRename(
        IReadOnlyList<RenameMove> moves,
        string root,
        IDictionary<string, string> actualTargets)
    {
        if (moves.Count == 0 || moves.Any(move => move.ConflictSame || move.ConflictDifferent))
        {
            return false;
        }

        var oldFolder = GetFirstPathSegment(moves[0].SourceRelativePath);
        var newFolder = GetFirstPathSegment(moves[0].TargetRelativePath);
        if (oldFolder.Length == 0 || newFolder.Length == 0)
        {
            return false;
        }

        foreach (var move in moves)
        {
            var source = move.SourceRelativePath.Replace('\\', '/');
            var target = move.TargetRelativePath.Replace('\\', '/');
            if (!source.StartsWith(oldFolder + "/", StringComparison.OrdinalIgnoreCase)
                || !target.StartsWith(newFolder + "/", StringComparison.OrdinalIgnoreCase)
                || !source[oldFolder.Length..].Equals(target[newFolder.Length..], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var oldDir = Path.GetFullPath(Path.Combine(root, oldFolder));
        var newDir = Path.GetFullPath(Path.Combine(root, newFolder));
        if (string.Equals(oldDir, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(oldDir)
            || Directory.Exists(newDir))
        {
            return false;
        }

        Directory.Move(oldDir, newDir);
        _logger.LogInformation("Renamed artist directory {Old} to {New}", oldDir, newDir);

        foreach (var move in moves)
        {
            actualTargets[move.SourceRelativePath] = move.TargetRelativePath;
        }

        return true;
    }

    private void RekeyCatalogEntries(IReadOnlyDictionary<string, string> actualTargets)
    {
        foreach (var pair in actualTargets)
        {
            var oldId = CreateStableId(pair.Key);
            var newId = CreateStableId(pair.Value);
            if (oldId.Equals(newId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entry = _catalog.Entries.FirstOrDefault(entry => entry.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            _catalog.Entries.RemoveAll(existing =>
                existing.Id.Equals(newId, StringComparison.OrdinalIgnoreCase)
                && !ReferenceEquals(existing, entry));
            entry.Id = newId;
        }
    }

    private ArtistRenamePlan CreatePendingRenamePlan(string lyricId, LyricMetadataUpdate update, IReadOnlyList<RenameMove> moves)
    {
        PruneExpiredPlans();
        var plan = new ArtistRenamePlan(
            ConflictKey: Guid.NewGuid().ToString("N"),
            LyricId: lyricId,
            Update: update,
            Moves: moves,
            CreatedAt: DateTimeOffset.UtcNow);
        _pendingRenames.Add(plan.ConflictKey, plan);
        _logger.LogInformation(
            "Staged artist rename for {LyricId} with {ConflictCount} conflict(s); conflict key {ConflictKey}",
            lyricId,
            moves.Count(move => move.ConflictSame),
            plan.ConflictKey);
        return plan;
    }

    private void PruneExpiredPlans()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(PendingRenameLifetime);
        var expired = _pendingRenames.Where(pair => pair.Value.CreatedAt < cutoff).Select(pair => pair.Key).ToArray();
        foreach (var key in expired)
        {
            _pendingRenames.Remove(key);
        }
    }

    private static void ApplyMetadata(CatalogEntry entry, LyricMetadataUpdate update)
    {
        entry.Title = EmptyToNull(update.Title);
        entry.Artist = EmptyToNull(update.Artist);
        entry.Album = EmptyToNull(update.Album);
        entry.Rating = Math.Clamp(update.Rating, 0, 100);
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
    }

    private static string SafePathPart(string? value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder((value ?? "").Length);
        foreach (var character in value ?? "")
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    private static bool HasSameLyricsContent(string sourcePath, string targetPath)
    {
        var source = File.ReadAllText(sourcePath).ReplaceLineEndings("\n").Trim();
        var target = File.ReadAllText(targetPath).ReplaceLineEndings("\n").Trim();
        return source.Equals(target, StringComparison.Ordinal);
    }

    private static string GetFirstPathSegment(string relativePath)
    {
        var index = relativePath.IndexOf('/');
        return index < 0 ? "" : relativePath[..index];
    }

    private static string GetAvailableFilePath(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find an available target filename for the artist rename.");
    }

    private static void RemoveEmptyDirectories(string directory, string root)
    {
        var normalizedRoot = root.TrimEnd('\\', '/');
        while (directory is not null
            && directory.Length > normalizedRoot.Length
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
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
        if (HasTokenOutsideAllowedText(requestSearchText, file.ExactSearchText))
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
