using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    CatalogPath = Path.GetFullPath(settings.CatalogPath, builder.Environment.ContentRootPath)
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
VerifyCatalogWriteAccess(settings.CatalogPath);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<LyricsIndex>();
builder.WebHost.UseUrls($"http://{settings.BindAddress}:{settings.Port}");

var app = builder.Build();

app.Services.GetRequiredService<LyricsIndex>().Refresh();

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

    var contentType = file.Format == "elrc"
        ? "application/vnd.lyrictified.elrc+text"
        : "application/vnd.lyrictified.lrc+text";

    return Results.File(file.AbsolutePath, contentType, Path.GetFileName(file.AbsolutePath));
});

app.MapGet("/admin", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminPage.Html, "text/html; charset=utf-8");
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

static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static bool ShouldCaptureResponseBody(PathString path)
{
    return !path.StartsWithSegments("/lyrics", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase);
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
