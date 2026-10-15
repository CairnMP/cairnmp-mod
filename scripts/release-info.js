import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Everything a release needs, read from what a maintainer already writes: the version
// comes from versions.json, the channel and the notes from the CHANGELOG heading of
// that version — "## [2.3.1] — 2026-10-20 (beta)" is published as a GitHub pre-release,
// which only the launcher's beta channel installs.

export function changelogSection(changelog, version) {
  const lines = changelog.replace(/\r/g, '').split('\n');
  const start = lines.findIndex(line => line.startsWith(`## [${version}]`));
  if (start < 0) return null;
  let end = lines.findIndex((line, index) => index > start && line.startsWith('## ['));
  if (end < 0) end = lines.length;
  const body = lines.slice(start + 1, end);
  while (body.length && /^\s*(---)?\s*$/.test(body[body.length - 1])) body.pop();
  while (body.length && !body[0].trim()) body.shift();
  return { heading: lines[start], notes: body.join('\n') };
}

export function releaseInfo({ tag, modVersion, changelog }) {
  // A suffix ("v2.2.16-r2") republishes the same version; the archive keeps its name.
  if (tag !== `v${modVersion}` && !tag.startsWith(`v${modVersion}-`))
    throw new Error(`Tag ${tag} does not match versions.json (mod ${modVersion}). ` +
      'Bump versions.json, run node scripts/sync-versions.js, commit, then tag v' + modVersion + '.');
  const section = changelogSection(changelog, modVersion);
  if (!section)
    throw new Error(`CHANGELOG.md has no "## [${modVersion}]" section. Rename [Unreleased] before tagging.`);
  if (!section.notes.trim())
    throw new Error(`The CHANGELOG.md section for ${modVersion} is empty.`);
  const prerelease = /\(beta\)/i.test(section.heading);
  return {
    version: modVersion,
    prerelease,
    title: `CairnMP ${modVersion}${prerelease ? ' (beta)' : ''}`,
    notes: section.notes + '\n',
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const [tag, notesPath] = process.argv.slice(2);
    if (!tag || !notesPath) throw new Error('Usage: node scripts/release-info.js TAG NOTES_OUTPUT.md');
    const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
    const info = releaseInfo({
      tag,
      modVersion: JSON.parse(readFileSync(path.join(root, 'versions.json'), 'utf8')).mod,
      changelog: readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8'),
    });
    writeFileSync(notesPath, info.notes);
    // GITHUB_OUTPUT format, one key per line.
    console.log(`version=${info.version}\nprerelease=${info.prerelease}\ntitle=${info.title}`);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
