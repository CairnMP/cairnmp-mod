import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const version = JSON.parse(readFileSync(resolve(root, 'versions.json'), 'utf8'));
if (!/^\d+\.\d+\.\d+$/.test(version.mod) || !Number.isSafeInteger(version.protocol) || version.protocol < 1)
  throw new Error('Invalid mod/protocol version');

function update(path, replacements) {
  const full = resolve(root, path);
  let text = readFileSync(full, 'utf8');
  for (const [pattern, replacement] of replacements) {
    if (!pattern.test(text)) throw new Error(`Version declaration not found in ${path}`);
    text = text.replace(pattern, replacement);
  }
  if (process.argv.includes('--check')) {
    if (text !== readFileSync(full, 'utf8')) throw new Error(`Version drift in ${path}`);
  } else writeFileSync(full, text);
}
update('CairnMultiplayerShared/Protocol.cs', [
  [/public const int Version = \d+;/, `public const int Version = ${version.protocol};`],
  [/public const string GameVersion = "[^"]+";/, `public const string GameVersion = "${version.mod}";`]
]);
update('CairnMultiplayerMod/Bootstrap/Mod.cs', [
  [/(MelonInfo\(typeof\(CairnMultiplayerMod.Bootstrap.Mod\), "Cairn Multiplayer Mod", ")[^"]+(".*)/, `$1${version.mod}$2`]
]);
