using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lyrictified.Server;

public sealed class LrclibCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lock _lock = new();
    private readonly LyrictifiedSettings _settings;
    private readonly ILogger<LrclibCacheStore> _logger;
    private LrclibCacheFile _file;

    public LrclibCacheStore(LyrictifiedSettings settings, ILogger<LrclibCacheStore> logger)
    {
        _settings = settings;
        _logger = logger;
        _file = Load();
    }

    public IReadOnlyList<LrclibCachedTrack> All
    {
        get
        {
            lock (_lock)
            {
                return _file.Tracks
                    .OrderByDescending(track => track.CachedAt)
                    .ToArray();
            }
        }
    }

    public bool IsAlreadyCachedOrIndexed(string title, string artist, LyricsIndex index)
    {
        var normalizedTitle = LyricsIndex.Normalize(title);
        var normalizedArtist = LyricsIndex.Normalize(artist);

        lock (_lock)
        {
            if (_file.Tracks.Any(track =>
                LyricsIndex.Normalize(track.Title) == normalizedTitle &&
                LyricsIndex.Normalize(track.Artist) == normalizedArtist))
            {
                return true;
            }
        }

        var searchRequest = new SearchRequest(null, title, artist, null, 20);
        var results = index.Search(searchRequest);
        return results.Any(result =>
            LyricsIndex.Normalize(result.Title) == normalizedTitle &&
            LyricsIndex.Normalize(result.Artist) == normalizedArtist);
    }

    public LrclibCachedTrack? Add(string title, string artist, string album, double? duration, string format, string lyrics, string searchQuery)
    {
        var safeArtist = SafePathPart(artist);
        var safeAlbum = SafePathPart(album);
        var safeTitle = SafePathPart(title);

        string relativePath;
        if (safeAlbum.Length > 0)
        {
            relativePath = Path.Combine(safeArtist, safeAlbum, $"{safeTitle}.{format}").Replace('\\', '/');
        }
        else
        {
            relativePath = Path.Combine(safeArtist, $"{safeTitle} - {safeArtist}.{format}").Replace('\\', '/');
        }

        var absolutePath = Path.GetFullPath(Path.Combine(_settings.LrclibCacheDirectory, relativePath));
        var root = Path.GetFullPath(_settings.LrclibCacheDirectory);
        if (!absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved cache path escaped the cache directory.");
        }

        lock (_lock)
        {
            var existing = _file.Tracks.FirstOrDefault(track =>
                track.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, lyrics, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var track = new LrclibCachedTrack(
                Id: CreateId($"{relativePath}:{DateTimeOffset.UtcNow:O}"),
                Title: title,
                Artist: artist,
                Album: album,
                Duration: duration,
                Format: format,
                RelativePath: relativePath,
                AbsolutePath: absolutePath,
                CachedAt: DateTimeOffset.UtcNow,
                SearchQuery: searchQuery);

            _file.Tracks.Add(track);
            Save();
            _logger.LogInformation("Cached LRCLIB track {TrackId} ({Artist} - {Title}) to {RelativePath}", track.Id, artist, title, relativePath);
            return track;
        }
    }

    public LrclibCachedTrack? Approve(string id, LyricsIndex index)
    {
        lock (_lock)
        {
            var track = _file.Tracks.FirstOrDefault(track => track.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (track is null)
            {
                return null;
            }

            var targetPath = Path.GetFullPath(Path.Combine(_settings.LyricsDirectory, track.RelativePath));
            var root = Path.GetFullPath(_settings.LyricsDirectory);
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved approval path escaped the lyrics directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(track.AbsolutePath, targetPath, overwrite: true);

            _file.Tracks.Remove(track);
            Save();

            try
            {
                if (File.Exists(track.AbsolutePath))
                {
                    File.Delete(track.AbsolutePath);
                }

                var dir = Path.GetDirectoryName(track.AbsolutePath);
                while (dir is not null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up cached file {AbsolutePath} after approval", track.AbsolutePath);
            }

            _logger.LogInformation("Approved LRCLIB cache {TrackId} as {RelativePath}", track.Id, track.RelativePath);
            return track;
        }
    }

    public LrclibCachedTrack? Reject(string id)
    {
        lock (_lock)
        {
            var track = _file.Tracks.FirstOrDefault(track => track.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (track is null)
            {
                return null;
            }

            _file.Tracks.Remove(track);
            Save();

            try
            {
                if (File.Exists(track.AbsolutePath))
                {
                    File.Delete(track.AbsolutePath);
                }

                var root = Path.GetFullPath(_settings.LrclibCacheDirectory);
                var dir = Path.GetDirectoryName(track.AbsolutePath);
                while (dir is not null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up cached file {AbsolutePath} after rejection", track.AbsolutePath);
            }

            _logger.LogInformation("Rejected LRCLIB cache {TrackId}", track.Id);
            return track;
        }
    }

    public string? GetLyrics(string id)
    {
        lock (_lock)
        {
            var track = _file.Tracks.FirstOrDefault(track => track.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (track is null || !File.Exists(track.AbsolutePath))
            {
                return null;
            }

            return File.ReadAllText(track.AbsolutePath);
        }
    }

    public int CleanExpired(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge);
        var expired = new List<LrclibCachedTrack>();

        lock (_lock)
        {
            expired.AddRange(_file.Tracks.Where(track => track.CachedAt < cutoff));
            if (expired.Count == 0)
            {
                return 0;
            }

            foreach (var track in expired)
            {
                _file.Tracks.Remove(track);
            }

            Save();
        }

        var root = Path.GetFullPath(_settings.LrclibCacheDirectory);
        foreach (var track in expired)
        {
            try
            {
                if (File.Exists(track.AbsolutePath))
                {
                    File.Delete(track.AbsolutePath);
                }

                var dir = Path.GetDirectoryName(track.AbsolutePath);
                while (dir is not null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up expired cached file {AbsolutePath}", track.AbsolutePath);
            }
        }

        _logger.LogInformation("Auto-cleaned {Count} expired LRCLIB cached track(s) older than {MaxAge}", expired.Count, maxAge);
        return expired.Count;
    }

    private LrclibCacheFile Load()
    {
        if (!File.Exists(_settings.LrclibCachePath))
        {
            return new LrclibCacheFile();
        }

        var json = File.ReadAllText(_settings.LrclibCachePath);
        return JsonSerializer.Deserialize<LrclibCacheFile>(json, JsonOptions) ?? new LrclibCacheFile();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settings.LrclibCachePath)!);
        var json = JsonSerializer.Serialize(_file, JsonOptions);
        var tempPath = $"{_settings.LrclibCachePath}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settings.LrclibCachePath, overwrite: true);
    }

    private static string SafePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    private static string CreateId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
