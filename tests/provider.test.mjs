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

test('provider status identifies Platform API-key authentication without exposing the key', async () => {
  const relay = await startRelay({
    extraEnv: {
      POCKETAGENT_FIXTURE_AUTH_TYPE: 'apiKey',
      OPENAI_API_KEY: 'fixture-api-key-that-must-not-appear',
    },
  });
  try {
    await relay.pair();
    const status = await (await relay.request('/provider/status')).json();
    assert.equal(status.authenticationType, 'apiKey');
    assert.equal(status.credentialPresent, true);
    assert.equal(status.credentialEnvironmentVariable, 'OPENAI_API_KEY');
    assert.equal(JSON.stringify(status).includes('fixture-api-key-that-must-not-appear'), false);
  } finally {
    await relay.stop();
  }
});

test('explicit provider verification proves inference access', async () => {
  const relay = await startRelay();
  try {
    await relay.pair();
    const response = await relay.request('/provider/verify', {});
    assert.equal(response.status, 200);
    const result = await response.json();
    assert.equal(result.verified, true);
    assert.equal(result.message, 'Inference access verified.');
  } finally {
    await relay.stop();
  }
});

test('provider verification reports project access failures without retrying', async () => {
  const relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_PROJECT_ERROR: '1' } });
  try {
    await relay.pair();
    const result = await (await relay.request('/provider/verify', {})).json();
    assert.equal(result.verified, false);
    assert.equal(result.providerStatus, 401);
    assert.match(result.message, /project tied to the API key/);
  } finally {
    await relay.stop();
  }
});

test('provider verification classifies deferred project access failures', async () => {
  const relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_PROJECT_ERROR_AFTER_ACCEPT: '1' } });
  try {
    await relay.pair();
    const result = await (await relay.request('/provider/verify', {})).json();
    assert.equal(result.verified, false);
    assert.equal(result.providerStatus, 401);
  } finally {
    await relay.stop();
  }
});
