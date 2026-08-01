namespace Lyrictified.Server;

public static class AdminLrclibCachePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Lyrictified LRCLIB Cache</title>
  <style>
    :root { color-scheme: light; font-family: Segoe UI, system-ui, sans-serif; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #f6f7f9; color: #151922; }
    header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 18px 24px; background: #ffffff; border-bottom: 1px solid #d9dde5; }
    h1 { margin: 0; font-size: 20px; }
    main { max-width: 1180px; margin: 0 auto; padding: 24px; }
    button, input { font: inherit; }
    button, .button { display: inline-flex; align-items: center; justify-content: center; min-height: 38px; border: 1px solid #1e5eff; background: #1e5eff; color: #fff; border-radius: 6px; padding: 8px 12px; cursor: pointer; text-decoration: none; font-weight: 600; }
    button.secondary, .button.secondary { background: #fff; color: #1e5eff; }
    button.danger { border-color: #b42318; background: #b42318; }
    input { border: 1px solid #c4cad6; border-radius: 6px; padding: 8px 10px; }
    pre { margin: 0; padding: 12px; max-height: 360px; overflow: auto; background: #111827; color: #f9fafb; border-radius: 6px; white-space: pre-wrap; line-height: 1.45; font-family: Consolas, Cascadia Mono, monospace; }
    .header-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
    .login { max-width: 360px; margin-top: 80px; background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 18px; }
    .login form { display: grid; gap: 10px; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-bottom: 18px; }
    .status { color: #667085; min-height: 20px; }
    .list { display: grid; gap: 14px; }
    .request { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 14px; display: grid; gap: 12px; }
    .request-header { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: start; }
    .title { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; font-weight: 700; }
    .artist { color: #344054; font-weight: 500; }
    .badge { display: inline-flex; align-items: center; border: 1px solid #c4cad6; border-radius: 4px; padding: 2px 7px; color: #344054; background: #f8fafc; font-size: 12px; font-weight: 700; text-transform: uppercase; }
    .meta { margin-top: 7px; color: #667085; font-size: 13px; overflow-wrap: anywhere; }
    .actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
    .empty { color: #667085; padding: 14px 0; }
    .preview { display: none; }
    .preview.visible { display: block; }
    @media (max-width: 760px) {
      header { align-items: flex-start; flex-direction: column; padding: 16px; }
      main { padding: 18px 16px 32px; }
      .request-header { grid-template-columns: 1fr; }
      button, .button { width: 100%; }
      .actions { justify-content: stretch; }
    }
  </style>
</head>
<body>
  <header>
    <h1>LRCLIB Cache</h1>
    <div class="header-actions">
      <a class="button secondary" href="/admin">Back to admin</a>
      <form method="post" action="/admin/logout"><button class="secondary">Log out</button></form>
    </div>
  </header>
  <main>
    <section id="login" class="login" hidden>
      <form method="post" action="/admin/login">
        <strong>Admin login</strong>
        <input name="password" type="password" autocomplete="current-password" placeholder="Password" required>
        <button>Log in</button>
      </form>
    </section>
    <section id="app" hidden>
      <div class="toolbar">
        <button id="refresh">Refresh cache</button>
        <span id="status" class="status"></span>
      </div>
      <div id="list" class="list"></div>
    </section>
  </main>
  <script>
    const login = document.querySelector("#login");
    const app = document.querySelector("#app");
    const list = document.querySelector("#list");
    const status = document.querySelector("#status");
    let tracks = [];

    async function api(path, options = {}) {
      const response = await fetch(path, {
        headers: { "content-type": "application/json", ...(options.headers || {}) },
        ...options
      });
      if (response.status === 401) throw new Error("unauthorized");
      if (!response.ok) {
        const text = await response.text();
        throw new Error(text || "Request failed.");
      }
      return response.json();
    }

    async function load() {
      try {
        const data = await api("/admin/api/lrclib-cache");
        tracks = data.tracks || [];
        login.hidden = true;
        app.hidden = false;
        status.textContent = `${tracks.length} cached track${tracks.length === 1 ? "" : "s"}.`;
        render();
      } catch (error) {
        if (error.message === "unauthorized") {
          login.hidden = false;
          app.hidden = true;
          return;
        }
        status.textContent = error.message;
      }
    }

    function render() {
      list.innerHTML = "";
      if (tracks.length === 0) {
        list.innerHTML = `<div class="empty">No cached LRCLIB tracks.</div>`;
        return;
      }

      for (const track of tracks) {
        const article = document.createElement("article");
        article.className = "request";
        article.innerHTML = `
          <div class="request-header">
            <div>
              <div class="title">
                <span>${escapeHtml(track.title)}</span>
                <span class="artist">${escapeHtml(track.artist)}</span>
                <span class="badge">lrc</span>
              </div>
              <div class="meta">
                Album: ${escapeHtml(track.album || "Singles")} &middot;
                Duration: ${escapeHtml(formatDuration(track.duration))} &middot;
                Cached: ${escapeHtml(new Date(track.cachedAt).toLocaleString())} &middot;
                Query: ${escapeHtml(track.searchQuery)}
              </div>
              <div class="meta">Path: ${escapeHtml(track.relativePath)}</div>
            </div>
            <div class="actions">
              <button data-preview="${track.id}">Preview</button>
              <button data-approve="${track.id}">Approve</button>
              <button class="danger" data-reject="${track.id}">Reject</button>
            </div>
          </div>
          <div class="preview" id="preview-${track.id}"><pre>Loading...</pre></div>`;
        list.appendChild(article);
      }
    }

    list.addEventListener("click", async event => {
      const approveId = event.target.dataset.approve;
      const rejectId = event.target.dataset.reject;
      const previewId = event.target.dataset.preview;

      if (previewId) {
        const previewDiv = document.getElementById("preview-" + previewId);
        previewDiv.classList.toggle("visible");
        if (previewDiv.classList.contains("visible") && previewDiv.querySelector("pre").textContent === "Loading...") {
          try {
            const data = await api(`/admin/api/lrclib-cache/${encodeURIComponent(previewId)}/preview`);
            previewDiv.querySelector("pre").textContent = data.lyrics || "No lyrics available.";
          } catch (error) {
            previewDiv.querySelector("pre").textContent = "Failed to load preview: " + error.message;
          }
        }
        return;
      }

      if (!approveId && !rejectId) return;

      const id = approveId || rejectId;
      const action = approveId ? "approve" : "reject";
      const message = approveId
        ? "Approve this cached track and add it to the main lyrics folder?"
        : "Reject this cached track and delete it from the cache?";

      if (!confirm(message)) return;

      try {
        const data = await api(`/admin/api/lrclib-cache/${encodeURIComponent(id)}/${action}`, { method: "POST", body: "{}" });
        status.textContent = approveId
          ? `Approved and added to lyrics.`
          : "Rejected and removed.";
        await load();
      } catch (error) {
        status.textContent = error.message;
      }
    });

    document.querySelector("#refresh").addEventListener("click", load);

    function escapeHtml(value) {
      return String(value ?? "").replace(/[&<>"']/g, char => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
      }[char]));
    }

    function formatDuration(seconds) {
      if (!seconds || seconds <= 0) return "0:00";
      const m = Math.floor(seconds / 60);
      const s = Math.floor(seconds % 60);
      return `${m}:${s.toString().padStart(2, "0")}`;
    }

    load();
  </script>
</body>
</html>
""";
}
