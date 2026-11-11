import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Announces a published release on Discord, from the release as GitHub holds it
// (`gh release view --json name,body,url,isPrerelease`). The webhook URL comes from the
// DISCORD_WEBHOOK_URL secret; without it, nothing is sent and the release is unaffected.

const DESCRIPTION_LIMIT = 4096; // Discord refuses a longer embed description.
const TITLE_LIMIT = 256;
const BETA_COLOR = 0xf0a020;
const STABLE_COLOR = 0x2ecc71;

// The changelog is hard-wrapped for reading as a file. Discord keeps every line break,
// so a wrapped paragraph has to be put back on one line.
export function unwrap(markdown) {
  const lines = [];
  let fenced = false;
  for (const raw of markdown.replace(/\r/g, '').split('\n')) {
    const line = raw.trimEnd();
    const text = line.trim();
    if (text.startsWith('```')) { fenced = !fenced; lines.push(line); continue; }
    const previous = lines[lines.length - 1];
    const startsBlock = !text || /^(#|[-*] |\d+\. |\|)/.test(text);
    const canContinue = !fenced && previous && !previous.trim().startsWith('#') && !previous.trim().startsWith('```');
    if (text.startsWith('>')) {
      if (canContinue && previous.startsWith('>')) lines[lines.length - 1] = `${previous} ${text.replace(/^>\s*/, '')}`;
      else lines.push(line);
    } else if (canContinue && !startsBlock) {
      lines[lines.length - 1] = `${previous} ${text}`;
    } else {
      lines.push(line);
    }
  }
  return lines.join('\n');
}

// GitHub links "@name" and "#12" in its own page; Discord shows them as plain text.
export function linkify(markdown, repositoryUrl) {
  return markdown
    .replace(/(^|[\s(])@([a-z\d][a-z\d-]*)/gim, '$1[@$2](https://github.com/$2)')
    .replace(/(^|[\s(])#(\d+)\b/gm, `$1[#$2](${repositoryUrl}/pull/$2)`);
}

export function truncate(markdown, url) {
  if (markdown.length <= DESCRIPTION_LIMIT) return markdown;
  const more = `\n\n[Read the full notes](${url})`;
  const room = DESCRIPTION_LIMIT - more.length;
  const lineEnd = markdown.lastIndexOf('\n', room);
  return markdown.slice(0, lineEnd > 0 ? lineEnd : room).trimEnd() + more;
}

export function discordPayload({ name, body, url, isPrerelease }) {
  const repositoryUrl = url.split('/releases/')[0];
  return {
    // Release notes never ping anyone, whatever they contain (@everyone included).
    allowed_mentions: { parse: [] },
    embeds: [{
      title: name.slice(0, TITLE_LIMIT),
      url,
      description: truncate(linkify(unwrap(body), repositoryUrl), url),
      color: isPrerelease ? BETA_COLOR : STABLE_COLOR,
    }],
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const [releasePath] = process.argv.slice(2);
    if (!releasePath) throw new Error('Usage: node scripts/discord-release.js RELEASE.json');
    const webhook = process.env.DISCORD_WEBHOOK_URL;
    if (!webhook) {
      console.log('::notice::The DISCORD_WEBHOOK_URL secret is not set: this release was not announced.');
    } else {
      const release = JSON.parse(readFileSync(releasePath, 'utf8'));
      const response = await fetch(webhook, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(discordPayload(release)),
      });
      if (!response.ok) throw new Error(`Discord refused the announcement: HTTP ${response.status} ${await response.text()}`);
      console.log(`Announced ${release.name} on Discord.`);
    }
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
