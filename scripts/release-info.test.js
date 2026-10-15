import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { changelogSection, releaseInfo } from './release-info.js';

const changelog = [
  '# Changelog',
  '',
  '## [Unreleased]',
  '',
  '- Not yet.',
  '',
  '---',
  '',
  '## [2.3.1] — 2026-10-20 (beta)',
  '',
  '> Protocol **15**.',
  '',
  '### Fixed',
  '',
  '- Ropes.',
  '',
  '---',
  '',
  '## [2.2.17] — 2026-09-18 (stable)',
  '',
  '### Fixed',
  '',
  '- Voice.',
  '',
].join('\r\n');

test('a beta heading publishes a pre-release with the section as notes', () => {
  const info = releaseInfo({ tag: 'v2.3.1', modVersion: '2.3.1', changelog });
  assert.equal(info.prerelease, true);
  assert.equal(info.title, 'CairnMP 2.3.1 (beta)');
  assert.equal(info.notes, '> Protocol **15**.\n\n### Fixed\n\n- Ropes.\n');
});

test('any other heading publishes a stable release, last section included', () => {
  const info = releaseInfo({ tag: 'v2.2.17', modVersion: '2.2.17', changelog });
  assert.equal(info.prerelease, false);
  assert.equal(info.title, 'CairnMP 2.2.17');
  assert.equal(info.notes, '### Fixed\n\n- Voice.\n');
});

test('a suffixed tag republishes the version it names', () => {
  assert.equal(releaseInfo({ tag: 'v2.2.17-r2', modVersion: '2.2.17', changelog }).version, '2.2.17');
});

test('a tag that disagrees with versions.json is refused', () => {
  assert.throws(() => releaseInfo({ tag: 'v2.3.2', modVersion: '2.3.1', changelog }), /does not match/);
  assert.throws(() => releaseInfo({ tag: 'v2.3.10', modVersion: '2.3.1', changelog }), /does not match/);
  assert.throws(() => releaseInfo({ tag: '2.3.1', modVersion: '2.3.1', changelog }), /does not match/);
});

test('a version without notes is refused', () => {
  assert.throws(() => releaseInfo({ tag: 'v2.4.0', modVersion: '2.4.0', changelog }), /no "## \[2\.4\.0\]"/);
  assert.throws(() => releaseInfo({ tag: 'v1.0.0', modVersion: '1.0.0', changelog: '## [1.0.0]\n\n---\n' }), /empty/);
});

test('a version prefix does not match a longer version', () => {
  assert.equal(changelogSection('## [2.3.10] — x\n\n- a\n', '2.3.1'), null);
});

test('every released heading in the real changelog yields notes', () => {
  const real = fs.readFileSync(new URL('../CHANGELOG.md', import.meta.url), 'utf8');
  const versions = [...real.matchAll(/^## \[(\d+\.\d+\.\d+)\]/gm)].map(match => match[1]);
  assert.ok(versions.length > 0);
  for (const version of versions) {
    const info = releaseInfo({ tag: `v${version}`, modVersion: version, changelog: real });
    assert.ok(info.notes.trim(), version);
  }
  assert.equal(releaseInfo({ tag: 'v2.3.0', modVersion: '2.3.0', changelog: real }).prerelease, true);
  assert.equal(releaseInfo({ tag: 'v2.2.17', modVersion: '2.2.17', changelog: real }).prerelease, false);
});
