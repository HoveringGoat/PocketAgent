import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay } from './helpers.mjs';

test('published native artifact serves metadata and protected API without a browser client', async () => {
  const relay = await startRelay({ published: true });
  try {
    const response = await fetch(relay.origin);
    assert.equal(response.status, 200);
    const metadata = await response.json();
    assert.equal(metadata.application, 'PocketAgent');
    assert.equal(metadata.client, 'Native Android app');
    assert.equal((await fetch(relay.origin + '/index.html')).status, 404);
    assert.equal((await relay.request('/conversations')).status, 401);
    await relay.pair();
    assert.equal((await relay.request('/conversations')).status, 200);
  } finally {
    await relay.stop();
  }
});
