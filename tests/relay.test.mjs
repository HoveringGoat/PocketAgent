import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay, until, root, dotnet } from './helpers.mjs';
import { randomUUID } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { X509Certificate } from 'node:crypto';

test('HTTPS pairing uses generated certificate', async () => {
  const directory = resolve(root, '.local', `https-check-${randomUUID()}`);
  const child = spawn(
    dotnet,
    [
      process.env.POCKETAGENT_TEST_SERVER_DLL ??
        resolve(root, 'src/PocketAgent.Server/bin/Debug/net10.0/PocketAgent.Server.dll'),
      '--setup',
    ],
    {
      cwd: resolve(root, 'src/PocketAgent.Server'),
      windowsHide: true,
      env: {
        ...process.env,
        PocketAgent__ListenAddress: '127.0.0.1',
        PocketAgent__DataDirectory: directory,
        PocketAgent__CertificatePath: '',
      },
      stdio: 'ignore',
    },
  );
  assert.equal(await new Promise((resolve) => child.once('exit', resolve)), 0);
  const certificateAuthority = new X509Certificate(
    await readFile(resolve(directory, 'pocketagent-root.cer')),
  ).toString();
  const relay = await startRelay({
    dataDirectory: directory,
    certificateAuthority,
    extraEnv: { PocketAgent__CertificatePath: resolve(directory, 'server.pfx') },
  });
  try {
    assert.equal((await relay.request('/conversations')).status, 401);
    await relay.pair();
    assert.equal((await relay.request('/conversations')).status, 200);
    assert.equal((await relay.request('/session')).status, 200);
  } finally {
    await relay.stop();
  }
});

test('LAN certificate setup works before a certificate exists', async () => {
  const directory = resolve(root, '.local', `certificate-check-${randomUUID()}`);
  const child = spawn(
    dotnet,
    [
      process.env.POCKETAGENT_TEST_SERVER_DLL ??
        resolve(root, 'src/PocketAgent.Server/bin/Debug/net10.0/PocketAgent.Server.dll'),
      '--setup',
    ],
    {
      cwd: resolve(root, 'src/PocketAgent.Server'),
      windowsHide: true,
      env: {
        ...process.env,
        PocketAgent__ListenAddress: '192.168.1.25',
        PocketAgent__DataDirectory: directory,
        PocketAgent__CertificatePath: '',
      },
      stdio: 'ignore',
    },
  );
  const code = await new Promise((resolve) => child.once('exit', resolve));
  assert.equal(code, 0, 'Certificate setup must not require the certificate it is creating.');
  assert.deepEqual((await readdir(directory)).sort(), ['pocketagent-root.cer', 'server.pfx']);
});

test('relay access and conversation lifecycle', async () => {
  let relay = await startRelay();
  try {
    assert.equal(
      (await relay.request('/conversations')).status,
      401,
      'Unpaired devices must be rejected.',
    );
    const badOrigin = await relay.request(
      '/pair',
      { code: '00000000', deviceName: 'Test' },
      { headers: { Origin: 'https://attacker.example' } },
    );
    assert.equal(badOrigin.status, 403, 'Cross-origin pairing must be rejected.');
    assert.equal(
      (await relay.request('/pair', { code: '00000000', deviceName: 'Test' })).status,
      401,
    );
    const cookie = await relay.pair();
    assert.equal((await relay.request('/session')).status, 200);
    const createdResponse = await relay.request('/conversations', {
      title: 'Focused verification',
    });
    assert.equal(createdResponse.status, 200);
    const created = await createdResponse.json();
    assert.equal((await relay.request('/conversations/foreign-thread')).status, 404);
    assert.equal(
      (await relay.request(`/conversations/${created.id}/messages`, { text: 'approval' })).status,
      202,
    );
    const snapshot = async () => (await relay.request(`/conversations/${created.id}`)).json();
    const waiting = await until(async () => {
      const state = await snapshot();
      return state.status === 'awaiting-input' && state;
    });
    assert.equal(waiting.requests.length, 1);
    assert.equal(
      (await relay.request(`/conversations/${created.id}/messages`, { text: 'duplicate' })).status,
      409,
    );
    const requestUrl = `/conversations/${created.id}/requests/${encodeURIComponent(waiting.requests[0].id)}`;
    assert.equal(
      (await relay.request(requestUrl, { decision: 'acceptForSession' })).status,
      400,
      'Do not expand approval scope.',
    );
    assert.equal((await relay.request(requestUrl, { decision: 'accept' })).status, 202);
    assert.equal(
      (await relay.request(requestUrl, { decision: 'accept' })).status,
      409,
      'An approval can only be answered once.',
    );
    await until(async () => (await snapshot()).status === 'completed');
    assert.equal(
      (await relay.request(`/conversations/${created.id}/messages`, { text: 'questions' })).status,
      202,
    );
    const question = await until(async () => {
      const state = await snapshot();
      return state.requests[0]?.method === 'item/tool/requestUserInput' && state.requests[0];
    });
    assert.equal(
      (
        await relay.request(
          `/conversations/${created.id}/requests/${encodeURIComponent(question.id)}`,
          { answers: { approach: ['Minimal'] } },
        )
      ).status,
      202,
    );
    await until(async () => (await snapshot()).status === 'completed');
    assert.equal(
      (await relay.request(`/conversations/${created.id}/messages`, { text: 'hold' })).status,
      202,
    );
    assert.equal((await relay.request(`/conversations/${created.id}/interrupt`, {})).status, 202);
    await until(async () => (await snapshot()).status === 'interrupted');
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    // Same device session survives a host restart; stored history stays available.
    const historyResponse = await relay.request(`/conversations/${created.id}`, undefined, {
      headers: { Cookie: cookie },
    });
    assert.equal(historyResponse.status, 200);
    assert.ok((await historyResponse.json()).items.some((item) => item.text.includes('Done.')));
    // Session tokens are hashed before they reach the database/WAL.
    const token = cookie.split('=')[1];
    for (const file of await readdir(directory)) {
      if (file.startsWith('pocketagent.db'))
        assert.equal(
          (await readFile(resolve(directory, file))).includes(Buffer.from(token)),
          false,
        );
    }
    assert.equal((await relay.request('/logout', {}, { headers: { Cookie: cookie } })).status, 204);
    assert.equal(
      (await relay.request('/conversations', undefined, { headers: { Cookie: cookie } })).status,
      401,
    );
  } finally {
    await relay.stop();
  }
});

test('pairing attempt limits', async () => {
  const relay = await startRelay();
  try {
    for (let attempt = 0; attempt < 5; attempt++)
      assert.equal(
        (await relay.request('/pair', { code: '00000000', deviceName: 'Test' })).status,
        401,
      );
    assert.equal(
      (await relay.request('/pair', { code: '00000000', deviceName: 'Test' })).status,
      429,
    );
  } finally {
    await relay.stop();
  }
});

test('approval scopes and private request metadata', async () => {
  const relay = await startRelay();
  try {
    await relay.pair();
    const created = await (
      await relay.request('/conversations', { title: 'Approval boundary' })
    ).json();
    await relay.request(`/conversations/${created.id}/messages`, { text: 'restricted' });
    const pending = await until(async () => {
      const snapshot = await (await relay.request(`/conversations/${created.id}`)).json();
      return snapshot.requests[0];
    });
    assert.equal(
      JSON.stringify(pending).includes('fixture-private-metadata'),
      false,
      'Unrecognized provider metadata must stay on the host.',
    );
    assert.deepEqual(pending.decisions, ['decline', 'cancel']);
    assert.equal(
      (
        await relay.request(
          `/conversations/${created.id}/requests/${encodeURIComponent(pending.id)}`,
          { decision: 'accept' },
        )
      ).status,
      400,
    );
  } finally {
    await relay.stop();
  }
});

test('provider resolution and disconnect clear pending controls', async () => {
  const relay = await startRelay();
  try {
    await relay.pair();
    const created = await (
      await relay.request('/conversations', { title: 'Provider lifecycle' })
    ).json();
    const snapshot = async () => (await relay.request(`/conversations/${created.id}`)).json();
    await relay.request(`/conversations/${created.id}/messages`, { text: 'resolved' });
    const pending = await until(async () => (await snapshot()).requests[0]);
    await until(async () => (await snapshot()).status === 'completed');
    assert.equal(
      (
        await relay.request(
          `/conversations/${created.id}/requests/${encodeURIComponent(pending.id)}`,
          { decision: 'accept' },
        )
      ).status,
      409,
    );
    await relay.request(`/conversations/${created.id}/messages`, { text: 'unsupported' });
    const unsupported = await until(async () => (await snapshot()).requests[0]);
    assert.equal(unsupported.supported, false);
    assert.equal(
      (
        await relay.request(
          `/conversations/${created.id}/requests/${encodeURIComponent(unsupported.id)}`,
          { decision: 'accept' },
        )
      ).status,
      409,
    );
    await relay.request(`/conversations/${created.id}/interrupt`, {});
    await until(async () => (await snapshot()).status === 'interrupted');
    await relay.request(`/conversations/${created.id}/messages`, { text: 'drop' });
    await until(async () => (await snapshot()).status === 'disconnected');
    assert.equal((await snapshot()).requests.length, 0);
    assert.equal(
      (await relay.request(`/conversations/${created.id}/messages`, { text: 'retry' })).status,
      409,
    );
  } finally {
    await relay.stop();
  }
});
