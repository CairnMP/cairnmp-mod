import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { discordPayload, linkify, truncate, unwrap } from './discord-release.js';

const script = fileURLToPath(new URL('./discord-release.js', import.meta.url));
const url = 'https://github.com/CairnMP/cairnmp-mod/releases/tag/v2.4.2';
const repository = 'https://github.com/CairnMP/cairnmp-mod';

function run(env, releasePath) {
  return new Promise(resolve => {
    execFile(process.execPath, [script, releasePath], { env: { ...process.env, ...env } },
      (error, stdout, stderr) => resolve({ code: error ? error.code : 0, stdout, stderr }));
  });
}

function releaseFile(release) {
  const file = path.join(mkdtempSync(path.join(tmpdir(), 'discord-release-')), 'release.json');
  writeFileSync(file, JSON.stringify(release));
  return file;
}

test('wrapped paragraphs, quotes and bullets go back on one line', () => {
  const notes = [
    '> The first stable build.',
    '> The protocol is **16**.',
    '',
    '### Fixed',
    '',
    '- CairnMP no longer',
    '  crashes.',
    '- Second entry.',
    '',
    'A paragraph',
    'on two lines.',
  ].join('\r\n');
  assert.equal(unwrap(notes), [
    '> The first stable build. The protocol is **16**.',
    '',
    '### Fixed',
    '',
    '- CairnMP no longer crashes.',
    '- Second entry.',
    '',
    'A paragraph on two lines.',
  ].join('\n'));
});

test('a heading is never joined to the next line and code fences stay as written', () => {
  assert.equal(unwrap('### Fixed\n- Entry'), '### Fixed\n- Entry');
  assert.equal(unwrap('```\nfirst\nsecond\n```'), '```\nfirst\nsecond\n```');
});

test('contributors and pull requests become links, code and headings do not', () => {
  assert.equal(
    linkify('- @Tippy-Tipster made their first contribution in #33', repository),
    `- [@Tippy-Tipster](https://github.com/Tippy-Tipster) made their first contribution in [#33](${repository}/pull/33)`);
  assert.equal(linkify('### Fixed `@keep` a@b.c v#2', repository), '### Fixed `@keep` a@b.c v#2');
});

test('notes over the Discord limit are cut between lines and point to the release', () => {
  const line = 'x'.repeat(99);
  const notes = Array(100).fill(line).join('\n');
  const cut = truncate(notes, url);
  assert.ok(cut.length <= 4096, `length ${cut.length}`);
  assert.ok(cut.endsWith(`[Read the full notes](${url})`));
  assert.ok(cut.split('\n').slice(0, -2).every(kept => kept === line));
  assert.equal(truncate('short', url), 'short');
});

test('a single line over the limit is still cut to size', () => {
  assert.ok(truncate('x'.repeat(5000), url).length <= 4096);
});

test('the channel sets the colour and nothing can ping', () => {
  const release = { name: 'CairnMP 2.4.2 (beta)', body: 'Hello @everyone', url, isPrerelease: true };
  const beta = discordPayload(release);
  const stable = discordPayload({ ...release, isPrerelease: false });
  assert.deepEqual(beta.allowed_mentions, { parse: [] });
  assert.equal(beta.embeds[0].title, 'CairnMP 2.4.2 (beta)');
  assert.equal(beta.embeds[0].url, url);
  assert.notEqual(beta.embeds[0].color, stable.embeds[0].color);
});

test('without the secret, the script sends nothing and succeeds', async () => {
  const result = await run({ DISCORD_WEBHOOK_URL: '' }, releaseFile({}));
  assert.equal(result.code, 0);
  assert.match(result.stdout, /not set/);
});

async function withWebhook(status, reply, assertions) {
  const received = [];
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      received.push(JSON.parse(body));
      response.statusCode = status;
      response.end(reply);
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await assertions(`http://127.0.0.1:${server.address().port}/webhook`, received);
  } finally { server.close(); }
}

const published = { name: 'CairnMP 2.4.2 (beta)', body: '### Fixed\n\n- Ropes.', url, isPrerelease: true };

test('the release is posted to the webhook', () => withWebhook(204, '', async (webhook, received) => {
  const result = await run({ DISCORD_WEBHOOK_URL: webhook }, releaseFile(published));
  assert.equal(result.code, 0, result.stderr);
  assert.equal(received.length, 1);
  assert.equal(received[0].embeds[0].description, '### Fixed\n\n- Ropes.');
}));

test('a refusal from Discord fails the job', () => withWebhook(400, 'bad embed', async webhook => {
  const result = await run({ DISCORD_WEBHOOK_URL: webhook }, releaseFile(published));
  assert.equal(result.code, 1);
  assert.match(result.stderr, /HTTP 400 bad embed/);
}));
