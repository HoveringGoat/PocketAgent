import test from 'node:test';
import assert from 'node:assert/strict';
import { request as httpRequest } from 'node:http';
import { readFile } from 'node:fs/promises';
import { crc32 } from 'node:zlib';
import { startRelay, until } from './helpers.mjs';

test('pasted images start and steer turns with bounded, retrievable transcript attachments', async () => {
  const relay = await startRelay();
  try {
    await relay.pairNative();
    const conversation = await (await relay.request('/conversations', {})).json();
    const path = `/conversations/${conversation.id}`;
    const png = await readFile(new URL('../fixture-image.png', import.meta.url));
    const chunk = Buffer.alloc(180_000);
    chunk.writeUInt32BE(chunk.length - 12);
    chunk.write('tEXtNote\0', 4);
    chunk.fill(97, 13, chunk.length - 4);
    chunk.writeUInt32BE(crc32(chunk.subarray(4, -4)), chunk.length - 4);
    const large = Buffer.concat([png.subarray(0, -12), chunk, png.subarray(-12)]);
    const imagePng = large.toString('base64');
    const send = (body) => relay.request(`${path}/messages`, body);
    assert.equal((await send({ imagePng: 'aW52YWxpZA==', clientMessageId: 'bad' })).status, 400);
    assert.equal(
      (
        await send({
          imagePng: Buffer.alloc(2 * 1024 * 1024 + 1).toString('base64'),
          clientMessageId: 'oversized',
        })
      ).status,
      413,
    );

    const first = { text: 'hold', imagePng, clientMessageId: 'pasted-start' };
    assert.equal((await send(first)).status, 202);
    assert.equal((await send(first)).status, 202);
    assert.equal((await send({ ...first, imagePng: png.toString('base64') })).status, 409);
    const running = await until(async () => {
      const snapshot = await (await relay.request(path)).json();
      return (
        snapshot.activeTurnId &&
        snapshot.items.some((item) => item.clientMessageId === first.clientMessageId) &&
        snapshot
      );
    });
    const snapshotImage = running.items.find(
      (item) => item.clientMessageId === first.clientMessageId,
    ).images[0];
    assert.match(snapshotImage.source, /^pocketagent-image:/);
    const image = await relay.request(
      `${path}/images?path=${encodeURIComponent(snapshotImage.source)}`,
    );
    assert.equal(image.headers.get('content-type'), 'image/png');
    assert.deepEqual(Buffer.from(await image.arrayBuffer()), large);
    const other = await (await relay.request('/conversations', {})).json();
    assert.equal(
      (
        await relay.request(
          `/conversations/${other.id}/images?path=${encodeURIComponent(snapshotImage.source)}`,
        )
      ).status,
      404,
    );
    assert.equal(
      (
        await send({
          imagePng,
          clientMessageId: 'pasted-steer',
          expectedTurnId: running.activeTurnId,
        })
      ).status,
      202,
    );
    await until(async () => {
      const snapshot = await (await relay.request(path)).json();
      return snapshot.items.some(
        (item) =>
          item.clientMessageId === 'pasted-steer' && item.text === '' && item.images?.length === 1,
      );
    });
  } finally {
    await relay.stop();
  }
});

test('historical messages without observed times do not invent per-message timestamps', async () => {
  let relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_HISTORICAL: '1' } });
  try {
    await relay.pair();
    const thread = await (await relay.request('/conversations', {})).json();
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    await relay.pair();
    const restored = await (await relay.request(`/conversations/${thread.id}`)).json();
    assert.equal(restored.items.length, 2);
    assert.equal(restored.items[0].timestamp, null);
    assert.equal(restored.items[1].timestamp, null);
    assert.equal(restored.lastWorkDurationMs, 10000);
  } finally {
    await relay.stop();
  }
});

test('native pairing naming and steering preserve the active turn and approval boundary', async () => {
  const relay = await startRelay();
  try {
    const paired = await relay.pairNative();
    assert.equal(typeof paired.token, 'string');
    const created = await (await relay.request('/conversations', {})).json();
    assert.equal(created.title, 'PocketAgent');
    assert.equal(created.repository, 'PocketAgent');
    assert.ok(created.workingDirectory.endsWith('PocketAgent'));
    const second = await (await relay.request('/conversations', {})).json();
    assert.equal(second.title, 'PocketAgent · 2');
    assert.equal(
      (await relay.request(`/conversations/${created.id}/rename`, { title: 'My work' })).status,
      200,
    );
    const path = `/conversations/${created.id}`;
    await relay.request(`${path}/messages`, { text: 'hold' });
    const running = await until(async () => {
      const state = await (await relay.request(path)).json();
      return state.activeTurnId && state;
    });
    assert.equal(
      (await relay.request(`${path}/messages`, { text: 'Unscoped extra turn' })).status,
      409,
    );
    assert.equal(
      (await relay.request(`${path}/messages`, { text: 'Wrong turn', expectedTurnId: 'stale' }))
        .status,
      409,
    );
    assert.equal(
      (
        await relay.request(`${path}/messages`, {
          text: 'Focus on tests',
          expectedTurnId: running.activeTurnId,
        })
      ).status,
      202,
    );
    const steered = await until(async () => {
      const state = await (await relay.request(path)).json();
      return state.items.some((item) => item.text === 'Focus on tests') && state;
    });
    assert.equal(steered.activeTurnId, running.activeTurnId);
    assert.equal(steered.items.filter((item) => item.type === 'userMessage').length, 2);
    await relay.request(`${path}/interrupt`, {});
    await until(async () => (await (await relay.request(path)).json()).status === 'interrupted');
    assert.equal(
      (
        await relay.request(`${path}/messages`, {
          text: 'Late steer',
          expectedTurnId: running.activeTurnId,
        })
      ).status,
      409,
    );
    await relay.request(`${path}/messages`, { text: 'approval' });
    const pending = await until(async () => {
      const state = await (await relay.request(path)).json();
      return state.requests.length && state;
    });
    assert.equal(
      (
        await relay.request(`${path}/messages`, {
          text: 'Change direction',
          expectedTurnId: pending.activeTurnId,
        })
      ).status,
      202,
    );
    const after = await (await relay.request(path)).json();
    assert.equal(after.requests.length, 1);
    assert.equal(after.status, 'awaiting-input');
    assert.equal((await relay.request('/logout', {})).status, 204);
    assert.equal((await relay.request('/conversations')).status, 401);
  } finally {
    await relay.stop();
  }
});

test('public HTTPS origin accepts native clients and rejects untrusted hosts and browser origins', async () => {
  const relay = await startRelay({
    extraEnv: { PocketAgent__PublicOrigin: 'https://pocket.example.com' },
  });
  try {
    const paired = await relay.pairNative();
    const withHost = (host) =>
      new Promise((resolve, reject) => {
        const request = httpRequest(
          relay.origin + '/api/conversations',
          { headers: { Host: host, Authorization: `Bearer ${paired.token}` } },
          (response) => {
            response.resume();
            resolve(response.statusCode);
          },
        );
        request.on('error', reject);
        request.end();
      });
    assert.equal(await withHost('pocket.example.com'), 200);
    assert.equal(await withHost('evil.example'), 400);
    assert.equal(
      (await relay.request('/conversations', {}, { headers: { Origin: 'https://evil.example' } }))
        .status,
      403,
    );
    assert.equal((await relay.request('/conversations', {}, { native: true })).status, 200);
    const root = await fetch(relay.origin);
    assert.equal(
      root.headers.get('content-type')?.includes('text/html'),
      false,
      'No browser app is served.',
    );
  } finally {
    await relay.stop();
  }
});
