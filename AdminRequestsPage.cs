namespace Lyrictified.Server;

public static class AdminRequestsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Lyrictified Pending Requests</title>
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
    <h1>Pending Requests</h1>
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
        <button id="refresh">Refresh requests</button>
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
    let requests = [];

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
        const data = await api("/admin/api/submissions");
        requests = data.submissions || [];
        login.hidden = true;
        app.hidden = false;
        status.textContent = `${requests.length} pending request${requests.length === 1 ? "" : "s"}.`;
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
      if (requests.length === 0) {
        list.innerHTML = `<div class="empty">No pending requests.</div>`;
        return;
      }

      for (const request of requests) {
        const article = document.createElement("article");
        article.className = "request";
        article.innerHTML = `
          <div class="request-header">
            <div>
              <div class="title">
                <span>${escapeHtml(request.title)}</span>
                <span class="artist">${escapeHtml(request.artist)}</span>
                <span class="badge">${escapeHtml(request.format)}</span>
              </div>
              <div class="meta">
                Album: ${escapeHtml(request.album || "Singles")} &middot;
                Suggested path: ${escapeHtml(request.suggestedRelativePath)} &middot;
                Submitted: ${escapeHtml(new Date(request.submittedAt).toLocaleString())}
              </div>
              <div class="meta">Submitter: ${escapeHtml(request.submitterKey)}</div>
            </div>
            <div class="actions">
              <button data-approve="${request.id}">Approve</button>
              <button class="danger" data-reject="${request.id}">Reject</button>
            </div>
          </div>
          <pre>${escapeHtml(request.lyrics)}</pre>`;
        list.appendChild(article);
      }
    }

    list.addEventListener("click", async event => {
      const approveId = event.target.dataset.approve;
      const rejectId = event.target.dataset.reject;
      if (!approveId && !rejectId) return;

      const id = approveId || rejectId;
      const action = approveId ? "approve" : "reject";
      const message = approveId
        ? "Approve this submission and write it into the lyrics folder?"
        : "Reject this submission and remove it from pending requests?";

      if (!confirm(message)) return;

      try {
        const data = await api(`/admin/api/submissions/${encodeURIComponent(id)}/${action}`, { method: "POST", body: "{}" });
        status.textContent = approveId
          ? `Approved as ${data.relativePath}.`
          : "Rejected.";
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

    load();
  </script>
</body>
</html>
""";
}
