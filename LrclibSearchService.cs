using System.Text.Json;

namespace Lyrictified.Server;

public sealed class LrclibSearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly LrclibCacheStore _cacheStore;
    private readonly LyricsIndex _index;
    private readonly ILogger<LrclibSearchService> _logger;

    public LrclibSearchService(LrclibCacheStore cacheStore, LyricsIndex index, ILogger<LrclibSearchService> logger)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Lyrictified-Server/1.0");
        _cacheStore = cacheStore;
        _index = index;
        _logger = logger;
    }

    public void TriggerBackgroundSearch(SearchRequest request)
    {
        var query = BuildSearchQuery(request);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        _ = Task.Run(async () => await SearchAndCacheAsync(query, request));
    }

    private static string BuildSearchQuery(SearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Song) && !string.IsNullOrWhiteSpace(request.Artist))
        {
            return $"{request.Artist} {request.Song}";
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            return request.Query;
        }

        if (!string.IsNullOrWhiteSpace(request.Song))
        {
            return request.Song;
        }

        return "";
    }

    private async Task SearchAndCacheAsync(string query, SearchRequest originalRequest)
    {
        try
        {
            _logger.LogInformation("Starting LRCLIB background search for query: {Query}", query);

            var title = originalRequest.Song ?? originalRequest.Query ?? "";
            var artist = originalRequest.Artist ?? "";

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
            {
                if (_cacheStore.IsAlreadyCachedOrIndexed(title, artist, _index))
                {
                    _logger.LogInformation("LRCLIB search skipped for {Artist} - {Title}: already cached or indexed", artist, title);
                    return;
                }
            }

            var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LRCLIB search failed with status {StatusCode} for query: {Query}", response.StatusCode, query);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var tracks = JsonSerializer.Deserialize<List<LrclibApiTrack>>(json, JsonOptions);

            if (tracks is null || tracks.Count == 0)
            {
                _logger.LogInformation("No LRCLIB results for query: {Query}", query);
                return;
            }

            var bestTrack = tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.SyncedLyrics));
            if (bestTrack is null)
            {
                _logger.LogInformation("LRCLIB results found but none have synced lyrics for query: {Query}", query);
                return;
            }

            var album = bestTrack.AlbumName ?? "Singles";

            if (_cacheStore.IsAlreadyCachedOrIndexed(bestTrack.Name, bestTrack.ArtistName, _index))
            {
                _logger.LogInformation("LRCLIB search skipped for {Artist} - {Title}: already cached or indexed after search", bestTrack.ArtistName, bestTrack.Name);
                return;
            }

            var cached = _cacheStore.Add(
                bestTrack.Name,
                bestTrack.ArtistName,
                album,
                bestTrack.Duration,
                "lrc",
                bestTrack.SyncedLyrics!,
                query);

            if (cached is not null)
            {
                _logger.LogInformation("LRCLIB cache successful: {TrackId} ({Artist} - {Title})", cached.Id, cached.Artist, cached.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LRCLIB background search failed for query: {Query}", query);
        }
    }
}
