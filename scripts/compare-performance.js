// Parsing is pinned to PresentMon's v1 schema because later exports rename these fields.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export function parseCsv(text) {
  const rows = [];
  let row = [], cell = '', quoted = false;
  text = text.replace(/^\uFEFF/, '');
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      if (quoted && text[i + 1] === '"') { cell += '"'; i++; }
      else quoted = !quoted;
    } else if (!quoted && (c === ',' || c === '\n')) {
      row.push(cell.replace(/\r$/, '')); cell = '';
      if (c === '\n') { if (row.some(v => v !== '')) rows.push(row); row = []; }
    } else cell += c;
  }
  if (quoted) throw new Error('Unterminated CSV quote');
  if (cell || row.length) { row.push(cell.replace(/\r$/, '')); rows.push(row); }
  const headers = rows.shift() ?? [];
  for (const name of ['ProcessID', 'SwapChainAddress', 'Dropped', 'MsBetweenPresents', 'MsBetweenDisplayChange']) {
    if (!headers.includes(name)) throw new Error(`Missing ${name}; capture with --v1_metrics`);
  }
  return rows.map(values => {
    if (values.length !== headers.length) throw new Error('CSV column count mismatch');
    return Object.fromEntries(headers.map((h, i) => [h, values[i]]));
  });
}

export function summarize(values) {
  const sorted = values.filter(v => Number.isFinite(v) && v > 0).sort((a, b) => a - b);
  if (!sorted.length) return { samples: 0, totalMs: 0, averageFps: null, medianMs: null, p95Ms: null, p99Ms: null, maximumMs: null, over33Point3Ms: 0, over50Ms: 0, over100Ms: 0 };
  const totalMs = sorted.reduce((a, b) => a + b, 0);
  const p = fraction => sorted[Math.ceil(sorted.length * fraction) - 1];
  return { samples: sorted.length, totalMs, averageFps: sorted.length * 1000 / totalMs,
    medianMs: p(.5), p95Ms: p(.95), p99Ms: p(.99), maximumMs: sorted.at(-1),
    over33Point3Ms: sorted.filter(v => v > 33.3).length,
    over50Ms: sorted.filter(v => v > 50).length,
    over100Ms: sorted.filter(v => v > 100).length };
}

export function analyzeRows(rows, processId) {
  const groups = new Map();
  for (const row of rows) {
    if (row.ProcessID !== String(processId)) continue;
    const key = row.SwapChainAddress;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(row);
  }
  if (!groups.size) throw new Error('No rows for the manifest process ID');
  return [...groups].map(([swapChain, frames]) => {
    const displayed = frames.filter(f => f.Dropped === '0');
    const present = summarize(frames.map(f => Number(f.MsBetweenPresents)));
    const display = summarize(displayed.map(f => Number(f.MsBetweenDisplayChange)));
    return { swapChain, rows: frames.length, dropped: frames.filter(f => f.Dropped === '1').length,
      unknownDropState: frames.filter(f => f.Dropped !== '0' && f.Dropped !== '1').length,
      invalidPresentIntervals: frames.length - present.samples,
      unavailableDisplayIntervals: displayed.length - display.samples,
      present, display };
  });
}

export function loadCapture(manifestPath) {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8').replace(/^\uFEFF/, ''));
  if (manifest.Status !== 'captured-unverified') throw new Error(`${manifestPath}: capture status is ${manifest.Status}`);
  if (manifest.CsvFiles?.length !== 1) throw new Error(`${manifestPath}: expected exactly one recording, found ${manifest.CsvFiles?.length ?? 0}`);
  const file = manifest.CsvFiles[0];
  if (path.basename(file) !== file || file.includes('\\') || file.includes('/')) throw new Error('CSV must be beside its manifest');
  return { manifest, source: path.resolve(manifestPath),
    streams: analyzeRows(parseCsv(fs.readFileSync(path.join(path.dirname(manifestPath), file), 'utf8')), manifest.ProcessId) };
}

export function comparisonWarnings(captures) {
  const warnings = [];
  const conditions = new Map();
  const machines = new Map();
  for (const c of captures) {
    const m = c.manifest;
    const label = `${m.MachineLabel} / ${m.Configuration} / ${m.Instrumentation} / ${m.Scenario}`;
    const key = JSON.stringify([label, m.SettingsNote, m.GameBuild, m.PresentMonSha256]);
    if (!conditions.has(key)) conditions.set(key, { label, runs: [] });
    conditions.get(key).runs.push(m.Run);
    if (!machines.has(m.MachineLabel)) machines.set(m.MachineLabel, new Set());
    machines.get(m.MachineLabel).add(JSON.stringify([m.GameBuild, m.ExecutableSha256, m.PresentMonSha256,
      m.SettingsNote, m.OS, m.CPU, m.GPU, m.RAMBytes]));
    for (const s of c.streams) {
      const seconds = s.present.totalMs / 1000;
      if (seconds < 171 || seconds > 189) warnings.push(`${label}, run ${m.Run}, stream ${s.swapChain}: ${seconds.toFixed(3)} seconds of present intervals; verify incomplete/overshooting run or auxiliary stream.`);
      if (s.display.samples === 0) warnings.push(`${label}, run ${m.Run}, stream ${s.swapChain}: display telemetry unavailable.`);
      if (s.unknownDropState) warnings.push(`${label}, run ${m.Run}, stream ${s.swapChain}: unknown Dropped values; inspect CSV compatibility.`);
    }
  }
  for (const { label, runs } of conditions.values()) {
    if (runs.length !== 3 || ![1, 2, 3].every(n => runs.includes(n)))
      warnings.push(`${label}: require exactly runs 1, 2 and 3 with matching metadata; received ${runs.join(', ')}.`);
  }
  for (const [machine, variants] of machines) {
    if (variants.size > 1) warnings.push(`${machine}: build, settings, hardware/driver or capture-tool metadata differs; separate these comparisons.`);
  }
  return warnings;
}

export function renderComparison(captures) {
  const lines = ['# CairnMP external performance comparison', '',
    'Observed timings only. Each swap chain is separate; select the main gameplay stream before drawing conclusions. No automatic cause or performance gain is inferred.', '',
    'Display intervals describe screen updates; present intervals describe application presents. Neither is a CPU/GPU execution cost. Missing/zero/invalid intervals are excluded and counted in JSON. Percentiles use nearest rank.', '',
    '| Machine / configuration / instrumentation / run | Stream | Seconds of present intervals | Display FPS | Display median / p95 / p99 ms | Display >33.3 / >50 / >100 ms |',
    '| --- | --- | ---: | ---: | --- | --- |'];
  const num = v => v == null ? 'unavailable' : v.toFixed(3);
  const esc = v => String(v ?? '').replace(/[\r\n|<>]/g, ' ');
  for (const capture of captures) {
    const m = capture.manifest;
    for (const s of capture.streams) {
      const d = s.display;
      lines.push(`| ${esc(m.MachineLabel)} / ${esc(m.Configuration)} / ${esc(m.Instrumentation)} / ${m.Run} | ${esc(s.swapChain)} | ${num(s.present.totalMs / 1000)} | ${num(d.averageFps)} | ${num(d.medianMs)} / ${num(d.p95Ms)} / ${num(d.p99Ms)} | ${d.over33Point3Ms} / ${d.over50Ms} / ${d.over100Ms} |`);
    }
  }
  lines.push('', '## Comparability checks', '',
    'Require three complete runs per condition, identical route/save/settings, game build, hardware and PresentMon hash. Compare each machine against its own solo baseline. Discard focus loss, altered settings and partial runs; preserve separate loading/transition captures.', '',
    'Internal recorder overhead: compare disabled, enabled-idle and recording conditions using these external measurements. A difference within the spread of repeats is inconclusive. Inspect internal JSON to align callback scopes with scene events; do not sum nested scopes.', '',
    ...comparisonWarnings(captures).map(warning => `- ${esc(warning)}`), '',
    '## Bottlenecks', '',
    '| Candidate | Reproduction | Evidence | Confidence | Next investigation / correction |',
    '| --- | --- | --- | --- | --- |',
    '| Pending investigation | See capture scenarios | No causal profiling supplied | Unconfirmed | Identify native/managed/GPU hotspots after comparing repeated runs |', '',
    '## Capture metadata', '');
  for (const c of captures) {
    lines.push(`- ${esc(c.source)}: scenario=${esc(c.manifest.Scenario)}; settings=${esc(c.manifest.SettingsNote)}; build=${esc(c.manifest.GameBuild)}; PresentMon SHA256=${esc(c.manifest.PresentMonSha256)}`);
  }
  return lines.join('\n') + '\n';
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const [outputDirectory, ...manifests] = process.argv.slice(2);
    if (!outputDirectory || !manifests.length) throw new Error('Usage: node scripts/compare-performance.js OUTPUT_DIRECTORY MANIFEST.json [MANIFEST.json ...]');
    const captures = manifests.map(loadCapture);
    fs.mkdirSync(outputDirectory, { recursive: true });
    const stem = path.join(outputDirectory, `comparison-${Date.now()}`);
    fs.writeFileSync(stem + '.json', JSON.stringify({ schemaVersion: 1, warnings: comparisonWarnings(captures), captures }, null, 2), { flag: 'wx' });
    fs.writeFileSync(stem + '.md', renderComparison(captures), { flag: 'wx' });
    console.log(stem + '.md');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
