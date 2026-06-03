using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lyrictified.Server;

public sealed class PendingSubmissionStore
{
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "lrc",
        "elrc",
        "ttml"
    };

    private readonly Lock _lock = new();
    private readonly LyrictifiedSettings _settings;
    private readonly ILogger<PendingSubmissionStore> _logger;
    private PendingSubmissionsFile _file;

    public PendingSubmissionStore(LyrictifiedSettings settings, ILogger<PendingSubmissionStore> logger)
    {
        _settings = settings;
        _logger = logger;
        _file = Load();
    }

    public IReadOnlyList<PendingLyricSubmission> All
    {
        get
        {
            lock (_lock)
            {
                return _file.Submissions
                    .OrderBy(submission => submission.SubmittedAt)
                    .ToArray();
            }
        }
    }

    public DateTimeOffset? NextAllowedAt(string submitterKey)
    {
        lock (_lock)
        {
            PruneExpiredRateLimits(DateTimeOffset.UtcNow);
            var latest = _file.RateLimits
                .Where(entry => entry.SubmitterKey.Equals(submitterKey, StringComparison.Ordinal))
                .Select(entry => entry.SubmittedAt)
                .DefaultIfEmpty()
                .Max();

            if (latest == default)
            {
                return null;
            }

            var nextAllowedAt = latest.Add(RateLimitWindow);
            return nextAllowedAt > DateTimeOffset.UtcNow ? nextAllowedAt : null;
        }
    }

    public PendingLyricSubmission Submit(LyricSubmissionRequest request, string submitterKey, bool bypassRateLimit)
    {
        var title = CleanRequired(request.Title, "Title");
        var artist = CleanRequired(request.Artist, "Artist");
        var album = CleanOptional(request.Album);
        var format = NormalizeFormat(request.Format);
        var lyrics = CleanLyrics(request.Lyrics);
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            PruneExpiredRateLimits(now);
            if (!bypassRateLimit)
            {
                var nextAllowedAt = NextAllowedAtCore(submitterKey, now);
                if (nextAllowedAt is not null)
                {
                    throw new SubmissionRateLimitException(nextAllowedAt.Value);
                }
            }

            var submission = new PendingLyricSubmission(
                Id: CreateId($"{submitterKey}:{now:O}:{artist}:{album}:{title}:{format}"),
                Title: title,
                Artist: artist,
                Album: album,
                Format: format,
                Lyrics: lyrics,
                SubmitterKey: submitterKey,
                SubmittedAt: now,
                SuggestedRelativePath: CreateRelativePath(artist, album, title, format));

            _file.Submissions.Add(submission);
            if (!bypassRateLimit)
            {
                _file.RateLimits.Add(new SubmissionRateLimitEntry
                {
                    SubmitterKey = submitterKey,
                    SubmittedAt = now
                });
            }

            Save();
            _logger.LogInformation("Stored pending lyric submission {SubmissionId} from {SubmitterKey}", submission.Id, submitterKey);
            return submission;
        }
    }

    public SubmissionApprovalResult? Approve(string id)
    {
        lock (_lock)
        {
            var submission = _file.Submissions.FirstOrDefault(submission => submission.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (submission is null)
            {
                return null;
            }

            var relativePath = CreateRelativePath(submission.Artist, submission.Album, submission.Title, submission.Format);
            var absolutePath = GetAvailableAbsolutePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, submission.Lyrics, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            _file.Submissions.Remove(submission);
            Save();

            var savedRelativePath = Path.GetRelativePath(_settings.LyricsDirectory, absolutePath).Replace('\\', '/');
            _logger.LogInformation("Approved lyric submission {SubmissionId} as {RelativePath}", submission.Id, savedRelativePath);
            return new SubmissionApprovalResult(submission, savedRelativePath);
        }
    }

    public PendingLyricSubmission? Reject(string id)
    {
        lock (_lock)
        {
            var submission = _file.Submissions.FirstOrDefault(submission => submission.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (submission is null)
            {
                return null;
            }

            _file.Submissions.Remove(submission);
            Save();
            _logger.LogInformation("Rejected lyric submission {SubmissionId}", submission.Id);
            return submission;
        }
    }

    private PendingSubmissionsFile Load()
    {
        if (!File.Exists(_settings.PendingSubmissionsPath))
        {
            return new PendingSubmissionsFile();
        }

        var json = File.ReadAllText(_settings.PendingSubmissionsPath);
        return JsonSerializer.Deserialize<PendingSubmissionsFile>(json, JsonOptions) ?? new PendingSubmissionsFile();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settings.PendingSubmissionsPath)!);
        var json = JsonSerializer.Serialize(_file, JsonOptions);
        var tempPath = $"{_settings.PendingSubmissionsPath}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settings.PendingSubmissionsPath, overwrite: true);
    }

    private DateTimeOffset? NextAllowedAtCore(string submitterKey, DateTimeOffset now)
    {
        var latest = _file.RateLimits
            .Where(entry => entry.SubmitterKey.Equals(submitterKey, StringComparison.Ordinal))
            .Select(entry => entry.SubmittedAt)
            .DefaultIfEmpty()
            .Max();

        if (latest == default)
        {
            return null;
        }

        var nextAllowedAt = latest.Add(RateLimitWindow);
        return nextAllowedAt > now ? nextAllowedAt : null;
    }

    private void PruneExpiredRateLimits(DateTimeOffset now)
    {
        _file.RateLimits.RemoveAll(entry => entry.SubmittedAt.Add(RateLimitWindow) <= now);
    }

    private string GetAvailableAbsolutePath(string relativePath)
    {
        var absolutePath = Path.GetFullPath(Path.Combine(_settings.LyricsDirectory, relativePath));
        var root = Path.GetFullPath(_settings.LyricsDirectory);
        if (!absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved submission path escaped the lyrics directory.");
        }

        if (!File.Exists(absolutePath))
        {
            return absolutePath;
        }

        var directory = Path.GetDirectoryName(absolutePath)!;
        var name = Path.GetFileNameWithoutExtension(absolutePath);
        var extension = Path.GetExtension(absolutePath);
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find an available filename for the approved submission.");
    }

    private static string CreateRelativePath(string artist, string album, string title, string format)
    {
        var safeArtist = SafePathPart(artist);
        var safeAlbum = SafePathPart(album);
        var safeTitle = SafePathPart(title);

        if (safeAlbum.Length > 0)
        {
            return Path.Combine(safeArtist, safeAlbum, $"{safeTitle}.{format}").Replace('\\', '/');
        }

        return Path.Combine(safeArtist, $"{safeTitle} - {safeArtist}.{format}").Replace('\\', '/');
    }

    public static bool IsAllowedFileNameForFormat(string fileName, string? format)
    {
        var normalizedFormat = NormalizeFormat(format);
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension.Equals(normalizedFormat, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeFormat(string? format)
    {
        var normalized = CleanRequired(format, "Lyrics type")
            .TrimStart('.')
            .ToLowerInvariant();

        if (normalized == "erlc")
        {
            normalized = "elrc";
        }

        if (!SupportedFormats.Contains(normalized))
        {
            throw new ArgumentException("Lyrics type must be lrc, elrc, or ttml.");
        }

        return normalized;
    }

    private static string CleanLyrics(string? lyrics)
    {
        var cleaned = lyrics?.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new ArgumentException("Lyrics are required.");
        }

        const int maxLyricsLength = 1_000_000;
        if (cleaned.Length > maxLyricsLength)
        {
            throw new ArgumentException("Lyrics are too large.");
        }

        return cleaned + "\n";
    }

    private static string CleanRequired(string? value, string fieldName)
    {
        var cleaned = CleanOptional(value);
        if (cleaned.Length == 0)
        {
            throw new ArgumentException($"{fieldName} is required.");
        }

        return cleaned;
    }

    private static string CleanOptional(string? value)
    {
        return string.Join(' ', (value ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
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

public sealed class SubmissionRateLimitException : Exception
{
    public SubmissionRateLimitException(DateTimeOffset nextAllowedAt)
        : base("Submission rate limit is active.")
    {
        NextAllowedAt = nextAllowedAt;
    }

    public DateTimeOffset NextAllowedAt { get; }
}
