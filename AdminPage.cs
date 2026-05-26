namespace Lyrictified.Server;

public static class AdminPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Lyrictified Admin</title>
  <style>
    :root { color-scheme: light; font-family: Segoe UI, system-ui, sans-serif; }
    body { margin: 0; background: #f6f7f9; color: #151922; }
    header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 18px 24px; background: #ffffff; border-bottom: 1px solid #d9dde5; }
    h1 { margin: 0; font-size: 20px; }
    main { max-width: 1250px; margin: 0 auto; padding: 24px; }
    button, input { font: inherit; }
    button { border: 1px solid #1e5eff; background: #1e5eff; color: #fff; border-radius: 6px; padding: 8px 12px; cursor: pointer; }
    button.secondary { background: #fff; color: #1e5eff; }
    input { border: 1px solid #c4cad6; border-radius: 6px; padding: 8px 10px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; margin-bottom: 18px; }
    .login { max-width: 360px; margin-top: 80px; background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 18px; }
    .login form { display: grid; gap: 10px; }
    .list { display: grid; gap: 12px; }
    .item { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 14px; display: grid; gap: 10px; }
    .row { display: grid; grid-template-columns: minmax(140px, 1fr) minmax(140px, 1fr) minmax(140px, 1fr) 80px minmax(260px, 1fr) auto; gap: 10px; align-items: center; }
    .rules { display: grid; grid-template-columns: repeat(3, auto) minmax(260px, 1fr); gap: 12px; align-items: center; }
    .check { display: inline-flex; align-items: center; gap: 6px; color: #344054; font-size: 13px; }
    .check input { padding: 0; }
    .patterns[hidden] { display: none; }
    .meta { color: #667085; font-size: 13px; }
    .status { color: #667085; min-height: 20px; }
    @media (max-width: 850px) { .row { grid-template-columns: 1fr; } header { align-items: flex-start; flex-direction: column; } }
  </style>
</head>
<body>
  <header>
    <h1>Lyrictified Admin</h1>
    <form method="post" action="/admin/logout"><button class="secondary">Log out</button></form>
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
        <input id="filter" placeholder="Filter indexed files">
        <button id="refresh">Refresh index</button>
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
    const filter = document.querySelector("#filter");
    let lyrics = [];

    async function api(path, options = {}) {
      const response = await fetch(path, {
        headers: { "content-type": "application/json", ...(options.headers || {}) },
        ...options
      });
      if (response.status === 401) throw new Error("unauthorized");
      if (!response.ok) throw new Error(await response.text());
      return response.json();
    }

    async function load() {
      try {
        const data = await api("/admin/api/lyrics");
        lyrics = data.lyrics;
        login.hidden = true;
        app.hidden = false;
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
      const term = filter.value.toLowerCase();
      list.innerHTML = "";
      lyrics
        .filter(file => `${file.artist} ${file.title} ${file.album || ""} ${file.relativePath} ${formatTags(file.tags)}`.toLowerCase().includes(term))
        .forEach(file => {
          const item = document.createElement("article");
          item.className = "item";
          item.innerHTML = `
            <div class="meta">${file.format.toUpperCase()} &middot; ${file.relativePath} &middot; ${file.id}</div>
            <div class="row">
              <input aria-label="Title" data-field="title" value="${escapeHtml(file.title)}">
              <input aria-label="Artist" data-field="artist" value="${escapeHtml(file.artist)}">
              <input aria-label="Album" data-field="album" value="${escapeHtml(file.album || "")}" placeholder="Album">
              <input aria-label="Rating" data-field="rating" type="number" min="0" max="100" value="${file.rating}">
              <input aria-label="Tags" data-field="tags" value="${escapeHtml(formatTags(file.tags))}" placeholder="tag | score, another tag | 20">
              <button data-save="${file.id}">Save</button>
            </div>
            <div class="rules">
              <label class="check"><input type="checkbox" data-field="exact" ${file.exact ? "checked" : ""}> Exact</label>
              <label class="check"><input type="checkbox" data-field="ignore" ${file.ignore ? "checked" : ""}> Ignore</label>
              <label class="check"><input type="checkbox" data-field="reverse" ${file.reverse ? "checked" : ""}> Reverse</label>
              <input class="patterns" aria-label="Ignore patterns" data-field="ignorePatterns" value="${escapeHtml(file.ignorePatterns || "")}" placeholder="patterns like *blue*, *remix*" ${file.ignore ? "" : "hidden"}>
            </div>`;
          list.appendChild(item);
        });
    }

    function escapeHtml(value) {
      return String(value).replace(/[&<>"']/g, char => ({
        "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
      }[char]));
    }

    function formatTags(tags) {
      return (tags || [])
        .map(tag => typeof tag === "string" ? tag : `${tag.name} | ${tag.score || 0}`)
        .join(", ");
    }

    function parseTags(value) {
      return value
        .split(",")
        .map(tag => tag.trim())
        .filter(Boolean)
        .map(tag => {
          const parts = tag.split("|");
          const name = parts[0].trim();
          const score = Number((parts[1] || "0").trim());
          return { name, score: Number.isFinite(score) ? Math.max(0, Math.min(100, score)) : 0 };
        })
        .filter(tag => tag.name.length > 0);
    }

    list.addEventListener("click", async event => {
      const id = event.target.dataset.save;
      if (!id) return;
      const item = event.target.closest(".item");
      const row = item.querySelector(".row");
      const update = {
        title: row.querySelector("[data-field=title]").value,
        artist: row.querySelector("[data-field=artist]").value,
        album: row.querySelector("[data-field=album]").value,
        rating: Number(row.querySelector("[data-field=rating]").value || 0),
        tags: parseTags(row.querySelector("[data-field=tags]").value),
        exact: item.querySelector("[data-field=exact]").checked,
        ignore: item.querySelector("[data-field=ignore]").checked,
        reverse: item.querySelector("[data-field=reverse]").checked,
        ignorePatterns: item.querySelector("[data-field=ignorePatterns]").value
      };
      await api(`/admin/api/lyrics/${id}`, { method: "PUT", body: JSON.stringify(update) });
      status.textContent = "Saved.";
      await load();
    });

    list.addEventListener("change", event => {
      if (event.target.dataset.field !== "ignore") return;
      const item = event.target.closest(".item");
      item.querySelector("[data-field=ignorePatterns]").hidden = !event.target.checked;
    });

    document.querySelector("#refresh").addEventListener("click", async () => {
      const data = await api("/admin/api/lyrics/refresh", { method: "POST", body: "{}" });
      status.textContent = `Indexed ${data.indexedFiles} file(s).`;
      await load();
    });

    filter.addEventListener("input", render);
    load();
  </script>
</body>
</html>
""";
}
