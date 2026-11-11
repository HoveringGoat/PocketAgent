import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay } from './helpers.mjs';

test('provider status distinguishes device pairing from Codex sign-in and model selection', async () => {
  const relay = await startRelay();
  try {
    assert.equal((await relay.request('/provider/status')).status, 401);
    await relay.pair();
    const response = await relay.request('/provider/status');
    assert.equal(
      response.status,
      200,
      'The paired client needs a read-only check of the chat provider process.',
    );
    const status = await response.json();
    assert.equal(status.signInAvailable, true);
    assert.equal(status.authenticationType, 'chatgpt');
    assert.equal(status.configuredModel, 'gpt-6.1-sol');
    assert.equal(status.defaultModel, 'gpt-6-astra');
    assert.equal(status.configuredModelInCatalog, false);
    assert.equal(status.inferenceVerified, false);
    assert.equal(status.models.length, 2, 'Read all model catalog pages.');
    const serialized = JSON.stringify(status);
    assert.equal(serialized.includes('private-test-account@example.com'), false);
    assert.equal(serialized.includes('private-account-id'), false);
    assert.deepEqual(
      await (await relay.request('/conversations')).json(),
      [],
      'Checking status must not create a thread or run a turn.',
    );
  } finally {
    await relay.stop();
  }
});

test('provider status reports missing sign-in on an otherwise paired phone', async () => {
  const relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_SIGNED_OUT: '1' } });
  try {
    await relay.pair();
    const response = await relay.request('/provider/status');
    assert.equal(response.status, 200);
    const status = await response.json();
    assert.equal(status.signInAvailable, false);
    assert.equal(status.authenticationType, null);
    assert.equal(status.requiresOpenaiAuth, true);
  } finally {
    await relay.stop();
  }
});
