import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay, until } from './helpers.mjs';

test('usage failure after work started never queues a replay of the original prompt', async () => {
  const relay = await startRelay();
  try {
    await relay.pair();
    const thread = await (await relay.request('/conversations', {})).json();
    await relay.request(`/conversations/${thread.id}/messages`, { text: 'quota-after-work' });
    await until(
      async () =>
        (await (await relay.request(`/conversations/${thread.id}`)).json()).status === 'failed',
    );
    assert.deepEqual(
      await (await relay.request(`/conversations/${thread.id}/scheduled`)).json(),
      [],
    );
  } finally {
    await relay.stop();
  }
});

test('usage deferral uses the latest exhausted window and never invents missing reset times', async () => {
  for (const unknown of [false, true]) {
    const relay = await startRelay({
      extraEnv: {
        POCKETAGENT_FIXTURE_EXHAUSTED: '1',
        ...(unknown
          ? { POCKETAGENT_FIXTURE_USAGE_WITHOUT_RESET: '1' }
          : { POCKETAGENT_FIXTURE_WEEKLY_EXHAUSTED: '1' }),
      },
    });
    try {
      await relay.pair();
      const usage = await (await relay.request('/usage')).json();
      assert.equal(JSON.stringify(usage).includes('private-account-id'), false);
      const thread = await (await relay.request('/conversations', {})).json();
      const receipt = await (
        await relay.request(`/conversations/${thread.id}/messages`, {
          text: 'After all windows reset',
        })
      ).json();
      assert.equal(receipt.queued, true);
      if (unknown) assert.equal(receipt.dueAt, null);
      else assert.ok(Date.parse(receipt.dueAt) > Date.now() + 23 * 60 * 60 * 1000);
    } finally {
      await relay.stop();
    }
  }
});

test('unavailable usage is explicit and does not disable ordinary conversation input', async () => {
  const relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_USAGE_UNSUPPORTED: '1' } });
  try {
    await relay.pair();
    const usage = await (await relay.request('/usage')).json();
    assert.equal(usage.available, false);
    assert.equal(usage.fiveHour, null);
    assert.equal(usage.weekly, null);
    const thread = await (await relay.request('/conversations', {})).json();
    const receipt = await (
      await relay.request(`/conversations/${thread.id}/messages`, { text: 'hold' })
    ).json();
    assert.equal(receipt.queued, false);
  } finally {
    await relay.stop();
  }
});

test('usage windows and durable scheduled messages dispatch once and can be canceled', async () => {
  let relay = await startRelay();
  try {
    assert.equal((await relay.request('/usage')).status, 401);
    await relay.pair();
    const usage = await (await relay.request('/usage')).json();
    assert.equal(usage.fiveHour.remainingPercent, 75);
    assert.equal(usage.weekly.remainingPercent, 40);
    const thread = await (await relay.request('/conversations', {})).json();
    const response = await relay.request(`/conversations/${thread.id}/scheduled`, {
      text: 'hold',
      dueAt: new Date(Date.now() + 3000).toISOString(),
    });
    assert.equal(response.status, 200);
    const queued = await response.json();
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    await relay.pair();
    await until(
      async () =>
        (await (await relay.request(`/conversations/${thread.id}/scheduled`)).json()).find(
          (job) => job.id === queued.id,
        )?.status === 'submitted',
    );
    const state = await (await relay.request(`/conversations/${thread.id}`)).json();
    assert.equal(state.items.filter((item) => item.type === 'userMessage').length, 1);
    assert.ok(state.items[0].timestamp);
    const next = await (
      await relay.request(`/conversations/${thread.id}/scheduled`, {
        text: 'never submit',
        dueAt: new Date(Date.now() + 60000).toISOString(),
      })
    ).json();
    assert.equal((await relay.request(`/scheduled/${next.id}/cancel`, {})).status, 204);
    assert.equal((await relay.request(`/scheduled/${queued.id}/cancel`, {})).status, 409);
  } finally {
    await relay.stop();
  }
});

test('exhausted usage queues until reset and revocation cancels pending work', async () => {
  let relay = await startRelay({ extraEnv: { POCKETAGENT_FIXTURE_EXHAUSTED: '1' } });
  try {
    await relay.pair();
    const thread = await (await relay.request('/conversations', {})).json();
    const sent = await relay.request(`/conversations/${thread.id}/messages`, {
      text: 'After reset',
    });
    assert.equal(sent.status, 202);
    const receipt = await sent.json();
    assert.equal(receipt.queued, true);
    assert.equal(
      (await (await relay.request(`/conversations/${thread.id}`)).json()).items.length,
      0,
    );
    const jobs = await (await relay.request(`/conversations/${thread.id}/scheduled`)).json();
    assert.equal(jobs.length, 1);
    assert.equal(jobs[0].reason, 'usage-reset');
    await relay.request('/logout', {});
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    await relay.pair();
    const canceled = await (await relay.request(`/conversations/${thread.id}/scheduled`)).json();
    assert.equal(canceled[0].status, 'canceled');
  } finally {
    await relay.stop();
  }
});

test('scheduled delivery with a lost acknowledgement is never replayed after restart', async () => {
  let relay = await startRelay();
  try {
    await relay.pair();
    const thread = await (await relay.request('/conversations', {})).json();
    await relay.request(`/conversations/${thread.id}/scheduled`, {
      text: 'drop-before-ack',
      dueAt: new Date(Date.now() + 500).toISOString(),
    });
    await until(
      async () =>
        (await (await relay.request(`/conversations/${thread.id}/scheduled`)).json())[0]?.status ===
        'uncertain',
    );
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    await relay.pair();
    const job = (await (await relay.request(`/conversations/${thread.id}/scheduled`)).json())[0];
    assert.equal(job.status, 'uncertain');
    const state = await (await relay.request(`/conversations/${thread.id}`)).json();
    assert.equal(state.items.filter((item) => item.type === 'userMessage').length, 1);
  } finally {
    await relay.stop();
  }
});

test('a provider-reported usage failure after acceptance queues the unexecuted prompt once', async () => {
  const relay = await startRelay();
  try {
    await relay.pair();
    const thread = await (await relay.request('/conversations', {})).json();
    assert.equal(
      (await relay.request(`/conversations/${thread.id}/messages`, { text: 'quota-after-accept' }))
        .status,
      202,
    );
    const jobs = await until(async () => {
      const list = await (await relay.request(`/conversations/${thread.id}/scheduled`)).json();
      return list.length && list;
    });
    assert.equal(jobs.length, 1);
    assert.equal(jobs[0].text, 'quota-after-accept');
    assert.equal(jobs[0].status, 'pending');
  } finally {
    await relay.stop();
  }
});
