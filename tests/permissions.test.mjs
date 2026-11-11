import test from 'node:test';
import assert from 'node:assert/strict';
import { startRelay, until } from './helpers.mjs';

for (const mode of ['inherit', 'ask', 'full-access']) {
  test(`desktop permission mode ${mode} reaches new and resumed provider turns`, async () => {
    const options = { PocketAgent__PermissionMode: mode, POCKETAGENT_EXPECT_PERMISSION_MODE: mode };
    let relay = await startRelay({ extraEnv: options });
    try {
      await relay.pair();
      const response = await relay.request('/conversations', { permissionMode: 'full-access' });
      assert.equal(response.status, 200);
      const created = await response.json();
      assert.equal(created.permissionMode, mode);
      assert.equal(
        created.sandboxMode,
        mode === 'full-access' ? 'dangerFullAccess' : 'workspaceWrite',
      );
      assert.equal(created.approvalPolicy, mode === 'full-access' ? 'never' : 'on-request');
      const path = `/conversations/${created.id}`;
      assert.equal((await relay.request(`${path}/messages`, { text: 'hold' })).status, 202);
      await relay.request(`${path}/interrupt`, {});
      await until(async () => (await (await relay.request(path)).json()).status === 'interrupted');
      const directory = relay.directory;
      await relay.stop();
      relay = await startRelay({ dataDirectory: directory, extraEnv: options });
      await relay.pair();
      assert.equal((await relay.request(`${path}/messages`, { text: 'hold' })).status, 202);
      const resumed = await (await relay.request(path)).json();
      assert.equal(resumed.permissionMode, mode);
      assert.equal(resumed.approvalPolicy, created.approvalPolicy);
    } finally {
      await relay.stop();
    }
  });
}
