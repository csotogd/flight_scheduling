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
  $("solveBtn").onclick = startSolve;
  $("designBtn").onclick = startDesign;
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
  };
  refreshSaved();
}

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
      "<tr><th>fleet</th><th>a/c</th><th>max t</th><th>max m³</th><th>range km</th>" +
      "<th>fixed $/a-c/wk</th><th>ground min</th><th>mnt cycles</th><th>mnt flight h</th>" +
      "<th>mnt elapsed d</th><th>mnt stop h</th></tr>" +
      d.fleets.map(k => `<tr><td>${k.code}</td><td>${k.count}</td><td>${k.maxWeightT}</td>
        <td>${k.maxVolM3}</td><td>${fmt(k.rangeKm)}</td><td>${fmt(k.fixedPerAircraftWeek)}</td>
        <td>${k.groundMin}</td><td>${k.mntCycles}</td><td>${k.mntFlightH}</td>
        <td>${k.mntElapsedDays}</td><td>${k.mntDurationH}</td></tr>`).join("");

    $("inputAirports").innerHTML =
      "<tr><th>code</th><th>name</th><th>hub</th><th>min transfer</th><th>transfer $/t</th><th>storage $/t/h</th></tr>" +
      d.airports.map(a => `<tr${a.hub ? "" : ' class=""'}><td>${a.code}</td><td>${a.name}</td>
        <td>${a.hub ? "yes" : ""}</td><td>${a.hub ? a.minTransferMin + " min" : "—"}</td>
        <td>${a.hub ? a.transferCostPerT : "—"}</td><td>${a.hub ? a.storagePerTHour : "—"}</td></tr>`).join("");

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
  $("progressSection").classList.remove("hidden");
  $("dashboard").classList.add("hidden");
  const es = new EventSource(`/api/jobs/${job.id}/events`);
  es.onmessage = async ev => {
    const e = JSON.parse(ev.data);
    if (e.type === "design-phase") {
      designPhase(e);
    } else if (e.type === "progress") {
      $("progressStats").textContent =
        `t ${e.t}s   nodes ${e.nodes}   incumbent ${fmt(e.incumbent)}   ` +
        `bound ${fmt(e.bound)}   gap ${e.gap != null ? pct(e.gap) : "—"}   ` +
        `columns ${e.cols}   cuts ${e.cuts}   ${e.phase}`;
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
    svgEl(svg, "text", { x: m.l - 6, y: Y(v) + 4, fill: "#555555", "font-size": 10, "text-anchor": "end" }, fmt(v));
  }
  const line = (key, color) => {
    const pts = series.filter(s => s[key] != null).map(s => `${X(s.t)},${Y(s[key])}`);
    if (pts.length > 1)
      svgEl(svg, "polyline", { points: pts.join(" "), fill: "none", stroke: color, "stroke-width": 1.6 });
  };
  line("bound", "#cc0000");
  line("incumbent", "#009933");
  svgEl(svg, "text", { x: W - 12, y: 14, fill: "#cc0000", "font-size": 10, "text-anchor": "end" }, "upper bound");
  svgEl(svg, "text", { x: W - 12, y: 26, fill: "#009933", "font-size": 10, "text-anchor": "end" }, "incumbent");
}

// ------------------------------------------------------------------ dashboard

function renderSolution(sol) {
  $("dashboard").classList.remove("hidden");
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
    svgEl(svg, "text", { x: X(d * 1440 + 720), y: 13, fill: "#555555", "font-size": 10, "text-anchor": "middle" }, days[d]);
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
      el.setAttribute("fill", on ? "#cc0000" : ap[id].hub ? "#cc0000" : "#555555");
      el.setAttribute("font-weight", on || ap[id].hub ? "bold" : "normal");
      el.setAttribute("text-decoration", on ? "underline" : "none");
    }
  }

  rowsIds.forEach((id, i) => {
    svgEl(svg, "line", { x1: m.l, x2: W - m.r, y1: Y(i), y2: Y(i), stroke: "#eeeeee", "stroke-width": .5 });
    const label = svgEl(svg, "text", { x: 6, y: Y(i) + 3,
      fill: ap[id].hub ? "#cc0000" : "#555555", "font-size": 10,
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
  const colors = { mandatory: "#0066cc", optional: "#009933", external: "#ff8800" };
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
    mandatory: { stroke: "#0066cc", op: .8 },
    optSel: { stroke: "#009933", op: .85 },
    optRej: { stroke: "#aaaaaa", op: .7, dash: "4 4" },
    external: { stroke: "#ff8800", op: .55 },
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
      fill: a.hub ? "#cc0000" : "#336699", stroke: "#ffffff", "stroke-width": 1,
    });
    svgEl(g, "title", {}, `${a.code} — ${a.name}${a.hub ? " (hub)" : ""}`);
    // every station gets its code next to the dot (hubs larger and red)
    const label = svgEl(svg, "text", {
      x: X(a.lon) + (a.hub ? 8 : 4), y: Y(a.lat) + 3,
      fill: a.hub ? "#cc0000" : "#333333",
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
    svgEl(svg, "text", { x: X(d * 1440 + 720), y: 14, fill: "#555555", "font-size": 10, "text-anchor": "middle" }, days[d]);
  }
  const fleetColors = {};
  const palette = ["#0066cc", "#009933", "#9933cc", "#ff8800", "#009999"];
  sol.fleets.forEach((f, i) => fleetColors[f.code] = palette[i % palette.length]);

  const flightById = Object.fromEntries(sol.flights.map(f => [f.id, f]));
  rows.forEach((r, i) => {
    const y = m.t + i * rowH;
    svgEl(svg, "text", { x: 6, y: y + 15, fill: "#555555", "font-size": 10 },
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
          x: X(s.arr), y: y + 4, width: 5, height: rowH - 10, fill: "#cc0000",
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
