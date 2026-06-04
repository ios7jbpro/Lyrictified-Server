# Lyrictified Server

A local Windows-first lyrics server for `.lrc` and `.elrc` files.

The first version is intentionally small:

- Reads a chosen port from `appsettings.json`
- Scans the configured lyrics folder into an in-memory index
- Stores admin metadata in `data/catalog.json`
- Searches matching `.lrc` and `.elrc` files by title, artist, path, and tags
- Serves the raw lyric file over a local HTTP API
- Provides a public lyrics search and download page
- Accepts public lyric submissions for admin approval
- Provides a password-protected admin page
- Logs each request, response status, duration, and API response summary to the console

## Run

```powershell
dotnet run
```

The default server URL is:

```text
http://127.0.0.1:32145
```

Open that URL in a browser to search and download lyrics. Use the "Log in as admin" button, or open `/admin`, to manage indexed metadata.

## Configure

Edit `appsettings.json`:

```json
{
  "Lyrictified": {
    "Port": 32145,
    "BindAddress": "127.0.0.1",
    "LyricsDirectory": "lyrics",
    "CatalogPath": "data/catalog.json",
    "PendingSubmissionsPath": "data/pending-submissions.json",
    "AdminPasswordHash": ""
  }
}
```

Generate an admin password hash before using the admin page:

```powershell
dotnet run -- --hash-admin-password
```

Paste the printed value into `AdminPasswordHash`. Password hashes are versioned, salted, and verified with PBKDF2-SHA256. If an older config still uses `AdminPassword`, the server accepts it only as a legacy fallback and logs a warning so you can replace it.

Admin changes are saved to the configured `CatalogPath`, usually:

```text
data/catalog.json
```

Pending public lyric submissions and their two-hour submitter rate-limit records are saved to:

```text
data/pending-submissions.json
```

Keep this file when updating, moving, or republishing the server. The server also writes a safety backup next to it:

```text
data/catalog.json.bak
```

On startup, the server logs the exact catalog path it loaded. If admin changes do not survive restart, check that log line and make sure you are not editing/running a different folder.

`BindAddress` controls where the server listens:

```json
"BindAddress": "127.0.0.1"
```

Use `127.0.0.1` when Cloudflare Tunnel runs on the same machine as Lyrictified. Your tunnel service should point to:

```text
http://127.0.0.1:32145
```

Use `0.0.0.0` only if you intentionally want other machines on your LAN to reach the server directly:

```json
"BindAddress": "0.0.0.0"
```

Then test from another device with the homeserver's IP, not `localhost`:

```text
http://YOUR_HOMESERVER_IP:32145/health
```

`localhost` always means "this same machine." If you type `localhost:32145` on your desktop, it looks for a server running on your desktop, not on the homeserver.

When publishing/copying the app to another machine, make sure `appsettings.json` is next to the executable or DLL you run.

## File Names

The server scans the lyrics folder recursively. These layouts are supported:

```text
Artist - Song Title.lrc
Artist - Song Title.elrc
Artist/Album/Song Title.lrc
Artist/Album/Song Title.elrc
Artist/Album/Song Title - Artist.lrc
Artist/Album/Artist - Song Title.lrc
```

For nested files like `Artist/Album/Song Title.lrc`, the server infers:

```text
artist = Artist
album = Album
title = Song Title
```

Files that do not match those patterns still work, but missing fields may need to be filled in from the admin panel.

## Use it on Lyrictified App

Doing this is very easy! Under `Services/LocalLyricsService.cs`, find this part:

```
    public LocalLyricsService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://lyrictifiedserve.ios7.xyz/"),
            Timeout = TimeSpan.FromSeconds(3)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Lyrictified/0.1");
    }
```

Change `https://lyrictifiedserve.ios7.xyz/` to your preferred host once you got Lyrictified-Server running!

## API

Base URL:

```text
http://127.0.0.1:32145
```

Health check:

```http
GET /health
```

Search:

```http
GET /search?q=artist%20song
GET /search?song=example&artist=artist
GET /search?song=example&album=album
GET /search?song=example&artist=artist&album=album
GET /search?song=example
```

Album search is only valid when `song` is also provided. `/search?album=album` returns `400 Bad Request`.
Artist search is only valid when `song` or `q` is also provided. `/search?artist=artist` returns `400 Bad Request`.

When `song`, `artist`, or `album` are provided as separate fields, each provided field must match. For example, `/search?song=Blue&artist=YOASOBI` will not return another YOASOBI song just because the artist matches.
For broad `q` searches, the query must match the title or tags. `/search?q=YOASOBI` will not return every YOASOBI file by artist alone.

The console prints request logs while the server is running. Raw lyric file responses and admin pages are not dumped into the log body, but their path, status code, and timing are still logged.

Get raw lyrics:

```http
GET /lyrics/{id}/raw
```

## Fetching a Lyrics File

Fetching lyrics is a two-step process:

1. Search for a matching lyric file.
2. Use the returned `id` to fetch the raw `.lrc` or `.elrc` file.

### 1. Search

You can search with one general query:

```http
GET http://127.0.0.1:32145/search?q=YOASOBI%20Yoru%20ni%20Kakeru
```

Or with more specific fields:

```http
GET http://127.0.0.1:32145/search?song=Yoru%20ni%20Kakeru&artist=YOASOBI
GET http://127.0.0.1:32145/search?song=Yoru%20ni%20Kakeru&artist=YOASOBI&album=THE%20BOOK
```

You can also provide only one field:

```http
GET http://127.0.0.1:32145/search?song=%E5%A4%9C%E3%81%AB%E9%A7%86%E3%81%91%E3%82%8B
GET http://127.0.0.1:32145/search?artist=YOASOBI
```

Spaces and non-English text should be URL encoded by the client. Browsers and most HTTP libraries usually do this for you when you pass query parameters normally.

Example response:

```json
{
  "results": [
    {
      "id": "79989c9a3b71f80b",
      "title": "Yoru ni Kakeru",
      "artist": "YOASOBI",
      "album": "THE BOOK",
      "format": "lrc",
      "relativePath": "Yoru ni Kakeru - YOASOBI.lrc",
      "rating": 0,
      "tags": [
        "The Night",
        "\u591c\u306b\u99c6\u3051\u308b"
      ],
      "score": 600
    }
  ]
}
```

Pick the first result if you trust the server ranking, or inspect the returned `title`, `artist`, `album`, `format`, `rating`, and `tags` if your client wants to choose manually.

### 2. Fetch the Raw File

Use the `id` from the search result:

```http
GET http://127.0.0.1:32145/lyrics/79989c9a3b71f80b/raw
```

The response body is the actual lyric file content:

```text
[00:01.00]Example lyric line
[00:04.00]Another lyric line
```

The server returns one of these content types:

```text
application/vnd.lyrictified.lrc+text
application/vnd.lyrictified.elrc+text
```

### PowerShell Example

```powershell
$search = Invoke-RestMethod "http://127.0.0.1:32145/search?song=Yoru%20ni%20Kakeru&artist=YOASOBI"
$best = $search.results[0]
$lyrics = Invoke-RestMethod "http://127.0.0.1:32145/lyrics/$($best.id)/raw"
$lyrics
```

### JavaScript Example

```javascript
const baseUrl = "http://127.0.0.1:32145";

const searchParams = new URLSearchParams({
  song: "Yoru ni Kakeru",
  artist: "YOASOBI"
});

const searchResponse = await fetch(`${baseUrl}/search?${searchParams}`);
const searchData = await searchResponse.json();

if (searchData.results.length === 0) {
  throw new Error("No lyrics found.");
}

const best = searchData.results[0];
const lyricsResponse = await fetch(`${baseUrl}/lyrics/${best.id}/raw`);
const lyricsText = await lyricsResponse.text();

console.log(lyricsText);
```

### Error Cases

Missing search parameters:

```http
GET /search
```

Returns:

```http
400 Bad Request
```

Album without song:

```http
GET /search?album=THE%20BOOK
```

Returns:

```http
400 Bad Request
```

Unknown lyric id:

```http
GET /lyrics/not-a-real-id/raw
```

Returns:

```http
404 Not Found
```

Admin UI:

```http
GET /admin
```

Public UI:

```http
GET /
GET /user
GET /submit
```

The public UI lets regular users search lyrics and download matching `.lrc`, `.elrc`, or `.ttml` files. It only calls the read-only `/search` and `/lyrics/{id}/raw` endpoints.
Users can also open `/submit`, or use the "Submit your own!" button, to submit lyrics for review by pasting text or uploading a file. Uploaded files must match the selected lyrics type: `.lrc` for regular LRC, `.elrc` for enhanced LRC, and `.ttml` for TTML. A normal submitter can submit once every 2 hours, tracked by their forwarded IP address when present and otherwise their direct remote IP. Logged-in admins can submit from the same page without the public rate limit, and their submissions are auto-approved immediately.

The admin UI lets you refresh the index, edit display title/artist/album, add tags, and set a rating from `0` to `100`. Rating boosts the whole file. Admin API routes under `/admin/api/*` require the admin login cookie, so regular users cannot change catalog metadata through the public page.

Use the admin page's "Pending requests" button, or open `/admin/requests`, to review submitted lyrics. Approving a request writes it into the lyrics folder and refreshes the index. Album submissions are saved as:

```text
Artist/Album/Song Title.ext
```

Submissions without an album are saved as:

```text
Artist/Song Title - Artist.ext
```

Tags can also have their own scores:

```text
夜に駆ける | 90, Yoru ni Kakeru | 80, Into the Night | 10
```

Use this when two lyric files are both related to the same song but should win for different searches. For example, the Japanese lyrics file can give the Japanese title a high tag score, while the English lyrics file can give the English title a high tag score. Then `/search?song=夜に駆ける` prefers the Japanese file and `/search?song=Into%20the%20Night` prefers the English file.

Admin search rules:

- `Exact`: rejects the file if the request contains words outside that file's title, artist, or tags.
- `Ignore`: enables wildcard patterns that can exclude or require requests.
- `Reverse`: changes ignore patterns from "exclude matching requests" to "only include matching requests".

Pattern examples:

```text
*blue*
*remix*, *live*
```

Without `Reverse`, `*blue*` means "do not return this file when the request contains blue". With `Reverse`, `*blue*` means "only return this file when the request contains blue".

## Example

Put this in `lyrics/Example Artist - Example Song.lrc`:

```text
[00:01.00]Hello from Lyrictified
[00:04.00]This is the first lyric file
```

Then run:

```powershell
dotnet run
```

Search:

```powershell
Invoke-RestMethod "http://127.0.0.1:32145/search?q=example"
```
