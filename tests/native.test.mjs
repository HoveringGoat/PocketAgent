import test from 'node:test';
import assert from 'node:assert/strict';
import { request as httpRequest } from 'node:http';
import { startRelay, until } from './helpers.mjs';

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
