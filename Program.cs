using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lyrictified.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var settings = builder.Configuration.GetSection("Lyrictified").Get<LyrictifiedSettings>() ?? new();
settings = settings with
{
    LyricsDirectory = Path.GetFullPath(settings.LyricsDirectory, builder.Environment.ContentRootPath),
    CatalogPath = Path.GetFullPath(settings.CatalogPath, builder.Environment.ContentRootPath),
    PendingSubmissionsPath = Path.GetFullPath(settings.PendingSubmissionsPath, builder.Environment.ContentRootPath)
};

if (settings.Port is < 1 or > 65535)
{
    throw new InvalidOperationException("Lyrictified:Port must be between 1 and 65535.");
}

if (string.IsNullOrWhiteSpace(settings.BindAddress))
{
    throw new InvalidOperationException("Lyrictified:BindAddress cannot be empty.");
}

Directory.CreateDirectory(settings.LyricsDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(settings.CatalogPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(settings.PendingSubmissionsPath)!);
VerifyCatalogWriteAccess(settings.CatalogPath);
VerifyCatalogWriteAccess(settings.PendingSubmissionsPath);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<LyricsIndex>();
builder.Services.AddSingleton<PendingSubmissionStore>();
builder.Services.AddHostedService<TrayIconService>();
builder.WebHost.UseUrls($"http://{settings.BindAddress}:{settings.Port}");

var app = builder.Build();

var index = app.Services.GetRequiredService<LyricsIndex>();
index.Refresh();

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Lyrictified.Requests");
    var stopwatch = Stopwatch.StartNew();
    var request = context.Request;
    var originalBody = context.Response.Body;

    logger.LogInformation(
        "Incoming {Method} {Path}{QueryString} from {RemoteIp}",
        request.Method,
        request.Path,
        request.QueryString,
        context.Connection.RemoteIpAddress);

    await using var capturedBody = new MemoryStream();
    if (ShouldCaptureResponseBody(request.Path))
    {
        context.Response.Body = capturedBody;
    }

    try
    {
        await next();
    }
    catch (Exception exception)
    {
        stopwatch.Stop();
        logger.LogError(
            exception,
            "Failed {Method} {Path}{QueryString} after {ElapsedMs}ms",
            request.Method,
            request.Path,
            request.QueryString,
            stopwatch.ElapsedMilliseconds);
        throw;
    }
    finally
    {
        stopwatch.Stop();

        if (context.Response.Body == capturedBody)
        {
            capturedBody.Position = 0;
            await capturedBody.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
        }

        var responseSummary = SummarizeResponse(context.Response, capturedBody);
        var logLevel = context.Response.StatusCode >= 500
            ? LogLevel.Error
            : context.Response.StatusCode >= 400
                ? LogLevel.Warning
                : LogLevel.Information;

        logger.Log(
            logLevel,
            "Completed {Method} {Path}{QueryString} => {StatusCode} in {ElapsedMs}ms {ResponseSummary}",
            request.Method,
            request.Path,
            request.QueryString,
            context.Response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseSummary);
    }
});

app.MapGet("/health", (LyricsIndex index) => Results.Ok(new
{
    status = "ok",
    port = settings.Port,
    bindAddress = settings.BindAddress,
    lyricsDirectory = settings.LyricsDirectory,
    catalogPath = settings.CatalogPath,
    indexedFiles = index.All.Count
}));

app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(UserPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/user", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(UserPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/submit", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(SubmitPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/assets/logo", (IWebHostEnvironment environment) =>
{
    var path = Path.Combine(environment.ContentRootPath, "lyrictified-server.png");
    return File.Exists(path)
        ? Results.File(path, "image/png")
        : Results.NotFound();
});

app.MapGet("/search", (
    LyricsIndex index,
    string? q,
    string? song,
    string? artist,
    string? album,
    int? limit) =>
{
    var request = new SearchRequest(q, song, artist, album, Math.Clamp(limit ?? 20, 1, 100));
    if (!request.HasQuery)
    {
        return Results.BadRequest(new { error = "Provide q, song, artist, album, or a valid combination of them." });
    }

    if (request.HasInvalidAlbumSearch)
    {
        return Results.BadRequest(new { error = "Album search requires song. Use /search?song=...&album=... ." });
    }

    if (request.HasInvalidArtistOnlySearch)
    {
        return Results.BadRequest(new { error = "Artist search requires song or q. Use /search?song=...&artist=... or /search?q=artist%20song ." });
    }

    return Results.Ok(new { results = index.Search(request) });
});

app.MapGet("/lyrics/{id}/raw", (LyricsIndex index, string id) =>
{
    var file = index.Find(id);
    if (file is null)
    {
        return Results.NotFound(new { error = "Lyrics file was not found." });
    }

    var path = file.AbsolutePath;
    if (file.Offset != 0)
    {
        var offsetPath = LyricOffsetHelper.GetOffsetFilePath(path);
        if (!File.Exists(offsetPath))
        {
            LyricOffsetHelper.SyncOffsetFile(path, file.Offset);
        }
        path = offsetPath;
    }

    var contentType = file.Format switch
    {
        "elrc" => "application/vnd.lyrictified.elrc+text",
        "ttml" => "application/vnd.lyrictified.ttml+xml",
        _ => "application/vnd.lyrictified.lrc+text"
    };

    return Results.File(path, contentType, Path.GetFileName(file.AbsolutePath));
});

app.MapGet("/api/submissions/status", (HttpContext context, PendingSubmissionStore submissions) =>
{
    var isAdmin = IsAdmin(context, settings);
    var submitterKey = GetSubmitterKey(context);
    return Results.Ok(new
    {
        isAdmin,
        rateLimitHours = PendingSubmissionStore.RateLimitWindow.TotalHours,
        nextAllowedAt = isAdmin ? null : submissions.NextAllowedAt(submitterKey)
    });
});

app.MapPost("/api/submissions", async (HttpContext context, PendingSubmissionStore submissions) =>
{
    var isAdmin = IsAdmin(context, settings);
    var submitterKey = GetSubmitterKey(context);

    try
    {
        var request = await ReadSubmissionRequest(context);
        var submission = submissions.Submit(request, submitterKey, bypassRateLimit: isAdmin);
        return Results.Ok(new
        {
            id = submission.Id,
            submittedAt = submission.SubmittedAt,
            suggestedRelativePath = submission.SuggestedRelativePath
        });
    }
    catch (SubmissionRateLimitException exception)
    {
        return Results.Json(
            new { error = "You can only submit lyrics once every 2 hours.", nextAllowedAt = exception.NextAllowedAt },
            statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/admin", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/admin/requests", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminRequestsPage.Html, "text/html; charset=utf-8");
});

app.MapPost("/admin/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();

    if (!FixedTimeEquals(password, settings.AdminPassword))
    {
        return Results.Unauthorized();
    }

    context.Response.Cookies.Append(
        "lyrictified_admin",
        CreateAdminToken(settings),
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Expires = DateTimeOffset.UtcNow.AddHours(12)
        });

    return Results.Redirect("/admin");
});

app.MapPost("/admin/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete("lyrictified_admin");
    return Results.Redirect("/admin");
});

app.MapGet("/admin/api/lyrics", (HttpContext context, LyricsIndex index) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { lyrics = index.All });
});

app.MapPost("/admin/api/lyrics/refresh", (HttpContext context, LyricsIndex index) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    index.Refresh();
    return Results.Ok(new { indexedFiles = index.All.Count });
});

app.MapGet("/admin/api/submissions", (HttpContext context, PendingSubmissionStore submissions) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { submissions = submissions.All });
});

app.MapPost("/admin/api/submissions/{id}/approve", (HttpContext context, PendingSubmissionStore submissions, LyricsIndex index, string id) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    var result = submissions.Approve(id);
    if (result is null)
    {
        return Results.NotFound(new { error = "Submission was not found." });
    }

    index.Refresh();
    return Results.Ok(new
    {
        submission = result.Submission,
        relativePath = result.RelativePath,
        indexedFiles = index.All.Count
    });
});

app.MapPost("/admin/api/submissions/{id}/reject", (HttpContext context, PendingSubmissionStore submissions, string id) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    var rejected = submissions.Reject(id);
    return rejected is null
        ? Results.NotFound(new { error = "Submission was not found." })
        : Results.Ok(new { submission = rejected });
});

app.MapPut("/admin/api/lyrics/{id}", (HttpContext context, LyricsIndex index, string id, LyricMetadataUpdate update) =>
{
    if (!IsAdmin(context, settings))
    {
        return Results.Unauthorized();
    }

    var updated = index.UpdateMetadata(id, update);
    return updated is null
        ? Results.NotFound(new { error = "Lyrics file was not found." })
        : Results.Ok(updated);
});

app.Run();

static bool IsAdmin(HttpContext context, LyrictifiedSettings settings)
{
    return context.Request.Cookies.TryGetValue("lyrictified_admin", out var token)
        && FixedTimeEquals(token, CreateAdminToken(settings));
}

static string CreateAdminToken(LyrictifiedSettings settings)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"lyrictified-admin:{settings.AdminPassword}"));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

static string GetSubmitterKey(HttpContext context)
{
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
    var forwardedAddress = forwardedFor
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(forwardedAddress))
    {
        return forwardedAddress;
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static async Task<LyricSubmissionRequest> ReadSubmissionRequest(HttpContext context)
{
    if (context.Request.HasFormContentType)
    {
        var form = await context.Request.ReadFormAsync();
        var format = form["format"].ToString();
        var file = form.Files["lyricsFile"];
        if (file is null || file.Length == 0)
        {
            throw new ArgumentException("Upload a lyrics file.");
        }

        if (!PendingSubmissionStore.IsAllowedFileNameForFormat(file.FileName, format))
        {
            var expectedExtension = PendingSubmissionStore.NormalizeFormat(format);
            throw new ArgumentException($"Uploaded file must use the .{expectedExtension} extension.");
        }

        const long maxUploadBytes = 1_000_000;
        if (file.Length > maxUploadBytes)
        {
            throw new ArgumentException("Lyrics file is too large.");
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lyrics = await reader.ReadToEndAsync();
        return new LyricSubmissionRequest(
            form["title"].ToString(),
            form["artist"].ToString(),
            form["album"].ToString(),
            format,
            lyrics);
    }

    if (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
    {
        var request = await context.Request.ReadFromJsonAsync<LyricSubmissionRequest>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return request ?? throw new ArgumentException("Submission body is required.");
    }

    throw new ArgumentException("Submission must be JSON or multipart form data.");
}

static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static bool ShouldCaptureResponseBody(PathString path)
{
    return !path.StartsWithSegments("/lyrics", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/api/submissions", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/user", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/submit", StringComparison.OrdinalIgnoreCase);
}

static string SummarizeResponse(HttpResponse response, MemoryStream capturedBody)
{
    if (capturedBody.Length == 0)
    {
        return "";
    }

    var contentType = response.ContentType ?? "";
    if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
    {
        return $"body={capturedBody.Length} byte(s)";
    }

    capturedBody.Position = 0;
    using var reader = new StreamReader(capturedBody, Encoding.UTF8, leaveOpen: true);
    var body = reader.ReadToEnd().ReplaceLineEndings(" ");
    capturedBody.Position = 0;

    const int maxBodyLength = 800;
    if (body.Length > maxBodyLength)
    {
        body = $"{body[..maxBodyLength]}...";
    }

    return $"body={body}";
}

static void VerifyCatalogWriteAccess(string catalogPath)
{
    var directory = Path.GetDirectoryName(catalogPath) ?? ".";
    var testPath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");

    try
    {
        File.WriteAllText(testPath, "ok");
        File.Delete(testPath);
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException($"Cannot write to catalog directory '{directory}'. Admin changes cannot be saved.", exception);
    }
}
