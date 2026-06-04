namespace Lyrictified.Server;

public static class SubmitPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Submit Lyrics - Lyrictified</title>
  <style>
    :root { color-scheme: light; font-family: Segoe UI, system-ui, sans-serif; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #f7f8fb; color: #151922; }
    header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 18px 24px; background: #ffffff; border-bottom: 1px solid #d9dde5; }
    main { max-width: 920px; margin: 0 auto; padding: 28px 24px 44px; }
    button, input, select, textarea { font: inherit; }
    button, .button { display: inline-flex; align-items: center; justify-content: center; min-height: 40px; border: 1px solid #1e5eff; background: #1e5eff; color: #fff; border-radius: 6px; padding: 8px 13px; cursor: pointer; text-decoration: none; font-weight: 600; }
    .button.secondary { background: #fff; color: #1e5eff; }
    input, select, textarea { width: 100%; border: 1px solid #c4cad6; border-radius: 6px; padding: 8px 10px; background: #fff; color: #151922; }
    input, select { min-height: 40px; }
    textarea { min-height: 360px; resize: vertical; font-family: Consolas, Cascadia Mono, monospace; line-height: 1.45; }
    label { display: grid; gap: 6px; color: #344054; font-size: 13px; font-weight: 600; }
    h1 { margin: 0; font-size: 22px; letter-spacing: 0; }
    .brand { display: inline-flex; align-items: center; gap: 10px; min-width: 0; }
    .brand img { width: 36px; height: 36px; object-fit: contain; }
    .nav { display: flex; flex-wrap: wrap; gap: 10px; }
    .panel { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 18px; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin-bottom: 12px; }
    .wide { grid-column: 1 / -1; }
    .hint { margin: 0 0 14px; color: #667085; font-size: 13px; }
    .tabs { display: inline-flex; gap: 6px; padding: 4px; margin: 2px 0 12px; background: #eef2f7; border: 1px solid #d9dde5; border-radius: 8px; }
    .tab { min-height: 34px; border-color: transparent; background: transparent; color: #344054; padding: 6px 12px; }
    .tab.active { border-color: #1e5eff; background: #fff; color: #1e5eff; }
    .source-panel[hidden] { display: none; }
    .file-row { display: grid; gap: 8px; padding: 14px; border: 1px dashed #aeb7c6; border-radius: 8px; background: #f8fafc; }
    .submit-actions { display: grid; justify-items: start; gap: 8px; }
    .admin-approval-note { margin: 0; color: #027a48; font-size: 13px; font-weight: 600; }
    .admin-approval-note[hidden] { display: none; }
    .rate-limit { color: #344054; font-weight: 600; line-height: 1.45; }
    .rate-limit[hidden] { display: none; }
    .status { min-height: 22px; margin-top: 14px; color: #667085; }
    .status.error { color: #b42318; }
    .status.success { color: #027a48; }
    @media (max-width: 720px) {
      header { align-items: flex-start; flex-direction: column; padding: 16px; }
      main { padding: 18px 16px 32px; }
      .grid { grid-template-columns: 1fr; }
      button, .button { width: 100%; }
    }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <img src="/assets/logo" alt="" width="36" height="36">
      <h1>Submit Lyrics</h1>
    </div>
    <nav class="nav">
      <a class="button secondary" href="/">Search lyrics</a>
      <a class="button secondary" href="/admin">Log in as admin</a>
    </nav>
  </header>
  <main>
    <section class="panel">
      <p id="intro" class="hint" hidden>Submitted lyrics stay pending until an admin reviews and approves them.</p>
      <div id="rate-limit" class="rate-limit" role="status">Checking submission availability...</div>
      <form id="submit-form" hidden>
        <div class="grid">
          <label>
            Song title
            <input name="title" autocomplete="off" required maxlength="180">
          </label>
          <label>
            Artist
            <input name="artist" autocomplete="off" required maxlength="180">
          </label>
          <label>
            Album
            <input name="album" autocomplete="off" maxlength="180" placeholder="Optional">
          </label>
          <label>
            Lyrics type
            <select id="format" name="format" required>
              <option value="lrc">Regular LRC (.lrc)</option>
              <option value="elrc">Enhanced LRC (.elrc)</option>
              <option value="ttml">TTML (.ttml)</option>
            </select>
          </label>
          <div class="wide">
            <div class="tabs" role="tablist" aria-label="Lyrics source">
              <button class="tab active" type="button" role="tab" aria-selected="true" data-source="paste">Paste text</button>
              <button class="tab" type="button" role="tab" aria-selected="false" data-source="upload">Upload file</button>
            </div>
            <label id="paste-panel" class="source-panel">
              Lyrics
              <textarea id="lyrics" name="lyrics" required placeholder="[00:01.00]First line"></textarea>
            </label>
            <div id="upload-panel" class="source-panel file-row" hidden>
              <label>
                Lyrics file
                <input id="lyrics-file" name="lyricsFile" type="file" accept=".lrc">
              </label>
              <p class="hint" id="file-hint">Only .lrc files are accepted for the selected lyrics type.</p>
            </div>
          </div>
        </div>
        <div class="submit-actions">
          <button id="submit-button" type="submit">Submit for review</button>
          <p id="admin-approval-note" class="admin-approval-note" hidden>Since you are signed in as an admin, these lyrics will be auto-approved and added immediately.</p>
        </div>
        <div id="status" class="status" role="status"></div>
      </form>
    </section>
  </main>
  <script>
    const form = document.querySelector("#submit-form");
    const intro = document.querySelector("#intro");
    const rateLimit = document.querySelector("#rate-limit");
    const status = document.querySelector("#status");
    const submitButton = document.querySelector("#submit-button");
    const adminApprovalNote = document.querySelector("#admin-approval-note");
    const format = document.querySelector("#format");
    const lyrics = document.querySelector("#lyrics");
    const lyricsFile = document.querySelector("#lyrics-file");
    const fileHint = document.querySelector("#file-hint");
    const tabs = document.querySelectorAll(".tab");
    const pastePanel = document.querySelector("#paste-panel");
    const uploadPanel = document.querySelector("#upload-panel");
    let sourceMode = "paste";
    let isAdmin = false;

    loadStatus();

    tabs.forEach(tab => {
      tab.addEventListener("click", () => {
        sourceMode = tab.dataset.source;
        tabs.forEach(candidate => {
          const active = candidate === tab;
          candidate.classList.toggle("active", active);
          candidate.setAttribute("aria-selected", String(active));
        });
        pastePanel.hidden = sourceMode !== "paste";
        uploadPanel.hidden = sourceMode !== "upload";
        lyrics.required = sourceMode === "paste";
        lyricsFile.required = sourceMode === "upload";
        status.className = "status";
        status.textContent = "";
      });
    });

    format.addEventListener("change", () => {
      updateFileAccept();
      if (lyricsFile.files.length > 0 && !fileMatchesFormat(lyricsFile.files[0])) {
        lyricsFile.value = "";
        status.className = "status error";
        status.textContent = `Choose a .${format.value} file for the selected lyrics type.`;
      }
    });

    lyricsFile.addEventListener("change", () => {
      if (lyricsFile.files.length === 0) return;
      if (!fileMatchesFormat(lyricsFile.files[0])) {
        lyricsFile.value = "";
        status.className = "status error";
        status.textContent = `Only .${format.value} files are accepted for the selected lyrics type.`;
        return;
      }

      status.className = "status";
      status.textContent = "";
    });

    form.addEventListener("submit", async event => {
      event.preventDefault();
      status.className = "status";
      status.textContent = "";

      if (sourceMode === "upload" && (lyricsFile.files.length === 0 || !fileMatchesFormat(lyricsFile.files[0]))) {
        status.className = "status error";
        status.textContent = `Upload a .${format.value} lyrics file.`;
        return;
      }

      if (!isAdmin) {
        const accepted = confirm("Submitting now means you will not be able to submit another lyrics request for the next 2 hours. Continue?");
        if (!accepted) return;
      }

      try {
        const response = sourceMode === "upload"
          ? await submitUpload()
          : await submitPaste();
        const data = await response.json();
        if (!response.ok) {
          if (response.status === 429 && data.nextAllowedAt && !isAdmin) {
            intro.hidden = true;
            form.hidden = true;
            rateLimit.hidden = false;
            rateLimit.textContent = `You can submit lyrics again after ${new Date(data.nextAllowedAt).toLocaleString()}.`;
            return;
          }

          throw new Error(data.error || "Submission failed.");
        }

        form.reset();
        updateFileAccept();
        if (data.autoApproved) {
          status.className = "status success";
          status.textContent = `Added and auto-approved as ${data.relativePath}.`;
          return;
        }

        if (!isAdmin) {
          const nextAllowedAt = new Date(new Date(data.submittedAt).getTime() + 2 * 60 * 60 * 1000);
          intro.hidden = true;
          form.hidden = true;
          rateLimit.hidden = false;
          rateLimit.textContent = `Submitted. An admin can review it in Pending requests. You can submit lyrics again after ${nextAllowedAt.toLocaleString()}.`;
          return;
        }

        status.className = "status success";
        status.textContent = "Submitted. An admin can review it in Pending requests.";
      } catch (error) {
        status.className = "status error";
        status.textContent = error.message;
      }
    });

    async function loadStatus() {
      try {
        const response = await fetch("/api/submissions/status");
        const data = await response.json();
        isAdmin = Boolean(data.isAdmin);
        if (data.nextAllowedAt && !isAdmin) {
          intro.hidden = true;
          form.hidden = true;
          rateLimit.hidden = false;
          rateLimit.textContent = `You can submit lyrics again after ${new Date(data.nextAllowedAt).toLocaleString()}.`;
          return;
        }

        intro.hidden = false;
        form.hidden = false;
        rateLimit.hidden = true;
        updateAdminCopy();
      } catch {
        isAdmin = false;
        intro.hidden = false;
        form.hidden = false;
        rateLimit.hidden = true;
        updateAdminCopy();
      }
    }

    async function submitPaste() {
      const payload = Object.fromEntries(new FormData(form).entries());
      delete payload.lyricsFile;
      return fetch("/api/submissions", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(payload)
      });
    }

    async function submitUpload() {
      const body = new FormData();
      body.set("title", form.elements.title.value);
      body.set("artist", form.elements.artist.value);
      body.set("album", form.elements.album.value);
      body.set("format", format.value);
      body.set("lyricsFile", lyricsFile.files[0]);
      return fetch("/api/submissions", {
        method: "POST",
        body
      });
    }

    function updateFileAccept() {
      const extension = `.${format.value}`;
      lyricsFile.accept = extension;
      fileHint.textContent = `Only ${extension} files are accepted for the selected lyrics type.`;
    }

    function fileMatchesFormat(file) {
      return file.name.toLowerCase().endsWith(`.${format.value}`);
    }

    function updateAdminCopy() {
      intro.textContent = isAdmin
        ? "Add a new lyrics file directly to the indexed lyrics folder."
        : "Submitted lyrics stay pending until an admin reviews and approves them.";
      submitButton.textContent = isAdmin ? "Add and auto-approve" : "Submit for review";
      adminApprovalNote.hidden = !isAdmin;
    }

    updateFileAccept();
  </script>
</body>
</html>
""";
}
