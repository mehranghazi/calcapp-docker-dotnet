"use strict";

const $ = (id) => document.getElementById(id);
const fmt = (n, d = 0) => (n === null || n === undefined || Number.isNaN(n)) ? "–" :
  Number(n).toLocaleString("en-US", { maximumFractionDigits: d, minimumFractionDigits: d });

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined
  });
  let data = null;
  try { data = await res.json(); } catch { /* empty body */ }
  return { ok: res.ok, status: res.status, data };
}

/* =========================== Calculator =========================== */

let expr = "";
let lastAnswer = null;
let justEvaluated = false;
const history = [];

function render() {
  $("expr").textContent = expr;
}

function insert(text) {
  const startsWithOperator = /^[+−×÷^%!]/.test(text) || text === "^2";
  if (justEvaluated) {
    // after "=", an operator continues from the answer; anything else starts fresh
    expr = startsWithOperator && lastAnswer !== null ? String(lastAnswer) : "";
    justEvaluated = false;
  }
  expr += text;
  render();
}

function clearAll() {
  expr = "";
  justEvaluated = false;
  $("result").textContent = "0";
  $("result").classList.remove("error");
  $("calc-meta").textContent = "Type or tap an expression, then press =";
  render();
}

function backspace() {
  if (justEvaluated) { justEvaluated = false; }
  expr = expr.slice(0, -1);
  render();
}

async function evaluate() {
  if (!expr.trim()) return;
  const started = performance.now();
  try {
    const { ok, data } = await api("POST", "/api/calc/eval", { expression: expr });
    const ms = Math.round(performance.now() - started);
    const res = $("result");
    if (ok) {
      res.classList.remove("error");
      res.textContent = data.display;
      lastAnswer = data.display;
      $("calc-meta").textContent = `Calculated on the server in ${ms} ms`;
      addHistory(expr, data.display);
      justEvaluated = true;
    } else {
      res.classList.add("error");
      res.textContent = data && data.error ? data.error : "The server could not calculate this.";
      $("calc-meta").textContent = "Fix the expression and press = again";
    }
  } catch {
    $("result").classList.add("error");
    $("result").textContent = "Cannot reach the server. Check that the container is running.";
  }
}

function addHistory(e, r) {
  history.unshift({ e, r });
  history.length = Math.min(history.length, 6);
  const ol = $("history");
  ol.innerHTML = "";
  for (const h of history) {
    const li = document.createElement("li");
    li.title = "Use this result";
    const a = document.createElement("span"); a.textContent = h.e;
    const b = document.createElement("span"); b.textContent = "= " + h.r;
    li.append(a, b);
    li.addEventListener("click", () => { expr = h.r; justEvaluated = false; render(); });
    ol.append(li);
  }
}

$("keys").addEventListener("click", (ev) => {
  const btn = ev.target.closest("button");
  if (!btn) return;
  const action = btn.dataset.action;
  if (btn.dataset.insert !== undefined) insert(btn.dataset.insert);
  else if (action === "clear") clearAll();
  else if (action === "back") backspace();
  else if (action === "eval") evaluate();
  else if (action === "ans" && lastAnswer !== null) insert(String(lastAnswer));
});

// Keyboard input (ignored while typing in the bench forms)
document.addEventListener("keydown", (ev) => {
  if (ev.target.closest("input, select, textarea") || ev.ctrlKey || ev.metaKey || ev.altKey) return;
  const map = { "*": "×", "/": "÷", "-": "−" };
  let handled = true;
  if (/^[0-9.+()^%!]$/.test(ev.key)) insert(ev.key);
  else if (map[ev.key]) insert(map[ev.key]);
  else if (/^[a-z]$/i.test(ev.key)) insert(ev.key.toLowerCase());
  else if (ev.key === "Enter" || ev.key === "=") evaluate();
  else if (ev.key === "Backspace") backspace();
  else if (ev.key === "Escape" || ev.key === "Delete") clearAll();
  else handled = false;
  if (handled) {
    ev.preventDefault();
    flashKey(ev.key);
  }
});

function flashKey(key) {
  const sel = { Enter: "eval", "=": "eval", Backspace: "back", Escape: "clear" }[key];
  const btn = sel
    ? document.querySelector(`.k[data-action="${sel}"]`)
    : [...document.querySelectorAll(".k[data-insert]")].find(b => b.dataset.insert === ({ "*": "×", "/": "÷", "-": "−" }[key] || key));
  if (!btn) return;
  btn.classList.add("pressed");
  setTimeout(() => btn.classList.remove("pressed"), 90);
}

/* =========================== Load test =========================== */

let ltTimer = null;

document.querySelectorAll('input[name="lt-mode"]').forEach(r => r.addEventListener("change", () => {
  const time = document.querySelector('input[name="lt-mode"]:checked').value === "time";
  $("lt-count-field").hidden = time;
  $("lt-time-field").hidden = !time;
}));

$("lt-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const time = document.querySelector('input[name="lt-mode"]:checked').value === "time";
  const body = {
    target: $("lt-target").value,
    concurrency: Number($("lt-concurrency").value) || 1,
    requests: time ? 0 : Number($("lt-requests").value) || 1,
    durationSeconds: time ? Number($("lt-seconds").value) || 1 : 0
  };
  const { ok, data } = await api("POST", "/api/loadtest/start", body);
  if (!ok) { setStatus("lt-status", data && data.error ? data.error : "The load test could not start.", "error"); return; }
  showLoadTest(data);
  pollLoadTest();
});

$("lt-stop").addEventListener("click", async () => {
  const { data } = await api("POST", "/api/loadtest/stop");
  if (data) showLoadTest(data);
});

function pollLoadTest() {
  clearInterval(ltTimer);
  ltTimer = setInterval(async () => {
    const { data } = await api("GET", "/api/loadtest/status");
    if (data) showLoadTest(data);
    if (!data || !data.running) clearInterval(ltTimer);
  }, 1000);
}

function showLoadTest(d) {
  if (!d || d.completed === undefined) return;
  $("lt-start").disabled = d.running;
  $("lt-stop").disabled = !d.running;

  const total = d.requests ?? null;
  const pct = d.running
    ? (total ? d.completed / total : d.durationSeconds ? d.elapsedSeconds / d.durationSeconds : 0)
    : 1;
  $("lt-bar").style.width = Math.min(100, pct * 100).toFixed(1) + "%";
  $("lt-bar").classList.toggle("running", d.running);

  const goal = total ? `${fmt(total)} requests` : `for ${d.durationSeconds} s`;
  if (d.running) setStatus("lt-status", `Running: ${d.target} traffic, ${d.concurrency} parallel users, ${goal}. ${fmt(d.elapsedSeconds, 1)} s so far.`, "running");
  else setStatus("lt-status", `Finished: ${fmt(d.completed)} requests in ${fmt(d.elapsedSeconds, 1)} s (${d.target} traffic, ${d.concurrency} parallel users).`, "");

  $("lt-completed").textContent = fmt(d.completed);
  $("lt-errors").textContent = fmt(d.errors);
  $("lt-errors").classList.toggle("bad", d.errors > 0);
  $("lt-rps").textContent = fmt(d.requestsPerSecond, 1);
  $("lt-avg").textContent = fmt(d.avgMs, 1) + " ms";
  $("lt-p50").textContent = fmt(d.p50Ms, 1) + " ms";
  $("lt-p95").textContent = fmt(d.p95Ms, 1) + " ms";
  $("lt-p99").textContent = fmt(d.p99Ms, 1) + " ms";
  $("lt-max").textContent = fmt(d.maxMs, 1) + " ms";
}

function setStatus(id, text, kind) {
  const el = $(id);
  el.textContent = text;
  el.className = "status" + (kind ? " " + kind : "");
}

/* =========================== Stress =========================== */

$("cpu-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const q = `threads=${Number($("cpu-threads").value) || 1}&seconds=${Number($("cpu-seconds").value) || 30}`;
  const { ok } = await api("POST", "/api/stress/cpu?" + q);
  if (!ok) setStatus("st-status", "The CPU stress could not start.", "error");
  refreshStress();
});

$("mem-form").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const q = `mb=${Number($("mem-mb").value) || 256}&seconds=${Number($("mem-seconds").value) || 30}`;
  const { ok } = await api("POST", "/api/stress/memory?" + q);
  if (!ok) setStatus("st-status", "The memory stress could not start.", "error");
  refreshStress();
});

$("stress-stop").addEventListener("click", async () => {
  await api("POST", "/api/stress/stop");
  refreshStress();
});

async function refreshStress() {
  const { data } = await api("GET", "/api/stress/status");
  if (!data) return;
  const parts = [];
  for (const j of [data.cpu, data.memory]) {
    if (j && j.running) parts.push(`${j.kind === "cpu" ? "CPU" : "Memory"} stress running (${j.detail}), ${fmt(j.elapsedSeconds)} of ${j.durationSeconds} s`);
  }
  const notes = [data.cpu, data.memory].filter(j => j && j.note).map(j => j.note);
  if (parts.length) setStatus("st-status", parts.join(". ") + ".", "running");
  else if (notes.length) setStatus("st-status", notes.join(". "), "error");
  else setStatus("st-status", "No stress job is running.", "");
}

/* =========================== Live metrics =========================== */

const series = { cpu: [], rps: [] };
const MAX_POINTS = 120;
let lastTotal = null, lastTime = null;

function pushPoint(arr, v) {
  arr.push(v);
  if (arr.length > MAX_POINTS) arr.shift();
}

function drawSpark(canvasId, data, maxHint) {
  const c = $(canvasId);
  const dpr = window.devicePixelRatio || 1;
  const w = c.clientWidth, h = c.clientHeight;
  if (c.width !== w * dpr) { c.width = w * dpr; c.height = h * dpr; }
  const g = c.getContext("2d");
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  if (data.length < 2) return;
  const max = Math.max(maxHint || 0, ...data, 1);
  const step = w / (MAX_POINTS - 1);
  const x0 = w - (data.length - 1) * step;
  g.beginPath();
  data.forEach((v, i) => {
    const x = x0 + i * step, y = h - 4 - (v / max) * (h - 8);
    i ? g.lineTo(x, y) : g.moveTo(x, y);
  });
  g.lineWidth = 2;
  g.strokeStyle = "#1f2421";
  g.stroke();
  g.lineTo(w, h); g.lineTo(x0, h); g.closePath();
  g.fillStyle = "rgba(227,168,43,0.35)";
  g.fill();
}

function uptime(s) {
  const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m ${s % 60}s`;
}

async function refreshStats() {
  let data;
  try { ({ data } = await api("GET", "/api/stats")); } catch { $("host").textContent = "Server is not reachable."; return; }
  if (!data) return;

  $("host").textContent = `Running on ${data.framework}, ${data.os}, host ${data.machine} with ${data.processorCount} CPU${data.processorCount > 1 ? "s" : ""}.`;
  $("cpu-threads").max = data.processorCount * 4;

  const now = performance.now();
  let rps = 0;
  if (lastTotal !== null) rps = Math.max(0, (data.requests.total - lastTotal) / ((now - lastTime) / 1000));
  lastTotal = data.requests.total; lastTime = now;

  pushPoint(series.cpu, data.cpuPercent);
  pushPoint(series.rps, rps);
  drawSpark("spark-cpu", series.cpu, 100);
  drawSpark("spark-rps", series.rps);

  $("mx-cpu").textContent = fmt(data.cpuPercent, 0) + " %";
  $("mx-rps").textContent = fmt(rps, rps < 10 ? 1 : 0);
  $("mx-ws").textContent = fmt(data.workingSetMb, 0) + " MB";
  $("mx-heap").textContent = fmt(data.gcHeapMb, 0) + " MB";
  $("mx-limit").textContent = fmt(data.memoryLimitMb, 0) + " MB";
  $("mx-threads").textContent = fmt(data.threads);
  $("mx-total").textContent = fmt(data.requests.total);
  $("mx-errors").textContent = fmt(data.requests.errors);
  $("mx-errors").classList.toggle("bad", data.requests.errors > 0);
  $("mx-inflight").textContent = fmt(data.requests.inFlight);
  $("mx-uptime").textContent = uptime(data.uptimeSeconds);

  const tb = $("mx-endpoints");
  if (!data.requests.endpoints.length) return;
  tb.innerHTML = "";
  for (const e of data.requests.endpoints) {
    const tr = document.createElement("tr");
    for (const [v, cls] of [[e.endpoint], [fmt(e.count)], [fmt(e.errors), e.errors ? "bad" : ""], [fmt(e.avgMs, 1)], [fmt(e.maxMs, 0)]]) {
      const td = document.createElement("td");
      td.textContent = v;
      if (cls) td.style.color = "var(--error)";
      tr.append(td);
    }
    tb.append(tr);
  }
}

$("mx-reset").addEventListener("click", async () => {
  await api("POST", "/api/stats/reset");
  lastTotal = null;
  $("mx-endpoints").innerHTML = '<tr><td colspan="5" class="empty">Counters reset. New requests will appear here.</td></tr>';
  refreshStats();
});

/* =========================== start =========================== */

refreshStats();
setInterval(refreshStats, 1000);
setInterval(refreshStress, 2000);
refreshStress();
// pick up a load test that was started earlier (e.g. after a page reload)
api("GET", "/api/loadtest/status").then(({ data }) => {
  if (data && data.completed !== undefined) { showLoadTest(data); if (data.running) pollLoadTest(); }
});
