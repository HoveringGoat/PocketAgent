import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay, until } from './helpers.mjs';

// Explicit opt-in: this starts one real, potentially billable model turn using host settings.
test(
  'live Codex one message',
  { skip: process.env.POCKETAGENT_LIVE_CHECK !== '1', timeout: 150_000 },
  async () => {
    const relay = await startRelay({ realCodex: true });
    try {
      await relay.pair();
      const response = await relay.request('/conversations', {
        title: 'PocketAgent connection check',
      });
      const conversation = await response.json();
      assert.equal(response.status, 200, conversation.error);
      const sent = await relay.request(`/conversations/${conversation.id}/messages`, {
        text: 'This is a connection check. Reply with exactly: PocketAgent connected. Do not use tools, inspect files, or modify anything.',
      });
      assert.equal(sent.status, 202);
      const final = await until(async () => {
        const state = await (await relay.request(`/conversations/${conversation.id}`)).json();
        return ['completed', 'failed', 'disconnected'].includes(state.status) && state;
      }, 120_000);
      assert.equal(final.status, 'completed', final.error ?? 'The live turn did not complete.');
      assert.ok(
        final.items.some(
          (item) => item.type === 'agentMessage' && item.text.includes('PocketAgent connected.'),
        ),
      );
    } finally {
      await relay.stop();
    }
  },
);
