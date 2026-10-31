/* Launcher UI.
   Host -> UI messages (LauncherController, Ro3EmuController): state, progress, server, settings, news, hero, notes, shortcuts, uninstall, toast.
   UI -> host commands: ready, primary, pause, repair, changeDir, openDir, shortcuts, notesSeen, uninstall, window, openUrl, panel
   (the title bar's Web panel entry, settings.panel: state open | start | checking | unavailable, url, hint);
   RO3Emu mode (settings.mode "ro3emu") also: later, host, join, stopServer, and its own phases nogame, gameupdate,
   appupdate, working. ?app=ro3emu runs the RO3Emu mode mock (see createRo3EmuMock).
   Inside the launcher the host is WebView2 (window.chrome.webview); in a plain browser a mock host simulates the flow:
   ?mock=install|update|ready|downloading|installing|repair|offline|error|checking|running (default update),
   &panel=settings, &notes=1 (What's new), &first=1 (the shortcuts question), &hero=<image url>, &links=0,
   &news=0 (no news.json: the server's name and status only), &fast=10 (a faster fake download), &webpanel=0 (no Web
   panel entry; in the RO3Emu mock a joined server without a panel). */
(function () {
  "use strict";

  var STAGES = {
    scanning: "Checking local files",
    downloading: "Downloading",
    assembling: "Assembling files",
    verifying: "Verifying",
    committing: "Finishing up"
  };

  // ---------- state ----------
  var S = {
    phase: "checking",
    installed: null,
    remote: null,
    message: null,
    downloadBytes: null,
    progress: null,
    news: [],
    server: null,
    settings: null
  };

  var $ = function (id) { return document.getElementById(id); };
  var host = null;

  // ---------- host ----------
  function send(obj) { if (host) host.post(obj); }

  function handle(msg) {
    if (typeof msg === "string") { try { msg = JSON.parse(msg); } catch (e) { return; } }
    if (!msg || typeof msg !== "object") return;
    switch (msg.type) {
      case "state":
        var wasTransfer = isTransfer(S.phase);
        S.phase = msg.phase || "checking";
        S.installed = msg.installed || null;
        S.remote = msg.remote || null;
        S.message = msg.message || null;
        S.title = msg.title || null;
        S.link = msg.link && msg.link.text && msg.link.cmd ? msg.link : null;
        S.downloadBytes = typeof msg.downloadBytes === "number" ? msg.downloadBytes : null;
        if (!isTransfer(S.phase) || !wasTransfer) S.progress = null;
        primaryLock = false;
        renderState();
        renderSettings();
        break;
      case "progress":
        S.progress = msg;
        renderProgress();
        break;
      case "news":
        S.news = Array.isArray(msg.items) ? msg.items : [];
        renderNews();
        break;
      case "server":
        S.server = { online: !!msg.online, players: msg.players, label: msg.label || null, starting: !!msg.starting };
        renderServer();
        break;
      case "hero":
        showHero(msg.url);
        break;
      case "shortcuts":
        onShortcuts(msg);
        break;
      case "notes":
        openNotes(msg);
        break;
      case "uninstall":
        onUninstall(msg);
        break;
      case "toast":
        if (msg.text) toast(String(msg.text));
        break;
      case "settings":
        S.settings = msg;
        renderBrand();
        renderNews();
        renderState();
        renderSettings();
        break;
    }
  }

  // "working" (RO3Emu mode: export, server start) shows the bar like a download but cannot be paused
  function isTransfer(phase) { return phase === "updating" || phase === "working"; }
  function serverName() { return (S.settings && S.settings.serverName) || ""; }
  function ro3emuMode() { return !!(S.settings && S.settings.mode === "ro3emu"); }

  // ---------- formatting ----------
  var MB = 1024 * 1024, GB = MB * 1024;
  function unitFor(n) { return n >= GB ? [GB, "GB", 2] : [MB, "MB", 1]; }
  function fmtBytes(n) { var u = unitFor(n); return (n / u[0]).toFixed(u[2]) + " " + u[1]; }
  function fmtPair(done, total) {
    var u = unitFor(total);
    return "<b>" + (done / u[0]).toFixed(u[2]) + "</b> / " + (total / u[0]).toFixed(u[2]) + " " + u[1];
  }
  function fmtSpeed(bps) {
    if (bps >= 100 * MB) return Math.round(bps / MB) + " MB/s";
    if (bps >= MB) return (bps / MB).toFixed(1) + " MB/s";
    return Math.max(1, Math.round(bps / 1024)) + " KB/s";
  }
  function fmtEta(s) {
    s = Math.round(s);
    if (s < 60) return s + " s left";
    if (s < 600) return Math.floor(s / 60) + " min " + (s % 60) + " s left";
    if (s < 3600) return Math.round(s / 60) + " min left";
    return Math.floor(s / 3600) + " h " + Math.round((s % 3600) / 60) + " min left";
  }
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  function fmtDate(d, short) {
    if (!d) return "";
    var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(d));
    if (!m) return String(d);
    return (+m[3]) + " " + MONTHS[(+m[2]) - 1] + (short && +m[1] === new Date().getFullYear() ? "" : " " + m[1]);
  }
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }
  function icon(id) { return '<svg class="i"><use href="#' + id + '"/></svg>'; }
  function tagClass(tag) {
    var t = String(tag || "").toLowerCase();
    if (/patch|update|release/.test(t)) return "patch";
    if (/event|festival/.test(t)) return "event";
    if (/maint/.test(t)) return "maintenance";
    if (/news|announce/.test(t)) return "news";
    return "";
  }
  function safeUrl(u) { return /^https?:\/\//i.test(String(u || "")) ? String(u) : null; }

  // ---------- render: phase ----------
  var primaryLock = false;
  var pausedByUser = false;     // the player pressed Pause: the next install/update offer reads "Resume"

  function renderState() {
    var p = S.phase, b = document.body;
    if (p !== "install" && p !== "update" && p !== "checking" && p !== "nogame" && p !== "gameupdate" && p !== "appupdate") pausedByUser = false;
    b.setAttribute("data-phase", p);
    renderVersion();

    var btn = $("primary"), label = $("primary-label"), useEl = btn.querySelector("use");
    var cls = "", text = "", ico = "i-download", disabled = false, busy = false;
    switch (p) {
      case "install": text = pausedByUser ? "Resume" : "Install"; break;
      case "update": text = pausedByUser ? "Resume" : "Update"; break;
      case "nogame": text = pausedByUser ? "Resume" : "Install"; break;
      case "gameupdate": text = pausedByUser ? "Resume" : "Update game"; break;
      case "appupdate": text = pausedByUser ? "Resume" : "Update"; break;
      case "repair": text = "Repair"; ico = "i-wrench"; break;
      case "updating": text = "Pause"; ico = "i-pause"; cls = "pause"; break;
      case "working": text = "Please wait"; disabled = true; busy = true; cls = "wait"; break;
      case "ready": text = "Play"; ico = "i-play"; cls = "play"; break;
      case "launching": text = "Starting…"; disabled = true; busy = true; cls = "wait"; break;
      case "running": text = "Running"; ico = "i-play"; disabled = true; break;
      case "error": text = "Retry"; ico = "i-refresh"; break;
      case "offline": text = "Retry"; ico = "i-refresh"; break;
      default: text = "Checking…"; disabled = true; busy = true; cls = "wait";
    }
    btn.className = "primary" + (cls ? " " + cls : "") + (busy ? " busy" : "");
    btn.disabled = disabled;
    label.textContent = text;
    useEl.setAttribute("href", "#" + ico);
    btn.setAttribute("aria-label", text);

    var transfer = isTransfer(p);
    $("status").hidden = transfer;
    $("progress").hidden = !transfer;
    if (transfer) renderProgress(); else renderStatus();
  }

  function renderStatus() {
    var p = S.phase, title = "", sub = "", ico = "i-sparkle";
    var rv = S.remote, iv = S.installed, name = serverName();
    var dir = S.settings && S.settings.dir;
    switch (p) {
      case "install":
        title = pausedByUser ? "Installation paused" : "Ready to install";
        sub = S.message || (dir ? "Version " + (rv || "") + (S.downloadBytes ? " (" + fmtBytes(S.downloadBytes) + " download)" : "") + " will be installed to " + dir : "Install the game to start your adventure.");
        ico = "i-download"; break;
      case "update":
        title = pausedByUser ? "Update paused" : "Update available";
        sub = S.message || ("Version " + (rv || "") + " is ready. Only the parts that changed are downloaded.");
        ico = "i-download"; break;
      case "repair":
        title = "Some files need repair";
        sub = S.message || "Some game files differ from the release.";
        ico = "i-wrench"; break;
      case "nogame":
        title = pausedByUser ? "Download paused" : "Ragnarok 3 not found";
        sub = S.message || "Download the game from Genesis' update servers, or pick the folder you have.";
        ico = "i-download"; break;
      case "gameupdate":
        title = "The game needs an update";
        sub = S.message || "Update it from Genesis' update servers.";
        ico = "i-download"; break;
      case "appupdate":
        title = "A new version is available";
        sub = S.message || ("Version " + (rv || "") + " is out.");
        ico = "i-download"; break;
      case "ready":
        title = "Ready to play";
        sub = S.message || ("Version " + (iv || rv || "") + " is installed and up to date.");
        ico = "i-check"; break;
      case "launching":
        title = name ? "Starting " + name : "Starting the game";
        sub = S.message || "The game window opens in a moment…"; break;
      case "running":
        title = name ? name + " is running" : "The game is running";
        sub = S.message || "Have a good adventure! The launcher waits in the tray."; ico = "i-play"; break;
      case "error":
        title = "Something went wrong";
        sub = S.message || "The update could not finish. Nothing was changed. Try again."; ico = "i-alert"; break;
      case "offline":
        title = "Can't reach the update server";
        sub = S.message || "Check your connection and retry."; ico = "i-cloud-off"; break;
      default:
        title = "Checking for updates";
        sub = S.message || "Comparing your files with the latest release…";
    }
    if (S.title) title = S.title;
    // a host-given action under the status (RO3Emu mode: Later, Pick game folder, Play with friends); else Change folder
    var link = $("status-change");
    if (S.link) { link.hidden = false; link.textContent = S.link.text; }
    else if (ro3emuMode() && p === "ready") { link.hidden = false; link.textContent = "Play with friends"; }
    else { link.hidden = p !== "install"; link.textContent = "Change folder"; }
    $("status-title").textContent = title;
    $("status-sub").textContent = sub;
    $("status-sub").title = sub;
    $("status-badge").querySelector("use").setAttribute("href", "#" + ico);
  }

  function renderVersion() {
    var iv = S.installed, rv = S.remote, el = $("ver-line");
    if (!iv && !rv) el.innerHTML = '<span class="na">' + (S.phase === "checking" ? "Checking…" : "Unknown") + "</span>";
    else if (!iv) el.innerHTML = '<span class="na">Not installed</span>';
    else if (rv && rv !== iv && S.phase !== "ready" && S.phase !== "running" && S.phase !== "launching")
      // both fit side by side when short; long (date) versions show the new one only
      el.innerHTML = iv.length + rv.length <= 12
        ? '<b class="old">' + esc(iv) + "</b>" + icon("i-arrow") + "<b>" + esc(rv) + "</b>"
        : icon("i-arrow") + "<b>" + esc(rv) + "</b>";
    else el.innerHTML = "<b>" + esc(iv) + "</b>";
    el.title = iv && rv && rv !== iv ? "Installed " + iv + ", available " + rv : "";
  }

  function renderProgress() {
    if (!isTransfer(S.phase)) return;
    var pr = S.progress, bar = $("p-bar"), fill = $("p-fill");
    if (!pr) {
      $("p-stage").textContent = S.title || "Preparing";
      $("p-file").textContent = "";
      $("p-pct").textContent = "";
      bar.classList.add("indet");
      $("p-bytes").textContent = S.phase === "working" ? "" : "Getting the update ready…";
      $("p-rate").textContent = "";
      return;
    }
    $("p-stage").textContent = STAGES[pr.stage] || pr.stage || "Working";
    $("p-file").textContent = pr.file || "";
    $("p-file").title = pr.file || "";
    var total = +pr.total || 0, done = Math.min(+pr.done || 0, total || Infinity);
    if (total > 0) {
      var pct = Math.max(0, Math.min(100, done / total * 100));
      bar.classList.remove("indet");
      fill.style.width = pct.toFixed(2) + "%";
      $("p-pct").textContent = Math.floor(pct) + "%";
      // bytes (downloads) or items (RO3Emu mode: tables, maps... of the game data export)
      $("p-bytes").innerHTML = pr.unit === "items" ? "<b>" + done.toLocaleString("en-US") + "</b> / " + total.toLocaleString("en-US") : fmtPair(done, total);
    } else {
      bar.classList.add("indet");
      $("p-pct").textContent = "";
      $("p-bytes").textContent = pr.stage === "committing" ? "Moving the new files into place…" : "";
    }
    var rate = [];
    if (pr.rate > 0) rate.push(fmtSpeed(pr.rate));
    if (pr.eta > 0 && total > 0) rate.push(fmtEta(pr.eta));
    $("p-rate").textContent = rate.join("  ·  ");
  }

  // ---------- render: brand, links, hero ----------
  function renderBrand() {
    var name = serverName();
    $("brand-name").textContent = name;
    document.title = name || "Launcher";
    renderLinks();
  }

  function linkIcon(l) {
    var known = { globe: 1, chat: 1, book: 1, trophy: 1, flag: 1, link: 1, code: 1 };
    if (l.icon && known[l.icon]) return "i-" + l.icon;
    var t = String(l.label || "").toLowerCase();
    if (/discord|chat|forum/.test(t)) return "i-chat";
    if (/github|source|code/.test(t)) return "i-code";
    if (/wiki|guide|help|docs/.test(t)) return "i-book";
    if (/rank|ladder/.test(t)) return "i-trophy";
    if (/web|site|home/.test(t)) return "i-globe";
    return "i-link";
  }

  function renderLinks() {
    var nav = $("links"), links = (S.settings && S.settings.links) || [];
    nav.innerHTML = "";
    renderPanelLink(nav);
    links.forEach(function (l) {
      var u = safeUrl(l.url);
      if (!u || !l.label) return;
      var b = document.createElement("button");
      b.type = "button";
      b.className = "link";
      b.innerHTML = icon(linkIcon(l)) + esc(l.label);
      b.title = u;
      b.addEventListener("click", function () { send({ cmd: "openUrl", url: u }); });
      nav.appendChild(b);
    });
  }

  // The server's web panel, first among the links. The host decides what a click does (open it, or start the
  // player's own server first); without a panel (a joined server that has not shared one) it is dimmed and says why.
  function renderPanelLink(nav) {
    var pn = S.settings && S.settings.panel;
    if (!pn || !pn.state) return;
    var on = pn.state === "open" || pn.state === "start";
    var b = document.createElement("button");
    b.type = "button";
    b.className = "link" + (on ? "" : " off");
    b.innerHTML = icon("i-panel") + "Web panel";
    b.title = pn.state === "open" ? (safeUrl(pn.url) || "") : (pn.hint || "");
    if (!on) b.setAttribute("aria-disabled", "true");
    b.addEventListener("click", function () {
      if (on) send({ cmd: "panel" });
      else if (pn.hint) toast(pn.hint);
    });
    nav.appendChild(b);
  }

  function showHero(url) {
    var img = $("art-img");
    if (!url) return;
    img.onload = function () { img.hidden = false; $("art").classList.remove("no-hero"); };
    img.onerror = function () { img.hidden = true; $("art").classList.add("no-hero"); };
    img.src = url;
  }

  // ---------- render: news, server ----------
  var featuredUrl = null;

  function renderNews() {
    var items = S.news || [];
    var f = items[0];
    if (f) {
      var bits = [];
      if (f.tag) bits.push(f.tag);
      if (f.date) bits.push(fmtDate(f.date));
      $("feat-kicker").textContent = bits.join(" · ") || "Latest news";
      $("feat-kicker").hidden = false;
      $("feat-title").textContent = f.title || "";
      var text = f.text || f.excerpt || "";
      $("feat-excerpt").textContent = text;
      $("feat-excerpt").hidden = !text;
      featuredUrl = safeUrl(f.url);
      $("feat-more").hidden = !featuredUrl;
    } else {
      // no news.json: the server's name and its status only
      $("feat-kicker").hidden = true;
      $("feat-title").textContent = serverName();
      $("feat-excerpt").hidden = true;
      featuredUrl = null;
      $("feat-more").hidden = true;
    }
    var rest = items.slice(1, 4), ul = $("news-list");
    ul.innerHTML = "";
    rest.forEach(function (it) {
      var li = document.createElement("li");
      var b = document.createElement("button");
      b.type = "button";
      b.className = "item";
      b.innerHTML = '<span class="t">' + esc(it.title) + "</span>" +
        '<span class="m"><span class="tag ' + tagClass(it.tag) + '">' + esc(it.tag || "News") + "</span>" +
        (it.date ? "<i></i><time>" + esc(fmtDate(it.date, true)) + "</time>" : "") + "</span>" + icon("i-arrow");
      b.title = it.title || "";
      var u = safeUrl(it.url);
      if (u) b.addEventListener("click", function () { send({ cmd: "openUrl", url: u }); });
      else b.disabled = true;
      li.appendChild(b);
      ul.appendChild(li);
    });
    $("news-list-wrap").hidden = rest.length === 0;
  }

  function renderServer() {
    var el = $("server"), t = $("server-text"), s = S.server;
    if (!s) { el.setAttribute("data-state", "unknown"); t.textContent = "Checking server…"; return; }
    if (s.label) {
      // RO3Emu mode: the player's own server (stopped, starting, running or hosting, with its players)
      el.setAttribute("data-state", s.online ? "online" : s.starting ? "unknown" : "offline");
      var count = s.players;
      t.innerHTML = "<b>" + esc(s.label) + "</b>" + (s.online && typeof count === "number"
        ? " · " + count.toLocaleString("en-US") + (count === 1 ? " adventurer" : " adventurers") : "");
      return;
    }
    if (s.online) {
      el.setAttribute("data-state", "online");
      var n = s.players;
      t.innerHTML = "<b>Online</b>" + (typeof n === "number"
        ? " · " + n.toLocaleString("en-US") + (n === 1 ? " adventurer" : " adventurers") : "");
    } else {
      el.setAttribute("data-state", "offline");
      t.innerHTML = "<b>Server offline</b>";
    }
  }

  // ---------- render: settings ----------
  function renderSettings() {
    var st = S.settings || {};
    setPath($("set-dir"), st.dir || "");
    var appName = st.mode === "ro3emu" ? "RO3Emu" : "Launcher";
    $("set-launcher").textContent = st.launcher ? appName + " " + st.launcher : appName;
    $("set-uninstall-field").hidden = !st.canUninstall;
    var busy = isTransfer(S.phase) || S.phase === "launching" || S.phase === "running";
    var app = ro3emuMode();
    $("set-dir-change").disabled = busy || S.phase === "checking";
    // RO3Emu mode: repair compares the game with Genesis' release; there is always an installed app version
    $("set-repair").disabled = busy || (app ? !st.dir : !S.installed) || S.phase === "checking" || S.phase === "offline";
    $("set-repair-hint").textContent = app
      ? "Checks every game file against Genesis' release and fixes what differs. Your settings and screenshots are kept."
      : "Checks every game file and downloads only what is missing or damaged. Your settings and screenshots are kept.";
    $("set-uninstall").disabled = busy || S.phase === "checking";
    $("set-friends-field").hidden = !app;
    $("set-friends").disabled = busy || !st.dir;
    $("set-server-field").hidden = !(app && st.serverRunning);
    $("set-server-text").textContent = st.serverKind === "host" ? "Hosting for friends. Characters are saved in " + (st.dataDir || "the data folder") + "."
      : "Running on this PC. Characters are saved in " + (st.dataDir || "the data folder") + ".";
    $("set-server-stop").disabled = S.phase === "running" || S.phase === "launching" || S.phase === "working";
  }

  // ---------- RO3Emu mode: play with friends ----------
  function openFriends() {
    closeSettings();
    var st = S.settings || {};
    if (!$("host-address").value) $("host-address").value = st.lanAddress || "";
    $("host-address").placeholder = st.lanAddress ? "This PC's address, " + st.lanAddress : "This PC's address";
    $("friends").hidden = false;
    setTimeout(function () { $("join-address").focus(); }, 30);
  }
  function closeFriends() { $("friends").hidden = true; }

  // Long paths: show the end (the folder name), the start is in the tooltip.
  function setPath(input, value) {
    // The field is right-to-left so the ellipsis falls at the start; the marks keep the path itself left-to-right.
    var mark = String.fromCharCode(0x200E);
    input.value = value ? mark + value + mark : "";
    input.title = value;
  }

  var toastTimer = null;
  function toast(text) {
    var el = $("toast");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove("show"); }, 2200);
  }

  // ---------- shortcuts (first run, or from Settings) ----------
  function openShortcuts() {
    $("shortcuts").hidden = false;
    setTimeout(function () { $("shortcuts-yes").focus(); }, 30);
  }
  function onShortcuts(msg) {
    switch (msg.status) {
      case "ask": openShortcuts(); break;
      case "done":
        $("shortcuts").hidden = true;
        if (msg.created) toast("Shortcuts added.");
        break;
      case "error":
        $("shortcuts").hidden = true;
        toast("Couldn't add the " + (msg.failed || []).join(" and ") + " shortcut.");
        break;
    }
  }
  function answerShortcuts(yes) {
    send({ cmd: "shortcuts", desktop: yes && $("shortcuts-desktop").checked, startMenu: yes && $("shortcuts-start").checked });
    $("shortcuts").hidden = true;
  }

  // ---------- what's new after an update ----------
  function openNotes(msg) {
    var items = Array.isArray(msg.items) ? msg.items : [];
    if (!items.length) return;
    $("notes-version").textContent = "Version " + (msg.version || "") + " is installed. What changed:";
    var ul = $("notes-list");
    ul.innerHTML = "";
    items.forEach(function (t) { var li = document.createElement("li"); li.textContent = String(t); ul.appendChild(li); });
    $("notes").hidden = false;
    // Focus without scrolling: a long list would otherwise open at its end, under the button.
    setTimeout(function () { $("notes-ok").focus({ preventScroll: true }); }, 30);
  }
  function closeNotes() { $("notes").hidden = true; send({ cmd: "notesSeen" }); }

  // ---------- uninstall ----------
  function openUninstall() {
    closeSettings();
    $("uninstall-path").textContent = (S.settings && S.settings.dir) || "";
    $("uninstall-error").hidden = true;
    $("uninstall-go").disabled = false;
    $("uninstall").hidden = false;
    setTimeout(function () { $("uninstall-cancel").focus(); }, 30);
  }
  function closeUninstall() { $("uninstall").hidden = true; }
  function onUninstall(msg) {
    if (msg.status === "done") { closeUninstall(); toast("The game was removed."); return; }
    $("uninstall").hidden = false;
    $("uninstall-go").disabled = false;
    $("uninstall-error").textContent = msg.message || "Uninstalling failed.";
    $("uninstall-error").hidden = false;
  }

  function openSettings() { $("settings").hidden = false; renderSettings(); setTimeout(function () { document.querySelector(".sheet").focus(); }, 30); }
  function closeSettings() { $("settings").hidden = true; }

  // ---------- events ----------
  function bind() {
    // drag the window from the title strip (never from its controls); double-click does nothing
    document.addEventListener("mousedown", function (e) {
      if (e.button !== 0) return;
      var t = e.target;
      if (!t.closest || !t.closest("[data-drag]")) return;
      if (t.closest("button,a,input,select,textarea,[data-nodrag]")) return;
      e.preventDefault();
      send({ cmd: "window", action: "drag" });
    });
    document.addEventListener("dblclick", function (e) { if (e.target.closest && e.target.closest("[data-drag]")) e.preventDefault(); });
    document.addEventListener("dragstart", function (e) { e.preventDefault(); });

    $("btn-min").addEventListener("click", function () { send({ cmd: "window", action: "minimize" }); });
    $("btn-close").addEventListener("click", function () { send({ cmd: "window", action: "close" }); });
    $("btn-settings").addEventListener("click", openSettings);
    $("settings-x").addEventListener("click", closeSettings);
    $("settings-done").addEventListener("click", closeSettings);
    $("settings").addEventListener("mousedown", function (e) { if (e.target === this) closeSettings(); });
    document.addEventListener("keydown", function (e) {
      if (e.key !== "Escape") return;
      if (!$("notes").hidden) closeNotes();
      else if (!$("friends").hidden) closeFriends();
      else if (!$("uninstall").hidden) closeUninstall();
      else if (!$("shortcuts").hidden) answerShortcuts(false);
      else if (!$("settings").hidden) closeSettings();
    });

    $("shortcuts-yes").addEventListener("click", function () { answerShortcuts(true); });
    $("shortcuts-no").addEventListener("click", function () { answerShortcuts(false); });
    $("shortcuts-x").addEventListener("click", function () { answerShortcuts(false); });
    $("set-shortcuts").addEventListener("click", function () { closeSettings(); openShortcuts(); });
    $("notes-ok").addEventListener("click", closeNotes);
    $("notes-x").addEventListener("click", closeNotes);
    $("set-uninstall").addEventListener("click", openUninstall);
    $("uninstall-x").addEventListener("click", closeUninstall);
    $("uninstall-cancel").addEventListener("click", closeUninstall);
    $("uninstall-go").addEventListener("click", function () { this.disabled = true; send({ cmd: "uninstall" }); });

    $("set-open").addEventListener("click", function () { send({ cmd: "openDir" }); });
    $("set-dir-change").addEventListener("click", function () { send({ cmd: "changeDir" }); });
    $("status-change").addEventListener("click", function () {
      if (S.link && S.link.cmd === "friends") openFriends();
      else if (S.link) send({ cmd: S.link.cmd });
      else if (ro3emuMode() && S.phase === "ready") openFriends();
      else send({ cmd: "changeDir" });
    });
    $("set-friends").addEventListener("click", openFriends);
    $("set-server-stop").addEventListener("click", function () { send({ cmd: "stopServer" }); closeSettings(); });
    $("friends-x").addEventListener("click", closeFriends);
    $("friends").addEventListener("mousedown", function (e) { if (e.target === this) closeFriends(); });
    $("host-go").addEventListener("click", function () {
      closeFriends();
      send({ cmd: "host", address: $("host-address").value.trim(), gm: $("host-gm").value.trim() });
    });
    $("join-go").addEventListener("click", function () {
      var a = $("join-address").value.trim();
      if (!a) { $("join-address").focus(); return; }
      closeFriends();
      send({ cmd: "join", address: a });
    });
    $("join-address").addEventListener("keydown", function (e) { if (e.key === "Enter") $("join-go").click(); });
    $("set-repair").addEventListener("click", function () { send({ cmd: "repair" }); closeSettings(); });
    $("feat-more").addEventListener("click", function () { if (featuredUrl) send({ cmd: "openUrl", url: featuredUrl }); });

    $("primary").addEventListener("click", function () {
      if (primaryLock) return;
      if (isTransfer(S.phase)) { pausedByUser = true; send({ cmd: "pause" }); return; }
      primaryLock = true;              // released by the next state message
      setTimeout(function () { primaryLock = false; }, 1500);
      send({ cmd: "primary" });
    });
  }

  // ---------- mock host (plain browser only) ----------
  function createMockHost(mode) {
    var timers = [];
    var Q = new URLSearchParams(location.search);
    var FAST = +Q.get("fast") || 1;   // test aid: ?fast=10 runs the fake transfer 10x faster
    function later(ms, fn) { var t = setTimeout(fn, ms); timers.push(t); return t; }
    function clearAll() { timers.forEach(clearTimeout); timers = []; }
    function emit(m) { setTimeout(function () { handle(JSON.parse(JSON.stringify(m))); }, 0); }

    var scen = {
      install:     { installed: null,    remote: "2026.10.25.1", phase: "install" },
      update:      { installed: "2026.10.17.3", remote: "2026.10.25.1", phase: "update" },
      ready:       { installed: "2026.10.25.1", remote: "2026.10.25.1", phase: "ready" },
      running:     { installed: "2026.10.25.1", remote: "2026.10.25.1", phase: "ready", autoPlay: true },
      repair:      { installed: "2026.10.25.1", remote: "2026.10.25.1", phase: "repair", message: "3 game files differ from the release." },
      error:       { installed: "2026.10.17.3", remote: "2026.10.25.1", phase: "error",
                     message: "ro3_Data/Managed/Assembly-CSharp.dll failed verification. Nothing was changed, so retrying is safe." },
      offline:     { installed: null, remote: null, phase: "offline" },
      downloading: { installed: "2026.10.17.3", remote: "2026.10.25.1", phase: "update", freezeAt: 0.434 },
      installing:  { installed: null,    remote: "2026.10.25.1", phase: "install", freezeAt: 0.276 },
      checking:    { installed: "2026.10.17.3", remote: null, phase: "checking", stay: true }
    }[mode] || null;
    if (!scen) { mode = "update"; scen = { installed: "2026.10.17.3", remote: "2026.10.25.1", phase: "update" }; }

    var m = {
      installed: scen.installed, remote: scen.remote,
      dir: "C:\\Games\\Example Realm\\Client",
      fresh: !scen.installed, owned: true
    };
    var settings = function () {
      return { type: "settings", dir: m.dir, launcher: "1.0.0", serverName: "Example Realm",
        links: Q.get("links") === "0" ? [] : [
          { label: "Website", url: "https://example.com/" },
          { label: "Discord", url: "https://discord.gg/example" }],
        canUninstall: !!m.installed && m.owned,
        panel: Q.get("webpanel") === "0" ? null : { state: "open", url: "https://panel.example.com/" } };
    };
    var state = function (phase, message) {
      var o = { type: "state", phase: phase, installed: m.installed, remote: m.remote };
      if (message) o.message = message;
      if (phase === "install" && !message) o.downloadBytes = 12.84 * GB;
      m.phase = phase;
      emit(o);
    };
    // Sample items for previews only; the launcher shows the repository's news.json.
    var SERVER_NEWS = { type: "news", items: [
      { tag: "Patch", date: "2026-10-28", url: "https://example.com/news/war-of-emperium",
        title: "Guild Wars: commanders take the field",
        text: "Battlefield skills, rally calls and Battle Merit ranks are live in the War of Emperium. Commanders, mark your targets." },
      { tag: "Event", date: "2026-10-24", url: "https://example.com/news/survivor", title: "Survivor Battle season opens Friday" },
      { tag: "News", date: "2026-10-20", url: "https://example.com/news/eddga", title: "Eddga stirs in Payon Forest" }
    ] };

    function boot() {
      emit(settings());
      state("checking");
      if (scen.phase !== "offline" && Q.get("news") !== "0") emit(SERVER_NEWS);
      emit(scen.phase === "offline" ? { type: "server", online: false } : { type: "server", online: true, players: 128 });
      if (Q.get("hero")) emit({ type: "hero", url: Q.get("hero") });
      if (Q.get("first")) later(300, function () { emit({ type: "shortcuts", status: "ask" }); });
      if (Q.get("notes")) later(1200, function () { emit({ type: "notes", version: "2026.10.25.1", items: ["Guild flags and rally points in Mock Battle.", "Bug reports from the game menu go straight to the team.", "The launcher waits in the tray while you play."] }); });
      if (scen.stay) return;
      later(900, function () {
        state(scen.phase, scen.message);
        if (scen.freezeAt != null) later(150, function () { startUpdate(scen.freezeAt); });
        if (scen.autoPlay) later(150, play);
      });
    }

    var FILES = ["ro3_Data/data.unity3d", "ro3_Data/Managed/Assembly-CSharp.dll", "GameAssembly.dll",
      "ro3_Data/resources.assets", "ro3_Data/StreamingAssets/lua.bundle"];

    // the stages of an update, with realistic sizes; time runs at wall clock
    function plan(repair) {
      if (repair) return [
        { stage: "scanning", total: 12.8 * GB, rate: 900 * MB, speed: false },
        { stage: "downloading", total: 3.1 * MB, rate: 9.5 * MB, speed: true },
        { stage: "assembling", total: 41 * MB, rate: 300 * MB, speed: false },
        { stage: "verifying", total: 41 * MB, rate: 380 * MB, speed: false },
        { stage: "committing", total: 0, ms: 700 }];
      if (m.fresh) return [
        { stage: "scanning", total: 0, ms: 600 },
        { stage: "downloading", total: 12.84 * GB, rate: 36 * MB, speed: true },
        { stage: "assembling", total: 12.9 * GB, rate: 520 * MB, speed: false },
        { stage: "verifying", total: 12.9 * GB, rate: 760 * MB, speed: false },
        { stage: "committing", total: 0, ms: 900 }];
      return [
        { stage: "scanning", total: 12.8 * GB, rate: 2.1 * GB, speed: false },
        { stage: "downloading", total: 186 * MB, rate: 12 * MB, speed: true },
        { stage: "assembling", total: 612 * MB, rate: 260 * MB, speed: false },
        { stage: "verifying", total: 612 * MB, rate: 420 * MB, speed: false },
        { stage: "committing", total: 0, ms: 800 }];
    }

    function startUpdate(freezeAt, repair) {
      clearAll();
      var stages = plan(repair), si = 0, done = 0, t0 = Date.now(), avg = 0, fileIx = 0;
      state("updating");
      if (freezeAt != null) {
        var s = stages[1], d = Math.round(s.total * freezeAt), sp = s.rate * 0.983;
        emit({ type: "progress", stage: "downloading", done: d, total: Math.round(s.total),
          rate: Math.round(sp), eta: Math.round((s.total - d) / sp), file: FILES[0] });
        return;
      }
      function tick() {
        var s = stages[si];
        var dt = 0.2;
        if (!s.total) {
          emit({ type: "progress", stage: s.stage, done: 0, total: 0, rate: 0, eta: 0 });
          later(s.ms / FAST, next);
          return;
        }
        var wobble = s.speed ? (1 + 0.18 * Math.sin((Date.now() - t0) / 900) + (Math.random() - 0.5) * 0.12) : 1;
        var rate = s.rate * wobble * FAST;
        done = Math.min(s.total, done + rate * dt);
        avg = avg ? avg * 0.85 + rate * 0.15 : rate;
        if (s.speed && Math.random() < 0.08) fileIx = (fileIx + 1) % FILES.length;
        emit({ type: "progress", stage: s.stage, done: Math.round(done), total: Math.round(s.total),
          rate: s.speed ? Math.round(rate) : 0,
          eta: s.speed ? Math.max(1, Math.round((s.total - done) / avg)) : 0,
          file: s.stage === "scanning" || s.stage === "committing" ? undefined : FILES[fileIx] });
        if (done >= s.total) later(250, next); else later(dt * 1000, tick);
      }
      function next() {
        si++; done = 0; avg = 0;
        if (si >= stages.length) {
          m.installed = m.remote; m.fresh = false;
          state("ready");
          emit(settings());
          return;
        }
        tick();
      }
      tick();
    }

    function play() {
      clearAll();
      state("launching");
      later(1400, function () {
        state("running");
        later(12000, function () { state("ready"); });
      });
    }

    return {
      post: function (c) {
        if (!c || !c.cmd) return;
        if (window.console) console.log("[mock host] <-", JSON.stringify(c));
        switch (c.cmd) {
          case "ready": boot(); break;
          case "primary":
            if (m.phase === "install" || m.phase === "update" || m.phase === "error") startUpdate(null);
            else if (m.phase === "repair") startUpdate(null, true);
            else if (m.phase === "offline") { m.remote = "2026.10.25.1"; state("checking"); later(800, function () { state("install"); emit({ type: "server", online: true, players: 128 }); emit(SERVER_NEWS); }); }
            else if (m.phase === "ready") play();
            break;
          case "pause": clearAll(); state(m.installed ? "update" : "install", "Paused. Downloaded parts are kept; continue any time."); break;
          case "repair": if (m.installed) startUpdate(null, true); break;
          case "openUrl": toast("Opens in your browser: " + c.url); break;
          case "panel": toast("Opens in your browser: https://panel.example.com/"); break;
          case "openDir": toast("Opens " + m.dir); break;
          case "changeDir": m.dir = "E:\\Games\\Example Realm"; emit(settings()); break;
          case "window": if (c.action !== "drag") toast("Window: " + c.action); break;
          case "shortcuts": emit({ type: "shortcuts", status: "done", created: c.desktop || c.startMenu, failed: [] }); break;
          case "notesSeen": break;
          case "uninstall":
            later(700, function () { m.installed = null; m.fresh = true; emit({ type: "uninstall", status: "done" }); emit(settings()); state("install"); });
            break;
        }
      }
    };
  }

  // ---------- mock host, RO3Emu mode (?app=ro3emu&mock=nogame|gameupdate|appupdate|ready|exporting|downloading|running|hosting|error) ----------
  function createRo3EmuMock(mode) {
    var timers = [];
    function later(ms, fn) { timers.push(setTimeout(fn, ms)); }
    function emit(m) { setTimeout(function () { handle(JSON.parse(JSON.stringify(m))); }, 0); }
    var m = { dir: "D:\\Games\\Ragnarok3\\ROO_PC", running: false, kind: null, phase: "checking" };
    function settings() {
      return { type: "settings", mode: "ro3emu", dir: mode === "nogame" ? "" : m.dir, launcher: "1.0.20", serverName: "RO3Emu",
        links: [{ label: "GitHub", url: "https://github.com/aethai/ro3emu-server", icon: "code" }, { label: "Discord", url: "https://discord.gg/vVkcvMFKy3", icon: "chat" }],
        canUninstall: false, lanAddress: "192.168.1.20", serverRunning: m.running, serverKind: m.kind, dataDir: "D:\\RO3Emu\\data",
        panel: panel() };
    }
    // the Web panel entry: the joined server's (found, still looking, or not shared: &webpanel=0), else the local server's
    function panel() {
      if (m.kind === "join") return m.joinPanel ? { state: "open", url: m.joinPanel }
        : m.joinProbing ? { state: "checking", hint: "Looking for the web panel of the server you joined." }
        : { state: "unavailable", hint: "The server you joined has not shared its web panel." };
      return m.running ? { state: "open", url: "http://127.0.0.1:5381" }
        : { state: "start", url: "http://127.0.0.1:5381", hint: "Starts your server on this PC and opens its web panel." };
    }
    function state(phase, message, title, link, remote) {
      m.phase = phase;
      emit({ type: "state", phase: phase, installed: "1.0.20", remote: remote || "1.0.20", message: message, title: title, link: link });
    }
    function server() {
      emit(m.running ? { type: "server", online: true, players: m.kind === "host" ? 3 : 1, label: m.kind === "host" ? "Hosting on 192.168.1.20" : "Your server is running" }
        : { type: "server", online: false, label: "Your server is stopped" });
    }
    var NEWS = { type: "news", items: [
      { tag: "Release", date: "2026-10-29", url: "https://github.com/aethai/ro3emu-server/releases", title: "v1.0.20: War machines and bolt effects",
        text: "The war warehouse and the Tactics panel, cannons you can reserve and build in the siege, and bolt skills show their projectiles again." },
      { tag: "Release", date: "2026-10-28", url: "https://github.com/aethai/ro3emu-server/releases", title: "v1.0.19: Commander tools" },
      { tag: "Release", date: "2026-10-24", url: "https://github.com/aethai/ro3emu-server/releases", title: "v1.0.18: Survivor Battle" }] };
    function ready() { state("ready", "Game 2026.10.25.1. " + (m.running ? "Your server is running." : "Play starts your own server on this PC.")); }
    function exportThenPlay(kind) {
      var stages = [["Reading the game scripts", 4120], ["Exporting the game tables", 1494], ["Exporting the walk maps", 212]], i = 0, done = 0;
      state("working", null, "Getting ready");
      function tick() {
        var s = stages[i];
        done = Math.min(s[1], done + Math.ceil(s[1] / 12));
        emit({ type: "progress", stage: s[0], done: done, total: s[1], unit: "items" });
        if (done < s[1]) return later(120, tick);
        i++; done = 0;
        if (i < stages.length) return later(200, tick);
        emit({ type: "progress", stage: "Starting the server", done: 0, total: 0, unit: "none" });
        emit({ type: "server", online: false, starting: true, label: "Your server is starting" });
        later(1500, function () {
          m.running = true; m.kind = kind; emit(settings()); server();
          state("launching", kind === "host" ? "Friends join with the address 192.168.1.20." : null);
          later(1200, function () { state("running", kind === "host" ? "Friends join with the address 192.168.1.20." : null); });
        });
      }
      tick();
    }
    function download(total, then, title) {
      var done = 0;
      state("updating", null, title);
      (function tick() {
        done = Math.min(total, done + total / 40);
        emit({ type: "progress", stage: "Downloading", done: Math.round(done), total: total, rate: 34 * MB, eta: Math.round((total - done) / (34 * MB)), unit: "bytes" });
        if (done < total) later(150, tick); else later(300, then);
      })();
    }
    function boot() {
      emit(settings());
      state("checking", "Looking for the game...");
      emit(NEWS);
      if (mode === "running" || mode === "hosting") { m.running = true; m.kind = mode === "hosting" ? "host" : "local"; emit(settings()); }
      server();
      later(700, function () {
        switch (mode) {
          case "nogame": state("nogame", "Download it from Genesis' update servers (about 24 GB) into D:\\Games\\Ragnarok3, or pick the folder you have.", "Ragnarok 3 not found", { text: "Pick game folder", cmd: "changeDir" }); break;
          case "gameupdate": state("gameupdate", "This game folder does not fit this RO3Emu: a client file is older than the patch expects. Update it from Genesis' update servers.", "The game needs an update", { text: "Pick another folder", cmd: "changeDir" }); break;
          case "appupdate": state("appupdate", "Only RO3Emu itself is replaced. Your game, characters and settings are kept.", "RO3Emu 1.0.21 is available", { text: "Later", cmd: "later" }, "1.0.21"); break;
          case "exporting": exportThenPlay("local"); break;
          case "downloading": state("updating", null, null); emit({ type: "progress", stage: "Downloading", done: Math.round(9.4 * GB), total: Math.round(23.7 * GB), rate: 38 * MB, eta: 381, unit: "bytes" }); break;
          case "running": state("running"); break;
          case "hosting": state("running", "Friends join with the address 192.168.1.20."); break;
          case "error": state("error", "Port 5380 is already used by another program, so the server cannot start. Close the other program (or another RO3Emu window)."); break;
          default: ready();
        }
      });
    }
    return {
      post: function (c) {
        if (!c || !c.cmd) return;
        if (window.console) console.log("[mock host] <-", JSON.stringify(c));
        switch (c.cmd) {
          case "ready": boot(); break;
          case "primary":
            if (m.phase === "ready") exportThenPlay("local");
            else if (m.phase === "nogame" || m.phase === "gameupdate") download(23.7 * GB, function () { mode = "ready"; emit(settings()); ready(); });
            else if (m.phase === "appupdate") download(96 * MB, function () { toast("RO3Emu restarts into the new version"); });
            else if (m.phase === "error") ready();
            break;
          case "later": ready(); break;
          case "host": exportThenPlay("host"); break;
          case "join":
            state("working", null, "Contacting http://" + c.address + ":5380");
            later(1200, function () {
              m.kind = "join"; m.joinPanel = null; m.joinProbing = true; emit(settings());
              state("running", "Playing on http://" + c.address + ":5380.");
              later(900, function () {
                m.joinProbing = false;
                m.joinPanel = new URLSearchParams(location.search).get("webpanel") === "0" ? null : "http://" + c.address + ":5381";
                emit(settings());
              });
            });
            break;
          case "panel":
            var pn = panel();
            if (pn.state === "open") toast("Opens in your browser: " + pn.url);
            else if (pn.state === "start" && m.phase === "ready") { m.running = true; m.kind = "local"; emit(settings()); server(); ready(); toast("Opens in your browser: " + pn.url); }
            else if (pn.state === "start") toast("Your server is not running. Press Play to start it, then open the web panel.");
            break;
          case "stopServer": m.running = false; m.kind = null; emit(settings()); server(); ready(); break;
          case "pause": timers.forEach(clearTimeout); state(m.phase === "updating" ? "nogame" : m.phase, "Paused. Downloaded parts are kept; continue any time.", "Download paused"); break;
          case "openUrl": toast("Opens in your browser: " + c.url); break;
          case "changeDir": toast("Opens the folder picker"); break;
          case "openDir": toast("Opens " + m.dir); break;
          case "window": if (c.action !== "drag") toast("Window: " + c.action); break;
          case "shortcuts": emit({ type: "shortcuts", status: "done", created: c.desktop || c.startMenu, failed: [] }); break;
        }
      }
    };
  }

  // ---------- start ----------
  function start() {
    var params = new URLSearchParams(location.search);
    var wv = window.chrome && window.chrome.webview;
    if (wv) {
      host = { post: function (o) { wv.postMessage(o); } };
      wv.addEventListener("message", function (e) { handle(e.data); });
      document.addEventListener("contextmenu", function (e) { if (!e.target.closest("input")) e.preventDefault(); });
    } else {
      host = params.get("app") === "ro3emu" ? createRo3EmuMock(params.get("mock") || "ready") : createMockHost(params.get("mock") || "update");
    }
    bind();
    renderState();
    renderServer();
    renderSettings();
    send({ cmd: "ready" });
    if (params.get("panel") === "settings") openSettings();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
