import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { parseCsv, summarize, analyzeRows, renderComparison, loadCapture, comparisonWarnings } from './compare-performance.js';

const header = 'Application,ProcessID,SwapChainAddress,Dropped,MsBetweenPresents,MsBetweenDisplayChange';
test('CSV handles BOM, CRLF, quoted commas and escaped quotes', () => {
  const rows = parseCsv('\uFEFF' + header + '\r\n"Cairn, ""test""",42,a,0,16,17\r\n');
  assert.equal(rows[0].Application, 'Cairn, "test"');
  assert.equal(rows[0].MsBetweenDisplayChange, '17');
});
test('malformed or incompatible CSV fails explicitly', () => {
  assert.throws(() => parseCsv('FrameTime\n16'), /Missing/);
  assert.throws(() => parseCsv(header + '\n"broken'), /Unterminated/);
  assert.throws(() => parseCsv(header + '\na,42'), /count mismatch/);
});
test('statistics exclude unavailable values and use total time for FPS', () => {
  const s = summarize([10, 20, 40, 50, 100, 200, NaN, 0, -1, Infinity]);
  assert.equal(s.averageFps, 6000 / 420);
  assert.equal(s.medianMs, 40);
  assert.equal(s.p99Ms, 200);
  assert.equal(s.over50Ms, 2);
  assert.equal(s.over100Ms, 1);
  assert.equal(summarize([]).averageFps, null);
});
test('separates swap chains and process IDs, counts unavailable display data', () => {
  const rows = parseCsv(header + '\nCairn,42,a,0,16,17\nCairn,42,a,1,20,NA\nCairn,42,a,0,16,NA\nCairn,42,b,0,10,10\nOther,99,a,0,1,1');
  const streams = analyzeRows(rows, 42);
  assert.equal(streams.length, 2);
  assert.equal(streams[0].present.samples, 3);
  assert.equal(streams[0].display.samples, 1);
  assert.equal(streams[0].dropped, 1);
  assert.equal(streams[0].unavailableDisplayIntervals, 1);
  assert.throws(() => analyzeRows(rows, 123), /No rows/);
});
test('empty display telemetry remains unavailable rather than zero FPS', () => {
  const streams = analyzeRows(parseCsv(header + '\nCairn,42,a,1,16,NA'), 42);
  const md = renderComparison([{ manifest: { Configuration: 'solo', Run: 1 }, source: 'test', streams }]);
  assert.match(md, /unavailable/);
  assert.match(md, /No causal profiling supplied/);
});

test('comparison warns about partial captures, missing repeats and incompatible metadata', () => {
  const streams = analyzeRows(parseCsv(header + '\nCairn,42,a,0,16,16'), 42);
  const warnings = comparisonWarnings([
    { manifest: { MachineLabel: 'A', Configuration: 'solo', Run: 1, GameBuild: 'one' }, streams },
    { manifest: { MachineLabel: 'A', Configuration: 'solo', Run: 2, GameBuild: 'two' }, streams },
  ]);
  assert.ok(warnings.some(w => w.includes('incomplete')));
  assert.ok(warnings.some(w => w.includes('require exactly runs')));
  assert.ok(warnings.some(w => w.includes('metadata differs')));
});

test('manifest validation and CLI create readable local reports', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cairnmp-performance-test-'));
  try {
    const manifestPath = path.join(directory, 'manifest.json');
    const manifest = { Status: 'captured-unverified', ProcessId: 42, CsvFiles: ['frames.csv'], Configuration: 'solo', Run: 1 };
    fs.writeFileSync(path.join(directory, 'frames.csv'), header + '\nCairn,42,a,0,16,17');
    const writeManifest = () => fs.writeFileSync(manifestPath, '\uFEFF' + JSON.stringify(manifest));
    writeManifest();
    assert.equal(loadCapture(manifestPath).streams.length, 1);
    const output = path.join(directory, 'reports');
    const result = spawnSync(process.execPath, ['scripts/compare-performance.js', output, manifestPath], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const reports = fs.readdirSync(output);
    assert.equal(reports.length, 2);
    assert.equal(JSON.parse(fs.readFileSync(path.join(output, reports.find(f => f.endsWith('.json'))), 'utf8')).captures.length, 1);
    manifest.CsvFiles = ['frames.csv', 'other.csv']; writeManifest();
    assert.throws(() => loadCapture(manifestPath), /exactly one/);
    manifest.CsvFiles = ['../frames.csv']; writeManifest();
    assert.throws(() => loadCapture(manifestPath), /beside/);
    manifest.Status = 'failed'; writeManifest();
    assert.throws(() => loadCapture(manifestPath), /status is failed/);
  } finally {
    assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('cairnmp-performance-test-'));
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
