#!/usr/bin/env node
// Propage les versions de versions.json vers les fichiers source du mod.
// Usage: node scripts/sync-versions.js

import { readFileSync, writeFileSync } from "fs";
import { resolve, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, "..");

const versions = JSON.parse(readFileSync(resolve(root, "versions.json"), "utf8"));

let errors = 0;

function patch(relPath, transform) {
  const abs = resolve(root, relPath);
  const before = readFileSync(abs, "utf8");
  const after = transform(before);
  if (before === after) {
    console.log(`  (unchanged) ${relPath}`);
    return;
  }
  writeFileSync(abs, after, "utf8");
  console.log(`  updated     ${relPath}`);
}

function patchRegex(relPath, regex, replacement) {
  patch(relPath, (src) => {
    if (!regex.test(src)) {
      console.error(`  ERROR: pattern not found in ${relPath}`);
      errors++;
      return src;
    }
    return src.replace(regex, replacement);
  });
}

// ── Mod (C#) ──────────────────────────────────────────────────────────────────
console.log("\n[mod]");
patchRegex(
  "CairnMultiplayerMod/Core/Mod.cs",
  /(\[assembly: MelonInfo\(typeof\(CairnMultiplayerMod\.Core\.Mod\), "Cairn Multiplayer Mod", )".*?"(, "CairnModTeam"\)\])/,
  `$1"${versions.mod}"$2`
);

// ── Protocol version (C#) ─────────────────────────────────────────────────────
console.log("\n[protocol]");
patchRegex(
  "CairnMultiplayerShared/Protocol.cs",
  /public const int Version = \d+;/,
  `public const int Version = ${versions.protocol};`
);
patchRegex(
  "CairnMultiplayerShared/Protocol.cs",
  /public const string GameVersion = ".*?";/,
  `public const string GameVersion = "${versions.mod}";`
);

// ── Done ──────────────────────────────────────────────────────────────────────
console.log();
if (errors > 0) {
  console.error(`Done with ${errors} error(s). Check output above.`);
  process.exit(1);
} else {
  console.log("All versions synced successfully.");
}
