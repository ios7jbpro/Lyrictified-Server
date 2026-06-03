namespace Lyrictified.Server;

public static class UserPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Lyrictified Lyrics</title>
  <style>
    :root { color-scheme: light; font-family: Segoe UI, system-ui, sans-serif; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #f7f8fb; color: #151922; }
    header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 18px 24px; background: #ffffff; border-bottom: 1px solid #d9dde5; }
    main { max-width: 980px; margin: 0 auto; padding: 28px 24px 44px; }
    button, input, select { font: inherit; }
    button, .button { display: inline-flex; align-items: center; justify-content: center; gap: 8px; min-height: 40px; border: 1px solid #1e5eff; background: #1e5eff; color: #fff; border-radius: 6px; padding: 8px 13px; cursor: pointer; text-decoration: none; font-weight: 600; }
    button.secondary, .button.secondary { background: #fff; color: #1e5eff; }
    input, select { min-height: 40px; width: 100%; border: 1px solid #c4cad6; border-radius: 6px; padding: 8px 10px; background: #fff; color: #151922; }
    label { display: grid; gap: 6px; color: #344054; font-size: 13px; font-weight: 600; }
    h1 { margin: 0; font-size: 22px; letter-spacing: 0; }
    h2 { margin: 0 0 8px; font-size: 18px; letter-spacing: 0; }
    .brand { display: inline-flex; align-items: center; gap: 10px; min-width: 0; }
    .brand img { width: 36px; height: 36px; object-fit: contain; }
    .nav { display: flex; flex-wrap: wrap; gap: 10px; }
    .panel { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 18px; }
    .search-grid { display: grid; grid-template-columns: 1fr 160px; gap: 12px; align-items: end; }
    .advanced { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin-top: 12px; }
    .hint { margin: 10px 0 0; color: #667085; font-size: 13px; }
    .status { min-height: 22px; margin: 16px 0 10px; color: #667085; }
    .results { display: grid; gap: 12px; }
    .result { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 14px; display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; }
    .title { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; font-weight: 700; }
    .artist { color: #344054; font-weight: 500; }
    .meta { margin-top: 7px; color: #667085; font-size: 13px; overflow-wrap: anywhere; }
    .badge { display: inline-flex; align-items: center; border: 1px solid #c4cad6; border-radius: 4px; padding: 2px 7px; color: #344054; background: #f8fafc; font-size: 12px; font-weight: 700; text-transform: uppercase; }
    .empty { color: #667085; padding: 16px 0; }
    @media (max-width: 720px) {
      header { align-items: flex-start; flex-direction: column; padding: 16px; }
      main { padding: 18px 16px 32px; }
      .search-grid, .advanced, .result { grid-template-columns: 1fr; }
      button, .button { width: 100%; }
    }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <img src="/assets/logo" alt="" width="36" height="36">
      <h1>Lyrictified Lyrics</h1>
    </div>
    <nav class="nav">
      <a class="button" href="/submit">Submit your own!</a>
      <a class="button secondary" href="/admin">Log in as admin</a>
    </nav>
  </header>
  <main>
    <section class="panel">
      <form id="search-form">
        <div class="search-grid">
          <label>
            Search
            <input id="q" name="q" autocomplete="off" placeholder="Song title, artist, or lyric tag">
          </label>
          <button type="submit">Search lyrics</button>
        </div>
        <div class="advanced">
          <label>
            Song
            <input id="song" name="song" autocomplete="off" placeholder="Optional title">
          </label>
          <label>
            Artist
            <input id="artist" name="artist" autocomplete="off" placeholder="With song or search">
          </label>
          <label>
            Album
            <input id="album" name="album" autocomplete="off" placeholder="Requires song">
          </label>
        </div>
        <p class="hint">Use the main search for broad matches, or fill in song details when you want a stricter result.</p>
      </form>
    </section>
    <div id="status" class="status"></div>
    <section id="results" class="results" aria-live="polite"></section>
  </main>
  <script>
    const form = document.querySelector("#search-form");
    const results = document.querySelector("#results");
    const status = document.querySelector("#status");

    form.addEventListener("submit", async event => {
      event.preventDefault();
      await search();
    });

    async function search() {
      const fields = Object.fromEntries(new FormData(form).entries());
      const params = new URLSearchParams();
      for (const [key, value] of Object.entries(fields)) {
        const trimmed = String(value).trim();
        if (trimmed.length > 0) params.set(key, trimmed);
      }

      results.innerHTML = "";
      if (params.size === 0) {
        status.textContent = "Enter a search term or song details.";
        return;
      }

      status.textContent = "Searching...";
      try {
        const response = await fetch(`/search?${params.toString()}`);
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.error || "Search failed.");
        }

        renderResults(data.results || []);
      } catch (error) {
        status.textContent = error.message;
      }
    }

    function renderResults(items) {
      if (items.length === 0) {
        status.textContent = "No lyrics found.";
        results.innerHTML = `<div class="empty">Try a different title, artist, or tag.</div>`;
        return;
      }

      status.textContent = `${items.length} result${items.length === 1 ? "" : "s"} found.`;
      results.innerHTML = "";

      for (const item of items) {
        const article = document.createElement("article");
        article.className = "result";
        article.innerHTML = `
          <div>
            <div class="title">
              <span>${escapeHtml(item.title || "Untitled")}</span>
              <span class="artist">${escapeHtml(item.artist || "Unknown artist")}</span>
              <span class="badge">${escapeHtml(item.format || "lrc")}</span>
            </div>
            <div class="meta">${escapeHtml(item.album || "Singles")} · ${escapeHtml(item.relativePath || "")}</div>
          </div>
          <a class="button" href="/lyrics/${encodeURIComponent(item.id)}/raw" download>Download</a>`;
        results.appendChild(article);
      }
    }

    function escapeHtml(value) {
      return String(value).replace(/[&<>"']/g, char => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
      }[char]));
    }
  </script>
</body>
</html>
""";
}
