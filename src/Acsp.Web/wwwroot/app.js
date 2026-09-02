/* ACSP web UI — vanilla JS, no dependencies. */
const $ = id => document.getElementById(id);
const NS = "http://www.w3.org/2000/svg";
const fmt = n => n == null ? "—" : Math.round(n).toLocaleString("en-US");
const fmt1 = n => n == null ? "—" : n.toLocaleString("en-US", { maximumFractionDigits: 1 });
const pct = n => n == null ? "—" : (100 * n).toFixed(2) + " %";

let currentJob = null;
let series = []; // {t, incumbent, bound}
let upload = null; // {uploadId, name} when an Excel instance is active
let liveRounds = []; // rounds streamed while a design job runs
let worldPolys = []; // simplified country outlines [[lon,lat],...]
let tsSelectedStation = null; // time-space: highlight flights touching this airport id
let mapLegEls = [];           // map legs with their times, for the hourly scrubber
let mapPeriodN = 10080;

init();

async function init() {
  fetch("world.json").then(r => r.json()).then(w => { worldPolys = w; }).catch(() => {});
  const profiles = await (await fetch("/api/profiles")).json();
  for (const a of profiles.airlines) {
    const o = document.createElement("option");
    o.value = a.code; o.textContent = `${a.code} — ${a.name}`;
    $("airline").appendChild(o);
  }
  $("solveBtn").onclick = () => requestRun("solve");
  $("designBtn").onclick = () => requestRun("design");
  $("reviewCancel").onclick = closeReview;
  $("reviewOverlay").onclick = e => { if (e.target === $("reviewOverlay")) closeReview(); };
  $("printBtn").onclick = () => window.print();
  fetch("/api/config").then(r => r.json()).then(c => {
    CONFIG = c;
    $("configLine").innerHTML =
      `Environment: LP backend <b>${c.lpBackend.toUpperCase()}</b> · .NET ${c.dotnet}` +
      ` · override with <code>ACSP_LP_BACKEND=highs|cplex</code>`;
  }).catch(() => {});
  $("cancelBtn").onclick = () => currentJob && fetch(`/api/jobs/${currentJob}/cancel`, { method: "POST" });
  $("proposeBtn").onclick = proposeAndResolve;
  $("inputBtn").onclick = showInput;
  $("inputCloseBtn").onclick = () => $("inputSection").classList.add("hidden");
  $("uploadBtn").onclick = () => $("uploadFile").click();
  $("uploadFile").onchange = uploadExcel;
  $("uploadCloseBtn").onclick = () => $("uploadPanel").classList.add("hidden");
  $("sourceClear").onclick = () => setUpload(null);
  for (const id of ["airline", "set", "seed"])
    $(id).onchange = updateTemplateLink;
  updateTemplateLink();
  $("mapHourMode").onchange = () => {
    $("mapHour").disabled = !$("mapHourMode").checked;
    applyMapHour();
  };
  $("mapHour").oninput = applyMapHour;
  $("savedSolutions").onchange = async e => {
    if (!e.target.value) return;
    const sol = await (await fetch(`/api/solutions/${e.target.value}`)).json();
    renderSolution(sol);
    setWs("planner");
  };
  document.querySelectorAll("#wsTabs button").forEach(b => b.onclick = () => setWs(b.dataset.ws));
  setWs(localStorage.getItem("acspWs") || "planner");
  refreshSaved();
  // a reload should not orphan a running optimization: re-attach to it
  try {
    const jobs = await (await fetch("/api/jobs")).json();
    const running = jobs.find(j => j.status === "running");
    if (running) followJob(running);
  } catch {}
}

/* Workspace tabs: Planner / OR Engineer / Data. Panels carry ws-p / ws-e / ws-d classes;
   the body attribute decides which family is visible (pure CSS filtering). */
function setWs(ws) {
  document.body.dataset.ws = ws;
  localStorage.setItem("acspWs", ws);
  document.querySelectorAll("#wsTabs button")
    .forEach(b => b.classList.toggle("on", b.dataset.ws === ws));
}

/* Header status chip: the one place a running job is always visible. */
function chip(state, text) {
  const c = $("jobChip");
  c.className = "chip " + state;
  $("jobChipText").textContent = text;
}

/* ------------------------------------------------- pre-run review & sanity checks */
let CONFIG = null;
let instCache = { key: null, data: null };

async function fetchInstanceSummary() {
  const req = formRequest();
  const key = upload ? "u:" + upload.uploadId : `${req.airline}|${req.set}|${req.seed}`;
  if (instCache.key === key) return instCache.data;
  const d = await (await fetch(upload
    ? `/api/instance?uploadId=${upload.uploadId}`
    : `/api/instance?airline=${req.airline}&set=${req.set}&seed=${req.seed}`)).json();
  instCache = { key, data: d };
  return d;
}

/* Open the review modal (unless the user opted out), then run. */
async function requestRun(mode) {
  if (localStorage.getItem("acspSkipReview") === "1") {
    return mode === "solve" ? startSolve() : startDesign();
  }
  const btn = mode === "solve" ? $("solveBtn") : $("designBtn");
  btn.disabled = true;
  let d;
  try { d = await fetchInstanceSummary(); }
  catch { btn.disabled = false; return mode === "solve" ? startSolve() : startDesign(); }
  btn.disabled = false;

  const s = d.summary, req = formRequest();
  $("reviewTitle").textContent = mode === "solve"
    ? `Review — solve ${d.name}` : `Review — auto-design from ${d.name}`;
  const cells = [
    ["Airports", `${s.airports} (${s.hubs} hubs)`],
    ["Aircraft", `${s.aircraft} · ${s.fleets} fleets`],
    ["Flights", `${s.mandatory} mand + ${s.optional} opt`],
    ["O&Ds", s.ods],
    ["Demand", fmt1(s.demandT) + " t"],
    ["Handling", (d.cargoHandlingMinutes ?? 0) + " min"],
  ];
  $("reviewSummary").innerHTML = cells.map(([l, v]) =>
    `<div class="kpi"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");

  const checks = [];
  const add = (lvl, txt) => checks.push([lvl, txt]);
  if (s.aircraft === 0) add("error", "No aircraft in the instance — nothing can fly.");
  if (s.demandT === 0) add("error", "No demand — the optimum is to fly nothing.");
  const flights = s.mandatory + s.optional;
  const minBudget = Math.max(20, Math.round(flights / 12));
  if (req.timeLimitSeconds < minBudget)
    add("warn", `Time limit ${req.timeLimitSeconds}s may be too small for a first feasible schedule ` +
      `at this size — rule of thumb: at least ~${minBudget}s (flights ÷ 12).`);
  else add("ok", `Time budget ${req.timeLimitSeconds}s is reasonable for ${flights} flights.`);
  if (req.maintenance && flights > 800)
    add("warn", "Maintenance on a large instance: string pricing becomes approximate and slower (σ label limit).");
  if (mode === "solve" && s.optional === 0)
    add("info", "No optional flights: this is pure fleet assignment + rotations + cargo routing (no flight selection).");
  if (mode === "design") {
    const batch = +$("designBatch").value;
    if (batch > 400)
      add("warn", `Batch ${batch} exceeds what the restricted-master MIP typically digests (~300–400) — flat rounds likely. ` +
        "Rule of thumb: ~20–25% of the base cargo flights.");
    else add("ok", `Batch ${batch} within the digestible range; up to ${$("designRounds").value} rounds.`);
  }
  if (d.deliverAll)
    add("info", "Deliver-all commitment: demand rows are equalities; the model minimizes the contracting bill.");
  const curfews = (d.airports || []).filter(a => a.curfew).length;
  if (curfews) add("info", `${curfews} airports enforce night curfews (no arrivals in the window).`);
  $("reviewChecks").innerHTML = checks.map(([lvl, t]) => `<li class="${lvl}">${t}</li>`).join("");

  $("reviewConfig").textContent =
    `mode=${mode}  time-limit=${req.timeLimitSeconds}s  maintenance=${req.maintenance ? "on" : "off"}` +
    (mode === "design" ? `  batch=${$("designBatch").value}  rounds=${$("designRounds").value}` : "") +
    `  gap-target=0.5%  lp-backend=${CONFIG ? CONFIG.lpBackend : "?"}` +
    (upload ? `  source=Excel:${upload.name}` : "");

  $("reviewRun").onclick = () => {
    if ($("reviewSkip").checked) localStorage.setItem("acspSkipReview", "1");
    closeReview();
    mode === "solve" ? startSolve() : startDesign();
  };
  $("reviewOverlay").classList.remove("hidden");
  $("reviewRun").focus();
}
function closeReview() { $("reviewOverlay").classList.add("hidden"); }

async function refreshSaved() {
  const list = await (await fetch("/api/solutions")).json();
  const sel = $("savedSolutions");
  while (sel.options.length > 1) sel.remove(1);
  for (const s of list) {
    const o = document.createElement("option");
    o.value = s.name; o.textContent = s.name;
    sel.appendChild(o);
  }
}

const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const fmtTime = t =>
  `${DAYS[Math.floor(t / 1440) % 7]} ${String(Math.floor(t % 1440 / 60)).padStart(2, "0")}:${String(t % 60).padStart(2, "0")}`;

/* Hourly scrubber on the network map: show only legs airborne (>= 1 min) in the chosen hour. */
function applyMapHour() {
  const hourly = $("mapHourMode").checked;
  if (!hourly) {
    $("mapHourLabel").textContent = "all week (aggregated)";
    for (const { el, baseOpacity } of mapLegEls) el.setAttribute("opacity", baseOpacity);
    return;
  }
  const h0 = +$("mapHour").value * 60, h1 = h0 + 60;
  $("mapHourLabel").textContent =
    `${fmtTime(h0)} – ${String(Math.floor((h1 % 1440) / 60)).padStart(2, "0")}:00`;
  let airborne = 0;
  for (const { el, dep, arrAbs, baseOpacity } of mapLegEls) {
    // overlap of [dep, arrAbs) with the hour window, also shifted a week for wrap-around legs
    const ov = Math.max(
      Math.min(arrAbs, h1) - Math.max(dep, h0),
      Math.min(arrAbs, h1 + mapPeriodN) - Math.max(dep, h0 + mapPeriodN));
    const on = ov >= 1;
    if (on) airborne++;
    el.setAttribute("opacity", on ? Math.max(0.85, +baseOpacity) : 0);
  }
  $("mapHourLabel").textContent += `  (${airborne} legs airborne)`;
}

/* Model-input inspection screen: everything the optimizer receives. */
async function showInput() {
  const req = formRequest();
  $("inputBtn").disabled = true;
  try {
    const d = await (await fetch(upload
      ? `/api/instance?uploadId=${upload.uploadId}`
      : `/api/instance?airline=${req.airline}&set=${req.set}&seed=${req.seed}`)).json();
    $("inputSection").classList.remove("hidden");
    setWs("data");
    $("inputName").textContent = d.name +
      ` — weekly periodic schedule, ${d.periodMinutes.toLocaleString()} min horizon`;
    const s = d.summary;
    const kpis = [
      ["Airports", `${s.airports} (${s.hubs} hubs)`],
      ["Fleet types", s.fleets], ["Aircraft", s.aircraft],
      ["Mandatory flights", s.mandatory], ["Optional flights", s.optional],
      ["External flights", s.external], ["Legs", s.legs],
      ["O&Ds", s.ods], ["Total demand", fmt1(s.demandT) + " t"],
    ];
    $("inputSummary").innerHTML = kpis.map(([l, v]) =>
      `<div class="kpi"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");

    $("inputFleets").innerHTML =
      "<tr><th>fleet</th><th>a/c</th><th>max t</th><th>max m³</th><th>range km (full)</th>" +
      "<th>range max km</th><th>payload @ max t</th>" +
      "<th>fixed $/a-c/wk</th><th>ground min</th><th>mnt cycles</th><th>mnt flight h</th>" +
      "<th>mnt elapsed d</th><th>mnt stop h</th></tr>" +
      d.fleets.map(k => `<tr><td>${k.code}</td><td>${k.count}</td><td>${k.maxWeightT}</td>
        <td>${k.maxVolM3}</td><td>${fmt(k.rangeKm)}</td>
        <td>${k.rangeMaxKm ? fmt(k.rangeMaxKm) : "—"}</td>
        <td>${k.payloadAtMaxRangeT != null ? fmt1(k.payloadAtMaxRangeT) : "—"}</td>
        <td>${fmt(k.fixedPerAircraftWeek)}</td>
        <td>${k.groundMin}</td><td>${k.mntCycles}</td><td>${k.mntFlightH}</td>
        <td>${k.mntElapsedDays}</td><td>${k.mntDurationH}</td></tr>`).join("");

    $("inputAirports").innerHTML =
      "<tr><th>code</th><th>name</th><th>hub</th><th>min transfer</th><th>transfer $/t</th>" +
      "<th>storage $/t/h</th><th>curfew (no arrivals)</th></tr>" +
      d.airports.map(a => `<tr${a.hub ? "" : ' class=""'}><td>${a.code}</td><td>${a.name}</td>
        <td>${a.hub ? "yes" : ""}</td><td>${a.hub ? a.minTransferMin + " min" : "—"}</td>
        <td>${a.hub ? a.transferCostPerT : "—"}</td><td>${a.hub ? a.storagePerTHour : "—"}</td>
        <td>${a.curfew || "open 24h"}</td></tr>`).join("");

    const renderFlights = () => {
      const q = $("flightFilter").value.trim().toUpperCase();
      const rows = d.flights.filter(f => !q ||
        f.code.toUpperCase().includes(q) || f.route.includes(q) || f.kind.toUpperCase().includes(q));
      $("inputFlights").innerHTML =
        `<tr><th>flight</th><th>kind</th><th>route</th><th>dep</th><th>arr</th>` +
        `<th>legs</th><th>km</th><th>fixed cost $</th><th>cap t</th></tr>` +
        rows.slice(0, 1200).map(f => `<tr${f.kind === "optional" ? ' class="dim"' : ""}>
          <td>${f.code}</td><td>${f.kind}</td><td>${f.route}</td>
          <td>${fmtTime(f.dep)}</td><td>${fmtTime(f.arr)}</td><td>${f.legs}</td>
          <td>${fmt(f.km)}</td><td>${fmt(f.minFixedCost)}</td>
          <td>${f.capT != null ? f.capT : "fleet"}</td></tr>`).join("") +
        (rows.length > 1200 ? `<tr><td colspan="9">… ${rows.length - 1200} more (use the filter)</td></tr>` : "");
    };
    const renderOds = () => {
      const q = $("odFilter").value.trim().toUpperCase();
      const rows = d.ods.filter(o => !q || o.from.includes(q) || o.to.includes(q));
      $("inputOds").innerHTML =
        `<tr><th>#</th><th>O&D</th><th>available</th><th>deadline h</th>` +
        `<th>weight t</th><th>vol m³</th><th>rate $/t</th></tr>` +
        rows.slice(0, 1200).map(o => `<tr><td>${o.id}</td><td>${o.from} → ${o.to}</td>
          <td>${fmtTime(o.avail)}</td><td>${o.deadlineH}</td><td>${fmt1(o.weightT)}</td>
          <td>${fmt1(o.volM3)}</td><td>${fmt(o.ratePerT)}</td></tr>`).join("") +
        (rows.length > 1200 ? `<tr><td colspan="7">… ${rows.length - 1200} more (use the filter)</td></tr>` : "");
    };
    $("flightFilter").oninput = renderFlights;
    $("odFilter").oninput = renderOds;
    renderFlights();
    renderOds();
    $("inputSection").scrollIntoView({ block: "start" });
  } finally { $("inputBtn").disabled = false; }
}

function formRequest() {
  return {
    airline: $("airline").value,
    set: +$("set").value,
    seed: +$("seed").value,
    maintenance: $("maintenance").checked,
    regional: $("regional").checked,
    pairs: $("regionalPairs").checked,
    timeLimitSeconds: +$("timeLimit").value,
    gapTarget: 0.005,
    uploadId: upload?.uploadId ?? null,
  };
}

async function startSolve() {
  const job = await (await fetch("/api/solve", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify(formRequest()),
  })).json();
  followJob(job);
}

/* ------------------------------------------------------ Excel upload workflow */

function updateTemplateLink() {
  $("templateBtn").href = upload
    ? `/api/template.xlsx?uploadId=${upload.uploadId}`
    : `/api/template.xlsx?airline=${$("airline").value}&set=${$("set").value}&seed=${$("seed").value}`;
}

function setUpload(u) {
  upload = u;
  $("sourceBadge").classList.toggle("hidden", !u);
  if (u) $("sourceName").textContent = u.name;
  for (const id of ["airline", "set", "seed"]) $(id).disabled = !!u;
  updateTemplateLink();
}

async function uploadExcel(e) {
  const file = e.target.files[0];
  e.target.value = "";
  if (!file) return;
  const res = await (await fetch(`/api/upload?name=${encodeURIComponent(file.name)}`, {
    method: "POST", body: file,
  })).json();
  $("uploadPanel").classList.remove("hidden");
  setWs("data");
  const msgs = res.messages || [];
  $("uploadMessages").innerHTML = msgs.length
    ? "<tr><th>severity</th><th>sheet</th><th>row</th><th>message</th></tr>" +
      msgs.map(mfe => `<tr${mfe.severity === "error" ? "" : ' class="dim"'}>
        <td>${mfe.severity}</td><td>${mfe.sheet}</td><td>${mfe.row || ""}</td><td>${mfe.text}</td></tr>`).join("")
    : "";
  if (!res.ok) {
    setUpload(null);
    $("uploadSummary").textContent =
      `❌ ${file.name} rejected: ${msgs.filter(mfe => mfe.severity === "error").length} error(s). ` +
      "Fix them in Excel and upload again.";
    return;
  }
  setUpload({ uploadId: res.uploadId, name: res.name });
  const s = res.summary;
  $("uploadSummary").textContent =
    `✓ ${res.name}: ${s.airports} airports, ${s.fleets} fleets (${s.aircraft} aircraft), ` +
    `${s.mandatory} mandatory + ${s.optional} optional + ${s.external} external flights, ` +
    `${s.ods} O&Ds (${fmt1(s.demandT)} t)` +
    (msgs.length ? ` — ${msgs.length} warning(s) below` : "") +
    ". Solve, Auto-design and View input now use this instance.";
  $("uploadPanel").scrollIntoView({ block: "nearest" });
}

/* --------------------------------------------------- autonomous design mode */

async function startDesign() {
  const req = formRequest();
  const job = await (await fetch("/api/design", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      airline: req.airline, set: req.set, seed: req.seed, maintenance: req.maintenance,
      regional: req.regional, pairs: req.pairs,
      roundTimeLimitSeconds: req.timeLimitSeconds, gapTarget: req.gapTarget,
      uploadId: req.uploadId,
      batch: +$("designBatch").value, maxRounds: +$("designRounds").value,
      stopThreshold: 0.003, evictAfter: 2,
    }),
  })).json();
  liveRounds = [];
  $("designPanel").classList.remove("hidden");
  $("designStatus").textContent = " running…";
  $("designSummary").textContent = "round 0: solving the base schedule";
  $("designRoundsChart").innerHTML = "";
  $("designProposals").innerHTML = "";
  followJob(job);
}

function designPhase(e) {
  if (e.phase === "proposing")
    $("designSummary").textContent =
      `round ${e.round}: proposing candidate flights and re-optimizing…`;
  else if (e.phase === "round-done" && !liveRounds.includes(e.round)) {
    liveRounds.push(e.round);
    $("designSummary").textContent = `round ${e.round} finished, ` +
      (e.round < +$("designRounds").value ? `starting round ${e.round + 1}…` : "wrapping up…");
  }
}

function renderDesign(d) {
  $("designPanel").classList.remove("hidden");
  $("designStatus").textContent = " " + d.stopReason;
  const best = d.rounds.find(r => r.round === d.bestRound);
  const delta = best.profit - d.baseProfit;
  const accepted = d.proposals.filter(p => p.status === "accepted");
  $("designSummary").textContent =
    `base profit $${fmt(d.baseProfit)} → best $${fmt(best.profit)} at round ${d.bestRound} ` +
    `(${delta >= 0 ? "+" : ""}${fmt(delta)}, ${(100 * delta / Math.abs(d.baseProfit)).toFixed(1)} %). ` +
    `${d.proposals.length} candidate flights tried, ${accepted.length} accepted into the network.`;

  const profits = d.rounds.map(r => r.profit).filter(v => v != null);
  const pMin = Math.min(...profits, d.baseProfit), pMax = Math.max(...profits, d.baseProfit);
  const H = v => pMax === pMin ? 60 : 8 + 72 * (v - pMin) / (pMax - pMin);
  $("designRoundsChart").innerHTML = d.rounds.map(r => {
    const cls = r.round === 0 ? "base" : r.round === d.bestRound ? "best" : "";
    const tip = `round ${r.round}: profit $${fmt(r.profit)}, gap ${r.gap != null ? (100 * r.gap).toFixed(2) + "%" : "—"}, ` +
      `${r.flights} flights (+${r.added} proposed, ${r.flown} flown, ${r.evicted} evicted), ${r.seconds}s — ${r.note}`;
    return `<div class="rbar ${cls}" title="${tip}">
      <div class="fill" style="height:${H(r.profit).toFixed(0)}px"></div>
      <div>${r.round === 0 ? "base" : "r" + r.round}</div></div>`;
  }).join("");

  $("designRoundsTable").innerHTML =
    "<tr><th>round</th><th>profit $</th><th>Δ</th><th>gap</th><th>flights</th>" +
    "<th>proposed</th><th>flown</th><th>evicted</th><th>time s</th><th>note</th></tr>" +
    d.rounds.map((r, i) => {
      const prev = i > 0 ? d.rounds[i - 1].profit : null;
      const delta = prev != null && r.profit != null
        ? (100 * (r.profit - prev) / Math.abs(prev)).toFixed(2) + " %" : "—";
      return `<tr${r.round === d.bestRound ? ' style="font-weight:bold"' : ""}>
        <td>${r.round === 0 ? "base" : "r" + r.round}</td>
        <td>${fmt(r.profit)}</td><td>${delta}</td>
        <td>${r.gap != null ? (100 * r.gap).toFixed(2) + " %" : "—"}</td>
        <td>${r.flights}</td><td>${r.added ? "+" + r.added : "—"}</td>
        <td>${r.flown || "—"}</td><td>${r.evicted || "—"}</td>
        <td>${fmt1(r.seconds)}</td><td>${r.note}</td></tr>`;
    }).join("");

  const order = { accepted: 0, testing: 1, evicted: 2 };
  const rows = [...d.proposals].sort((a, b) =>
    (order[a.status] - order[b.status]) || b.targetTonnes - a.targetTonnes);
  $("designProposals").innerHTML =
    "<tr><th>flight</th><th>route</th><th>hub departure</th><th>for</th><th>target t</th>" +
    "<th>reason</th><th>round</th><th>status</th></tr>" +
    rows.map(p => `<tr${p.status === "evicted" ? ' class="dim"' : ""}>
      <td>${p.code}</td><td>${p.route.join(" → ")}</td><td>${fmtTime(p.depMinute)}</td>
      <td>${p.targetPair}</td><td>${fmt1(p.targetTonnes)}</td><td>${p.reason}</td>
      <td>r${p.addedRound}${p.evictedRound ? "–r" + p.evictedRound : ""}</td>
      <td><span class="st st-${p.status}">${p.status}</span></td></tr>`).join("");
}

async function proposeAndResolve() {
  $("proposeBtn").disabled = true;
  try {
    const data = await (await fetch("/api/propose", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(formRequest()),
    })).json();
    renderProposals(data);
    followJob(data);
  } finally { $("proposeBtn").disabled = false; }
}

function renderProposals(data) {
  $("proposalsPanel").classList.remove("hidden");
  $("proposalsSummary").textContent =
    `unroutable demand: ${data.unservableBefore} O&Ds (${fmt1(data.tonnesBefore)} t) → ` +
    `after proposals: ${data.unservableAfter} O&Ds (${fmt1(data.tonnesAfter)} t) still unreachable. ` +
    `The ${data.proposals.length} proposed flights enter as OPTIONAL: the optimizer decides.`;
  $("proposalsTable").innerHTML =
    "<tr><th>flight</th><th>route</th><th>hub departure</th><th>for</th><th>target t</th><th>reason</th></tr>" +
    data.proposals.map(p => {
      const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
      const t = `${days[Math.floor(p.depMinute / 1440) % 7]} ${String(Math.floor(p.depMinute % 1440 / 60)).padStart(2, "0")}:${String(p.depMinute % 60).padStart(2, "0")}`;
      return `<tr><td>${p.code}</td><td>${p.route.join(" → ")}</td><td>${t}</td>
        <td>${p.targetPair}</td><td>${fmt1(p.targetTonnes)}</td><td>${p.reason}</td></tr>`;
    }).join("");
}

function followJob(job) {
  currentJob = job.id;
  series = [];
  $("jobName").textContent = job.instance;
  $("emptyState").classList.add("hidden");
  $("progressSection").classList.remove("hidden");
  $("dashboard").classList.add("hidden");
  if (document.body.dataset.ws === "data") setWs("planner");
  chip("running", "starting · " + job.instance);
  const es = new EventSource(`/api/jobs/${job.id}/events`);
  es.onmessage = async ev => {
    const e = JSON.parse(ev.data);
    if (e.type === "design-phase") {
      designPhase(e);
      chip("running", `design round ${e.round} · ${e.phase === "proposing" ? "proposing" : "done"}`);
    } else if (e.type === "progress") {
      $("progressStats").textContent =
        `t ${e.t}s   nodes ${e.nodes}   incumbent ${fmt(e.incumbent)}   ` +
        `bound ${fmt(e.bound)}   gap ${e.gap != null ? pct(e.gap) : "—"}   ` +
        `columns ${e.cols}   cuts ${e.cuts}   ${e.phase}`;
      chip("running", `${e.phase || "solving"} · gap ${e.gap != null ? pct(e.gap) : "—"} · ${e.t}s`);
      if (e.incumbent != null || e.bound != null)
        series.push({ t: e.t, incumbent: e.incumbent, bound: e.bound });
      drawConvergence();
    } else if (e.type === "status") {
      es.close();
      if (e.status === "done") {
        const sol = await (await fetch(`/api/jobs/${currentJob}/result`)).json();
        renderSolution(sol);
        refreshSaved();
      } else {
        $("progressStats").textContent = "no solution: " + (e.error || "error");
        chip("error", e.error ? "failed: " + e.error : "failed");
      }
    }
  };
}

function svgEl(parent, tag, attrs, text) {
  const el = document.createElementNS(NS, tag);
  for (const k in attrs) el.setAttribute(k, attrs[k]);
  if (text != null) el.textContent = text;
  parent.appendChild(el);
  return el;
}

/* Wheel-zoom + drag-pan on an SVG via its viewBox. Double click resets. */
function makeZoomable(svg) {
  if (svg.dataset.zoomable) return;
  svg.dataset.zoomable = "1";
  svg.style.cursor = "grab";
  const pt = e => {
    const p = svg.createSVGPoint();
    p.x = e.clientX; p.y = e.clientY;
    return p.matrixTransform(svg.getScreenCTM().inverse());
  };
  svg.addEventListener("wheel", e => {
    e.preventDefault();
    const vb = svg.viewBox.baseVal;
    const k = e.deltaY > 0 ? 1.18 : 1 / 1.18;
    const c = pt(e);
    vb.x = c.x - (c.x - vb.x) * k;
    vb.y = c.y - (c.y - vb.y) * k;
    vb.width *= k; vb.height *= k;
  }, { passive: false });
  // capture the pointer only once a real drag starts, so plain clicks still
  // reach child elements (e.g. clickable station labels)
  let dragging = false, captured = false;
  svg.addEventListener("pointerdown", e => {
    dragging = true;
    captured = false;
    svg.dataset.moved = "";
  });
  svg.addEventListener("pointermove", e => {
    if (!dragging) return;
    if (Math.abs(e.movementX) + Math.abs(e.movementY) > 2) {
      svg.dataset.moved = "1";
      if (!captured) {
        svg.setPointerCapture(e.pointerId);
        svg.style.cursor = "grabbing";
        captured = true;
      }
    }
    if (!captured) return;
    const vb = svg.viewBox.baseVal;
    const scale = vb.width / svg.clientWidth;
    vb.x -= e.movementX * scale;
    vb.y -= e.movementY * scale;
  });
  svg.addEventListener("pointerup", e => {
    dragging = false;
    if (captured) svg.releasePointerCapture(e.pointerId);
    captured = false;
    svg.style.cursor = "grab";
  });
  svg.addEventListener("dblclick", () => {
    const vb = svg.viewBox.baseVal;
    const o = svg.dataset.vb0.split(" ").map(Number);
    vb.x = o[0]; vb.y = o[1]; vb.width = o[2]; vb.height = o[3];
  });
}
function rememberViewBox(svg) {
  svg.dataset.vb0 = svg.getAttribute("viewBox");
  const o = svg.dataset.vb0.split(" ").map(Number);
  const vb = svg.viewBox.baseVal;
  vb.x = o[0]; vb.y = o[1]; vb.width = o[2]; vb.height = o[3];
}

function drawConvergence() {
  const svg = $("convergence");
  svg.innerHTML = "";
  if (series.length < 2) return;
  const W = 860, H = 200, m = { l: 70, r: 10, t: 10, b: 20 };
  const tMax = series[series.length - 1].t || 1;
  const vals = series.flatMap(s => [s.incumbent, s.bound]).filter(v => v != null);
  if (!vals.length) return;
  let vMin = Math.min(...vals), vMax = Math.max(...vals);
  if (vMax === vMin) { vMax += 1; vMin -= 1; }
  const pad = (vMax - vMin) * 0.08; vMin -= pad; vMax += pad;
  const X = t => m.l + (W - m.l - m.r) * t / tMax;
  const Y = v => m.t + (H - m.t - m.b) * (1 - (v - vMin) / (vMax - vMin));
  for (let i = 0; i <= 4; i++) {
    const v = vMin + (vMax - vMin) * i / 4;
    svgEl(svg, "line", { x1: m.l, x2: W - m.r, y1: Y(v), y2: Y(v), stroke: "#dddddd", "stroke-width": .5 });
    svgEl(svg, "text", { x: m.l - 6, y: Y(v) + 4, fill: "#647084", "font-size": 10, "text-anchor": "end" }, fmt(v));
  }
  const line = (key, color) => {
    const pts = series.filter(s => s[key] != null).map(s => `${X(s.t)},${Y(s[key])}`);
    if (pts.length > 1)
      svgEl(svg, "polyline", { points: pts.join(" "), fill: "none", stroke: color, "stroke-width": 1.6 });
  };
  line("bound", "#c03434");
  line("incumbent", "#17805c");
  svgEl(svg, "text", { x: W - 12, y: 14, fill: "#c03434", "font-size": 10, "text-anchor": "end" }, "upper bound");
  svgEl(svg, "text", { x: W - 12, y: 26, fill: "#17805c", "font-size": 10, "text-anchor": "end" }, "incumbent");
}

// ------------------------------------------------------------------ dashboard

function renderSolution(sol) {
  $("dashboard").classList.remove("hidden");
  $("emptyState").classList.add("hidden");
  chip("done", `done · profit $${fmt(sol.pnl.profit)} · gap ${pct(sol.stats.gap)}`);
  const solName = (sol.design ? sol.instance.replace("+prop", "") + "+design" : sol.instance)
    + (sol.withMaintenance ? "-mnt" : "");
  $("xlsxBtn").href = `/api/solutions/${encodeURIComponent(solName)}/itinerary.xlsx`;
  if (sol.design) renderDesign(sol.design);
  else $("designPanel").classList.add("hidden");
  renderKpis(sol);
  renderMap(sol);
  renderTimeSpace(sol);
  renderGantt(sol);
  renderOdTable(sol);
  renderFlightTable(sol);
  renderReport(sol);
}

/* ------------------------------------------------------------------ report */

function renderReport(sol) {
  $("reportEmpty").classList.add("hidden");
  $("reportSection").classList.remove("hidden");
  const pnl = sol.pnl;
  const shipped = sol.ods.reduce((a, o) => a + o.shippedT, 0);
  const demand = sol.ods.reduce((a, o) => a + o.demandT, 0);
  const served = sol.ods.filter(o => o.shippedT > 1e-6).length;
  const totalCosts = pnl.variableCosts + pnl.fixedFlightCosts + pnl.aircraftCosts +
    pnl.externalCosts + (pnl.contractedCosts || 0);
  const aircraftUsed = sol.rotations.reduce((a, r) => a + r.aircraft, 0);
  const aircraftOwned = sol.fleets.reduce((a, f) => a + f.count, 0);

  $("reportTitle").textContent = `Weekly network plan — ${sol.instance}`;
  $("reportMeta").textContent =
    `${new Date().toISOString().slice(0, 10)} · gap ${pct(sol.stats.gap)} · ` +
    `${fmt(sol.stats.nodes)} B&B nodes · ${fmt1(sol.stats.seconds)} s · ` +
    `maintenance ${sol.withMaintenance ? "on" : "off"}` +
    (CONFIG ? ` · LP ${CONFIG.lpBackend.toUpperCase()}` : "");

  const kpis = [
    ["Weekly profit", "$" + fmt(pnl.profit), pnl.profit >= 0 ? "pos" : "neg"],
    ["Revenue", "$" + fmt(pnl.revenue)],
    ["Total costs", "$" + fmt(totalCosts)],
    ["Margin", pnl.revenue ? (100 * pnl.profit / pnl.revenue).toFixed(1) + " %" : "—"],
    ["Demand served", (100 * shipped / demand).toFixed(1) + " %"],
    ["Tonnes flown", fmt1(shipped) + " t"],
    ["Aircraft in use", `${aircraftUsed} / ${aircraftOwned}`],
  ];
  $("reportKpis").innerHTML = kpis.map(([l, v, cls]) =>
    `<div class="kpi"><div class="l">${l}</div><div class="v ${cls || ""}">${v}</div></div>`).join("");

  drawWaterfall(pnl);
  renderFleetUtil(sol);
  renderServiceTable(sol, shipped, demand, served);
  renderTopLanes(sol);

  $("designOutcome").innerHTML = "";
  if (sol.design) {
    const d = sol.design;
    const best = d.rounds.find(r => r.round === d.bestRound);
    const accepted = d.proposals.filter(p => p.status === "accepted").length;
    $("designOutcome").innerHTML =
      `<b>Autonomous design outcome.</b> Base schedule $${fmt(d.baseProfit)} → ` +
      `<b>$${fmt(best.profit)}</b> at round ${d.bestRound} ` +
      `(<b>${(100 * (best.profit - d.baseProfit) / Math.abs(d.baseProfit)).toFixed(1)} %</b>). ` +
      `${d.proposals.length} candidate flights evaluated, ${accepted} adopted. ` +
      `Stop: ${d.stopReason}.`;
  }
}

/* P&L waterfall: revenue down through each cost bucket to profit. */
function drawWaterfall(pnl) {
  const svg = $("pnlWaterfall");
  svg.innerHTML = "";
  const steps = [
    ["Revenue", pnl.revenue, "in"],
    ["Variable", -pnl.variableCosts, "out"],
    ["Flight fixed", -pnl.fixedFlightCosts, "out"],
    ["Aircraft", -pnl.aircraftCosts, "out"],
    ...(pnl.externalCosts ? [["External", -pnl.externalCosts, "out"]] : []),
    ...(pnl.contractedCosts ? [["Contracted", -pnl.contractedCosts, "out"]] : []),
    ["Profit", pnl.profit, pnl.profit >= 0 ? "net" : "netneg"],
  ];
  const W = 860, H = 240, m = { l: 74, r: 10, t: 14, b: 34 };
  let run = 0, lo = 0, hi = 0;
  for (const [, v, kind] of steps) {
    if (kind === "net" || kind === "netneg") continue;
    run += v; hi = Math.max(hi, run); lo = Math.min(lo, run);
  }
  hi = Math.max(hi, pnl.revenue, pnl.profit, 0); lo = Math.min(lo, pnl.profit, 0);
  const span = hi - lo || 1;
  const Y = v => m.t + (H - m.t - m.b) * (1 - (v - lo) / span);
  const bw = (W - m.l - m.r) / steps.length;
  const colors = { in: "#1f4e8c", out: "#c8a36a", net: "#17805c", netneg: "#c03434" };
  for (let i = 0; i <= 3; i++) {
    const v = lo + span * i / 3;
    svgEl(svg, "line", { x1: m.l, x2: W - m.r, y1: Y(v), y2: Y(v), stroke: "#e2e5ea", "stroke-width": .7 });
    svgEl(svg, "text", { x: m.l - 6, y: Y(v) + 4, fill: "#647084", "font-size": 10, "text-anchor": "end" }, "$" + fmt(v));
  }
  let cum = 0;
  steps.forEach(([label, v, kind], i) => {
    const x = m.l + i * bw + bw * 0.14, w = bw * 0.72;
    let y0, y1;
    if (kind === "net" || kind === "netneg") { y0 = Y(0); y1 = Y(v); }
    else { y0 = Y(cum); cum += v; y1 = Y(cum); }
    const top = Math.min(y0, y1), h = Math.max(2, Math.abs(y1 - y0));
    svgEl(svg, "rect", { x, y: top, width: w, height: h, rx: 3, fill: colors[kind], opacity: .92 });
    if (kind === "out" && i < steps.length - 1)
      svgEl(svg, "line", { x1: x + w, x2: x + bw, y1: Y(cum), y2: Y(cum), stroke: "#b6bcc4", "stroke-dasharray": "3 3" });
    svgEl(svg, "text", { x: x + w / 2, y: H - 18, fill: "#647084", "font-size": 10.5, "text-anchor": "middle" }, label);
    svgEl(svg, "text", { x: x + w / 2, y: top - 5, fill: "#16181d", "font-size": 10, "text-anchor": "middle", "font-weight": 600 },
      (v < 0 ? "−$" : "$") + fmt(Math.abs(v)));
  });
}

function renderFleetUtil(sol) {
  const flightById = Object.fromEntries(sol.flights.map(f => [f.id, f]));
  const N = sol.periodMinutes;
  const util = {}; // fleet -> {used, blockMin, legs}
  for (const f of sol.fleets) util[f.code] = { owned: f.count, used: 0, blockMin: 0, legs: 0 };
  for (const r of sol.rotations) {
    const u = util[r.fleet];
    u.used += r.aircraft;
    for (const s of r.strings)
      for (const fid of s.flightIds)
        for (const l of flightById[fid].legs) {
          u.blockMin += (l.arr >= l.dep ? l.arr - l.dep : l.arr + N - l.dep);
          u.legs++;
        }
  }
  $("fleetUtilTable").innerHTML =
    "<tr><th>fleet</th><th>aircraft used</th><th>legs/wk</th><th>block h/wk</th><th>h per a/c</th><th>utilization</th></tr>" +
    sol.fleets.map(f => {
      const u = util[f.code];
      const perAc = u.used ? u.blockMin / 60 / u.used : 0;
      const pctU = Math.min(100, 100 * perAc / 168);
      return `<tr${u.used ? "" : ' class="dim"'}><td>${f.code}</td>
        <td>${u.used} / ${f.count}</td><td>${u.legs || "—"}</td>
        <td>${fmt1(u.blockMin / 60)}</td><td>${fmt1(perAc)}</td>
        <td><span class="bar" style="width:${Math.round(pctU * 0.6)}px"></span> ${pctU.toFixed(0)}%</td></tr>`;
    }).join("");
}

function renderServiceTable(sol, shipped, demand, served) {
  const rows = [
    ["Demand tendered", fmt1(demand) + " t · " + sol.ods.length + " O&Ds"],
    ["Flown on own network", fmt1(shipped - (sol.pnl.contractedT || 0)) + " t"],
    ...(sol.pnl.contractedT > 0
      ? [["Contracted out", fmt1(sol.pnl.contractedT) + ` t ($${fmt(sol.pnl.contractedCosts)})`]] : []),
    ["O&Ds served", `${served} / ${sol.ods.length}`],
    ...(sol.demandAtRisk ? [
      ["No feasible route at all", `${sol.demandAtRisk.unservableOds} O&Ds · ${fmt1(sol.demandAtRisk.unservableTonnes)} t`],
      ["No route in this schedule", `${sol.demandAtRisk.notInScheduleOds} O&Ds · ${fmt1(sol.demandAtRisk.notInScheduleTonnes)} t`],
    ] : []),
  ];
  $("serviceTable").innerHTML = rows.map(([l, v]) =>
    `<tr><td>${l}</td><td>${v}</td></tr>`).join("");
}

function renderTopLanes(sol) {
  const ap = Object.fromEntries(sol.airports.map(a => [a.id, a.code]));
  const top = [...sol.ods].sort((a, b) => b.shippedT - a.shippedT).slice(0, 10);
  $("topLanesTable").innerHTML =
    "<tr><th>lane</th><th>demand t</th><th>flown t</th><th>fill</th><th>rate $/t</th><th>revenue $</th></tr>" +
    top.map(o => `<tr><td>${ap[o.from]} → ${ap[o.to]}</td>
      <td>${fmt1(o.demandT)}</td><td>${fmt1(o.shippedT)}</td>
      <td>${o.demandT ? (100 * o.shippedT / o.demandT).toFixed(0) : 0}%</td>
      <td>${fmt(o.rate)}</td><td>${fmt(o.shippedT * o.rate)}</td></tr>`).join("");
}

/* Time-space network: airports on the y-axis, the week on the x-axis; every selected leg is a
   diagonal arc from (dep, origin) to (arr, destination). Ground/waiting time is the horizontal
   distance between consecutive arcs at an airport. */
function renderTimeSpace(sol) {
  const svg = $("timespace");
  svg.innerHTML = "";
  const N = sol.periodMinutes;
  const ap = Object.fromEntries(sol.airports.map(a => [a.id, a]));

  // rows: hubs first, then airports by activity in the selected schedule
  const activity = {};
  const activeLegs = [];
  for (const f of sol.flights) {
    if (!f.selected) continue;
    const external = f.kind === "external";
    for (const l of f.legs) {
      if (external && l.loadT <= 0) continue;
      activeLegs.push({ f, l, external });
      activity[l.from] = (activity[l.from] || 0) + 1;
      activity[l.to] = (activity[l.to] || 0) + 1;
    }
  }
  const maxRows = 32;
  const rowsIds = sol.airports
    .filter(a => activity[a.id])
    .sort((a, b) => (b.hub - a.hub) || (activity[b.id] - activity[a.id]))
    .slice(0, maxRows)
    .map(a => a.id);
  const rowOf = Object.fromEntries(rowsIds.map((id, i) => [id, i]));

  const rowH = 22, m = { l: 64, r: 10, t: 24, b: 8 };
  const dayW = 1400; // horizontal pixels per day: legs stay readable diagonals, not bars
  const W = m.l + 7 * dayW + m.r, H = m.t + rowsIds.length * rowH + m.b;
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  // element size must match the viewBox aspect ratio, otherwise the default
  // xMidYMid letterboxing shifts the drawing away from the hit-test coordinates
  svg.setAttribute("preserveAspectRatio", "xMinYMin meet");
  svg.style.width = W + "px";
  svg.style.height = H + "px";
  svg.removeAttribute("height");
  const X = t => m.l + (W - m.l - m.r) * t / N;
  const Y = row => m.t + row * rowH + rowH / 2;

  const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
  for (let d = 0; d < 7; d++) {
    svgEl(svg, "line", { x1: X(d * 1440), x2: X(d * 1440), y1: m.t - 6, y2: H - m.b, stroke: "#dddddd", "stroke-width": .6 });
    svgEl(svg, "text", { x: X(d * 1440 + 720), y: 13, fill: "#647084", "font-size": 10, "text-anchor": "middle" }, days[d]);
  }
  // airports touched by each flight (for the station highlight filter)
  const flightAirports = {};
  for (const f of sol.flights) {
    const set = new Set();
    for (const l of f.legs) { set.add(l.from); set.add(l.to); }
    flightAirports[f.id] = set;
  }

  const legEls = [];   // {el, flightId, from, to, baseOpacity}
  const labelEls = {}; // airport id -> text element

  function applyStationFilter() {
    const sel = tsSelectedStation;
    for (const { el, flightId, from, to, baseOpacity } of legEls) {
      // legs arriving/departing at the station: full strength;
      // remaining legs of the same flight: faint context; everything else: dimmed
      const o = sel == null ? baseOpacity
        : from === sel || to === sel ? 0.95
        : flightAirports[flightId].has(sel) ? 0.25
        : 0.05;
      el.setAttribute("opacity", o);
    }
    for (const [id, el] of Object.entries(labelEls)) {
      const on = sel != null && +id === sel;
      el.setAttribute("fill", on ? "#c03434" : ap[id].hub ? "#c03434" : "#647084");
      el.setAttribute("font-weight", on || ap[id].hub ? "bold" : "normal");
      el.setAttribute("text-decoration", on ? "underline" : "none");
    }
  }

  rowsIds.forEach((id, i) => {
    svgEl(svg, "line", { x1: m.l, x2: W - m.r, y1: Y(i), y2: Y(i), stroke: "#eeeeee", "stroke-width": .5 });
    const label = svgEl(svg, "text", { x: 6, y: Y(i) + 3,
      fill: ap[id].hub ? "#c03434" : "#647084", "font-size": 10,
      "font-weight": ap[id].hub ? "bold" : "normal", cursor: "pointer",
      "pointer-events": "none" }, ap[id].code);
    // generous invisible hit area over the whole label column of the row
    const hit = svgEl(svg, "rect", {
      x: 0, y: Y(i) - rowH / 2, width: m.l, height: rowH,
      fill: "transparent", cursor: "pointer",
    });
    svgEl(hit, "title", {}, `${ap[id].code} — click to highlight only flights touching this station`);
    hit.addEventListener("click", e => {
      e.stopPropagation();
      if (svg.dataset.moved === "1") { svg.dataset.moved = ""; return; }
      tsSelectedStation = tsSelectedStation === id ? null : id;
      applyStationFilter();
    });
    labelEls[id] = label;
  });

  let skipped = 0;
  const colors = { mandatory: "#1f4e8c", optional: "#17805c", external: "#b26a00" };
  for (const { f, l, external } of activeLegs) {
    const r1 = rowOf[l.from], r2 = rowOf[l.to];
    if (r1 === undefined || r2 === undefined) { skipped++; continue; }
    const arrAbs = l.arr >= l.dep ? l.arr : l.arr + N;
    const color = external ? colors.external : colors[f.kind];
    const width = external ? 1 : 1.4 + 1.6 * Math.min(1, l.capT ? l.loadT / l.capT : 0);
    const seg = (t1, y1, t2, y2) => {
      const line = svgEl(svg, "line", {
        x1: X(t1), y1, x2: X(t2), y2, stroke: color, "stroke-width": width, opacity: .8,
      });
      if (external) line.setAttribute("stroke-dasharray", "4 3");
      svgEl(line, "title", {},
        `${f.code} ${ap[l.from].code}→${ap[l.to].code}  load ${l.loadT}t/${l.capT}t`);
      legEls.push({ el: line, flightId: f.id, from: l.from, to: l.to, baseOpacity: 0.8 });
    };
    if (arrAbs <= N) {
      seg(l.dep, Y(r1), arrAbs, Y(r2));
    } else {
      // leg wraps over the week boundary: split with interpolated y
      const frac = (N - l.dep) / (arrAbs - l.dep);
      const yMid = Y(r1) + (Y(r2) - Y(r1)) * frac;
      seg(l.dep, Y(r1), N, yMid);
      seg(0, yMid, arrAbs - N, Y(r2));
    }
  }
  // clicking the background clears the station filter (but not after a pan drag)
  svg.addEventListener("click", () => {
    if (svg.dataset.moved === "1") { svg.dataset.moved = ""; return; }
    if (tsSelectedStation != null) { tsSelectedStation = null; applyStationFilter(); }
  });
  if (tsSelectedStation != null && rowOf[tsSelectedStation] === undefined) tsSelectedStation = null;
  applyStationFilter();

  $("tsNote").textContent = ` ${rowsIds.length} busiest airports` +
    (skipped ? `, ${skipped} legs out of view` : "") +
    " — wheel: zoom, drag: pan, click a station to filter";
  rememberViewBox(svg);
  makeZoomable(svg);
}

function renderKpis(sol) {
  const shipped = sol.ods.reduce((a, o) => a + o.shippedT, 0);
  const demand = sol.ods.reduce((a, o) => a + o.demandT, 0);
  const aircraft = sol.rotations.reduce((a, r) => a + r.aircraft, 0);
  // legs actually operated: every leg of a selected own flight, plus external legs carrying load
  const flownLegs = sol.flights.reduce((a, f) => a + (f.kind === "external"
    ? f.legs.filter(l => l.loadT > 0).length
    : f.selected ? f.legs.length : 0), 0);
  const totalCosts = sol.pnl.variableCosts + sol.pnl.fixedFlightCosts +
    sol.pnl.aircraftCosts + sol.pnl.externalCosts + (sol.pnl.contractedCosts || 0);
  const kpis = [
    ["Weekly profit", "$" + fmt(sol.pnl.profit), sol.pnl.profit >= 0 ? "pos" : "neg"],
    ["Revenue", "$" + fmt(sol.pnl.revenue)],
    ["Total costs", "$" + fmt(totalCosts)],
    ["Cargo shipped", fmt1(shipped) + " t"],
    ["Demand served", (100 * shipped / demand).toFixed(1) + " %"],
    ["Aircraft", aircraft],
    ["Legs flown", fmt(flownLegs)],
    ...(sol.pnl.contractedT > 0
      ? [["Contracted", fmt1(sol.pnl.contractedT) + " t ($" + fmt(sol.pnl.contractedCosts) + ")"]]
      : []),
    ["Gap", pct(sol.stats.gap)],
    ["B&B nodes", fmt(sol.stats.nodes)],
    ["Time", fmt1(sol.stats.seconds) + " s"],
  ];
  $("kpis").innerHTML = kpis.map(([l, v, cls]) =>
    `<div class="kpi"><div class="l">${l}</div><div class="v ${cls || ""}">${v}</div></div>`).join("");
}

function renderMap(sol) {
  const svg = $("map");
  svg.innerHTML = "";
  mapLegEls = [];
  mapPeriodN = sol.periodMinutes;
  const W = 1000, H = 480;
  // fit the airport bounding box (with padding) into the viewport, preserving aspect
  const lons = sol.airports.map(a => a.lon), lats = sol.airports.map(a => a.lat);
  let lonMin = Math.min(...lons), lonMax = Math.max(...lons);
  let latMin = Math.min(...lats), latMax = Math.max(...lats);
  const padLon = Math.max(3, (lonMax - lonMin) * 0.08);
  const padLat = Math.max(3, (latMax - latMin) * 0.12);
  lonMin -= padLon; lonMax += padLon; latMin -= padLat; latMax += padLat;
  const scale = Math.min(W / (lonMax - lonMin), H / (latMax - latMin));
  const xOff = (W - (lonMax - lonMin) * scale) / 2;
  const yOff = (H - (latMax - latMin) * scale) / 2;
  const X = lon => xOff + (lon - lonMin) * scale;
  const Y = lat => yOff + (latMax - lat) * scale;
  const gridStep = (lonMax - lonMin) > 120 ? 30 : (lonMax - lonMin) > 40 ? 10 : 5;
  for (let lon = Math.ceil(lonMin / gridStep) * gridStep; lon <= lonMax; lon += gridStep)
    svgEl(svg, "line", { x1: X(lon), x2: X(lon), y1: 0, y2: H, stroke: "#eeeeee", "stroke-width": .5 });
  for (let lat = Math.ceil(latMin / gridStep) * gridStep; lat <= latMax; lat += gridStep)
    svgEl(svg, "line", { x1: 0, x2: W, y1: Y(lat), y2: Y(lat), stroke: "#eeeeee", "stroke-width": .5 });

  // world landmass behind the network (clipped by the svg viewBox)
  for (const poly of worldPolys) {
    if (poly.every(p => p[0] < lonMin || p[0] > lonMax || p[1] < latMin || p[1] > latMax)) continue;
    svgEl(svg, "path", {
      d: "M " + poly.map(p => `${X(p[0]).toFixed(1)} ${Y(p[1]).toFixed(1)}`).join(" L ") + " Z",
      fill: "#eae7dc", stroke: "#bbbbbb", "stroke-width": .7,
    });
  }

  const ap = Object.fromEntries(sol.airports.map(a => [a.id, a]));
  const styles = {
    mandatory: { stroke: "#1f4e8c", op: .8 },
    optSel: { stroke: "#17805c", op: .85 },
    optRej: { stroke: "#b6bcc4", op: .7, dash: "4 4" },
    external: { stroke: "#b26a00", op: .55 },
  };
  const legLine = (l, st, width) => {
    const a = ap[l.from], b = ap[l.to];
    // draw the shorter way around the antimeridian
    let x1 = X(a.lon), x2 = X(b.lon);
    if (Math.abs(x1 - x2) > W / 2) { if (x1 < x2) x1 += W; else x2 += W; }
    const mx = (x1 + x2) / 2, my = (Y(a.lat) + Y(b.lat)) / 2 - Math.abs(x1 - x2) * 0.12;
    const draw = (xa, xb, dx) => {
      const p = svgEl(svg, "path", {
        d: `M ${xa - dx} ${Y(a.lat)} Q ${mx - dx} ${my} ${xb - dx} ${Y(b.lat)}`,
        fill: "none", stroke: st.stroke, opacity: st.op, "stroke-width": width,
      });
      if (st.dash) p.setAttribute("stroke-dasharray", st.dash);
      svgEl(p, "title", {},
        `${a.code} → ${b.code}  ${Math.round(l.km)} km  load ${l.loadT} t / cap ${l.capT} t  ` +
        `${fmtTime(l.dep)}→${fmtTime(l.arr)}`);
      mapLegEls.push({ el: p, dep: l.dep,
        arrAbs: l.arr >= l.dep ? l.arr : l.arr + mapPeriodN, baseOpacity: st.op });
    };
    draw(x1, x2, 0);
    if (x1 > W || x2 > W) draw(x1, x2, W);
  };
  for (const f of sol.flights) {
    for (const l of f.legs) {
      const st = f.kind === "external"
        ? (l.loadT > 0 ? styles.external : null)
        : f.kind === "mandatory" ? styles.mandatory
        : f.selected ? styles.optSel : styles.optRej;
      if (!st) continue;
      const width = st === styles.optRej ? 1 : 1 + 2.5 * Math.min(1, l.capT ? l.loadT / l.capT : 0);
      legLine(l, st, width);
    }
  }
  for (const a of sol.airports) {
    const g = svgEl(svg, "circle", {
      cx: X(a.lon), cy: Y(a.lat), r: a.hub ? 6 : 2.6,
      fill: a.hub ? "#c03434" : "#1f4e8c", stroke: "#ffffff", "stroke-width": 1,
    });
    svgEl(g, "title", {}, `${a.code} — ${a.name}${a.hub ? " (hub)" : ""}`);
    // every station gets its code next to the dot (hubs larger and red)
    const label = svgEl(svg, "text", {
      x: X(a.lon) + (a.hub ? 8 : 4), y: Y(a.lat) + 3,
      fill: a.hub ? "#c03434" : "#333333",
      "font-size": a.hub ? 11 : 7.5,
      "font-weight": a.hub ? "bold" : "normal",
      "paint-order": "stroke", stroke: "#ffffff", "stroke-width": 2,
    }, a.code);
  }
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  rememberViewBox(svg);
  makeZoomable(svg);
  applyMapHour(); // keep the hourly scrubber state across re-renders
}

function renderGantt(sol) {
  const svg = $("gantt");
  svg.innerHTML = "";
  const N = sol.periodMinutes;
  const rowH = 26, m = { l: 120, r: 10, t: 26 };
  const W = 1400;
  const rows = sol.rotations;
  const H = m.t + rows.length * rowH + 10;
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  svg.setAttribute("height", H);
  const X = t => m.l + (W - m.l - m.r) * t / N;
  const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
  for (let d = 0; d < 7; d++) {
    svgEl(svg, "line", { x1: X(d * 1440), x2: X(d * 1440), y1: m.t - 8, y2: H, stroke: "#dddddd", "stroke-width": .6 });
    svgEl(svg, "text", { x: X(d * 1440 + 720), y: 14, fill: "#647084", "font-size": 10, "text-anchor": "middle" }, days[d]);
  }
  const fleetColors = {};
  const palette = ["#1f4e8c", "#17805c", "#7a5fb5", "#b26a00", "#0e7f8a"];
  sol.fleets.forEach((f, i) => fleetColors[f.code] = palette[i % palette.length]);

  const flightById = Object.fromEntries(sol.flights.map(f => [f.id, f]));
  rows.forEach((r, i) => {
    const y = m.t + i * rowH;
    svgEl(svg, "text", { x: 6, y: y + 15, fill: "#647084", "font-size": 10 },
      `${r.fleet} rot.${r.id + 1} (${r.aircraft} a/c)`);
    for (const s of r.strings) {
      for (const fid of s.flightIds) {
        const f = flightById[fid];
        for (const l of f.legs) {
          const x1 = X(l.dep), x2raw = l.arr >= l.dep ? l.arr : l.arr + N;
          const seg = (a, b) => {
            const rect = svgEl(svg, "rect", {
              x: X(a), y: y + 4, width: Math.max(2, X(b) - X(a)), height: rowH - 10,
              rx: 2, fill: fleetColors[r.fleet], opacity: .85,
            });
            svgEl(rect, "title", {}, `${f.code}: leg ${l.id} dep ${tstr(l.dep)} arr ${tstr(l.arr)}`);
          };
          if (x2raw <= N) seg(l.dep, x2raw);
          else { seg(l.dep, N); seg(0, x2raw - N); }
        }
      }
      if (sol.withMaintenance) {
        const mnt = svgEl(svg, "rect", {
          x: X(s.arr), y: y + 4, width: 5, height: rowH - 10, fill: "#c03434",
        });
        svgEl(mnt, "title", {}, "maintenance stop");
      }
    }
  });

  function tstr(t) {
    const d = Math.floor(t / 1440), h = Math.floor((t % 1440) / 60), mi = t % 60;
    return `${days[d]} ${String(h).padStart(2, "0")}:${String(mi).padStart(2, "0")}`;
  }
}

function renderOdTable(sol) {
  const shipped = sol.ods.reduce((a, o) => a + o.shippedT, 0);
  const demand = sol.ods.reduce((a, o) => a + o.demandT, 0);
  const served = sol.ods.filter(o => o.shippedT > 1e-6).length;
  let riskTxt = "";
  if (sol.demandAtRisk) {
    const r = sol.demandAtRisk;
    riskTxt = ` — no possible route: ${r.unservableOds} O&Ds (${fmt1(r.unservableTonnes)} t); ` +
      `no route in this schedule: ${r.notInScheduleOds} O&Ds (${fmt1(r.notInScheduleTonnes)} t)`;
  }
  $("odSummary").textContent =
    ` ${fmt1(shipped)} / ${fmt1(demand)} t (${(100 * shipped / demand).toFixed(1)} %), ` +
    `${served}/${sol.ods.length} O&Ds served${riskTxt}`;
  const ap = Object.fromEntries(sol.airports.map(a => [a.id, a.code]));
  // unservable demand first (planner attention), then largest shipped
  const top = [...sol.ods]
    .sort((a, b) => (a.servable === false ? -1 : 0) - (b.servable === false ? -1 : 0)
      || b.shippedT - a.shippedT)
    .slice(0, 18);
  $("odTable").innerHTML =
    "<tr><th>O&D</th><th>demand t</th><th>shipped t</th><th>fill</th><th>rate $/t</th><th>route</th></tr>" +
    top.map(o => {
      const fill = o.demandT ? o.shippedT / o.demandT : 0;
      const route = o.servable === false ? "✖ no route"
        : o.servableInSchedule === false ? "⚠ not in schedule" : "✓";
      return `<tr${o.shippedT < 1e-6 ? ' class="dim"' : ""}>
        <td>${ap[o.from]} → ${ap[o.to]}</td>
        <td>${fmt1(o.demandT)}</td><td>${fmt1(o.shippedT)}</td>
        <td><span class="bar" style="width:${Math.round(40 * fill)}px"></span> ${(100 * fill).toFixed(0)}%</td>
        <td>${fmt(o.rate)}</td><td>${route}</td></tr>`;
    }).join("");
}

function renderFlightTable(sol) {
  const rows = sol.flights
    .filter(f => f.kind !== "external")
    .map(f => {
      const maxLoad = Math.max(...f.legs.map(l => l.capT ? l.loadT / l.capT : 0));
      return { f, maxLoad };
    })
    .sort((a, b) => (b.f.selected - a.f.selected) || b.maxLoad - a.maxLoad)
    .slice(0, 22);
  $("flightTable").innerHTML =
    "<tr><th>flight</th><th>kind</th><th>fleet</th><th>legs</th><th>load factor</th></tr>" +
    rows.map(({ f, maxLoad }) =>
      `<tr${f.selected ? "" : ' class="dim"'}>
        <td>${f.code}</td><td>${f.kind}${f.selected ? "" : " (not flown)"}</td>
        <td>${f.fleet || "—"}</td><td>${f.legs.length}</td>
        <td><span class="bar" style="width:${Math.round(40 * Math.min(1, maxLoad))}px"></span>
          ${(100 * maxLoad).toFixed(0)}%</td></tr>`).join("");
}
