import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, basename } from 'node:path';
import { randomUUID } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { spawn } from 'node:child_process';
import { startRelay, until, root, dotnet } from './helpers.mjs';

test(
  'a second host cannot migrate the database while another version owns the runtime',
  { skip: process.platform !== 'win32' },
  async () => {
    const directory = resolve(root, '.local', `upgrade-lease-${randomUUID()}`);
    await mkdir(directory, { recursive: true });
    const database = new DatabaseSync(resolve(directory, 'pocketagent.db'));
    database.exec(
      'CREATE TABLE conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at INTEGER NOT NULL, repository TEXT NOT NULL)',
    );
    const owner = spawn(
      'powershell.exe',
      [
        '-NoProfile',
        '-Command',
        '$lease = [IO.File]::Open($env:POCKETAGENT_TEST_LOCK, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); [Console]::WriteLine("locked"); [Console]::ReadLine() | Out-Null; $lease.Dispose()',
      ],
      {
        windowsHide: true,
        env: { ...process.env, POCKETAGENT_TEST_LOCK: resolve(directory, 'host.lock') },
        stdio: ['pipe', 'pipe', 'ignore'],
      },
    );
    let locked = false;
    owner.stdout.on('data', (chunk) => {
      if (chunk.toString().includes('locked')) locked = true;
    });
    let challenger;
    try {
      await until(() => locked);
      challenger = spawn(
        dotnet,
        [
          process.env.POCKETAGENT_TEST_SERVER_DLL ??
            resolve(root, 'src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll'),
        ],
        {
          cwd: root,
          windowsHide: true,
          env: {
            ...process.env,
            PocketAgent__RepositoryPath: root,
            PocketAgent__DataDirectory: directory,
            PocketAgent__ListenAddress: '127.0.0.1',
            PocketAgent__Port: '65148',
          },
          stdio: 'ignore',
        },
      );
      await until(() => challenger.exitCode !== null);
      assert.notEqual(challenger.exitCode, 0);
      assert.deepEqual(
        database
          .prepare('PRAGMA table_info(conversations)')
          .all()
          .map((column) => column.name),
        ['id', 'title', 'created_at', 'repository'],
        'A failed new host must not change an active older host\u2019s database schema.',
      );
    } finally {
      if (challenger && challenger.exitCode === null) challenger.kill();
      owner.stdin.end('\n');
      await new Promise((resolve) => owner.once('exit', resolve));
      database.close();
    }
  },
);

test('conversation folders and models remain independent across turns and restarts', async () => {
  const options = {
    PocketAgent__PermissionMode: 'ask',
    POCKETAGENT_EXPECT_PERMISSION_MODE: 'ask',
  };
  let relay = await startRelay({ extraEnv: options });
  try {
    assert.equal((await relay.request('/directories')).status, 401);
    assert.equal((await relay.request('/models')).status, 401);
    await relay.pairNative();
    const folder = resolve(relay.directory, 'Different workspace');
    await mkdir(folder);
    const listing = await (
      await relay.request(`/directories?path=${encodeURIComponent(relay.directory)}`)
    ).json();
    assert.ok(listing.directories.some((entry) => entry.path === folder));
    assert.equal((await relay.request('/directories?path=relative')).status, 400);
    assert.equal(
      (await relay.request(`/directories?path=${encodeURIComponent('\\\\host\\share')}`)).status,
      400,
    );
    assert.equal(
      (await relay.request('/conversations', { workingDirectory: resolve(folder, 'missing') }))
        .status,
      404,
    );
    assert.equal(
      (await relay.request('/conversations', { model: 'not-a-server-model' })).status,
      400,
    );
    const models = await (await relay.request('/models')).json();
    assert.equal(models.length, 2, 'Read every page of the server model catalog.');
    const first = await (
      await relay.request('/conversations', { workingDirectory: folder, model: models[0].model })
    ).json();
    const second = await (await relay.request('/conversations', {})).json();
    assert.equal(first.title, basename(folder));
    assert.equal(first.workingDirectory, folder);
    assert.equal(second.workingDirectory, root);
    assert.equal(second.model, null);
    const path = `/conversations/${first.id}`;
    await relay.request(`${path}/messages`, { text: 'hold' });
    const running = await until(async () => {
      const state = await (await relay.request(path)).json();
      return state.activeTurnId && state;
    });
    assert.equal((await relay.request(`${path}/model`, { model: models[1].model })).status, 409);
    assert.equal((await relay.request(`${path}/archive`, {})).status, 409);
    const text = 'Keep the conversation\u2019s folder \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00';
    await relay.request(`${path}/messages`, { text, expectedTurnId: running.activeTurnId });
    await until(async () =>
      (await (await relay.request(path)).json()).items.some((item) => item.text === text),
    );
    await relay.request(`${path}/interrupt`, {});
    await until(async () => (await (await relay.request(path)).json()).status === 'interrupted');
    assert.equal((await relay.request(`${path}/model`, { model: models[1].model })).status, 200);
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({
      dataDirectory: directory,
      extraEnv: { ...options, PocketAgent__RepositoryPath: directory },
    });
    await relay.pairNative();
    assert.equal((await (await relay.request('/conversations')).json()).length, 2);
    assert.equal((await relay.request(`${path}/messages`, { text: 'hold' })).status, 202);
    const provider = JSON.parse(await readFile(resolve(directory, 'fixture-history.json'), 'utf8'));
    assert.equal(provider[first.id].cwd, folder);
    assert.equal(provider[first.id].model, models[1].model);
    assert.equal(provider[second.id].cwd, root);
    assert.equal((await (await relay.request(path)).json()).workingDirectory, folder);
  } finally {
    await relay.stop();
  }
});

test('archive hides idle conversations, cancels pending schedules and restores without replay', async () => {
  let relay = await startRelay();
  try {
    await relay.pairNative();
    const thread = await (await relay.request('/conversations', {})).json();
    const path = `/conversations/${thread.id}`;
    const dueAt = new Date(Date.now() + 3600000).toISOString();
    await relay.request(`${path}/scheduled`, { text: 'Future work', dueAt });
    const database = new DatabaseSync(resolve(relay.directory, 'pocketagent.db'));
    try {
      database
        .prepare("UPDATE scheduled_messages SET status='dispatching' WHERE conversation_id=?")
        .run(thread.id);
      assert.equal(
        (await relay.request(`${path}/archive`, {})).status,
        409,
        'Do not archive during an unacknowledged scheduled delivery.',
      );
      database
        .prepare("UPDATE scheduled_messages SET status='pending' WHERE conversation_id=?")
        .run(thread.id);
    } finally {
      database.close();
    }
    assert.equal((await relay.request(`${path}/archive`, {})).status, 200);
    assert.deepEqual(await (await relay.request('/conversations')).json(), []);
    assert.equal(
      (await (await relay.request('/conversations?archived=true')).json())[0].id,
      thread.id,
    );
    assert.equal((await (await relay.request(path)).json()).archived, true);
    assert.equal((await relay.request(`${path}/messages`, { text: 'hold' })).status, 409);
    assert.equal(
      (await relay.request(`${path}/scheduled`, { text: 'More work', dueAt })).status,
      409,
    );
    assert.equal((await relay.request(`${path}/model`, { model: 'gpt-6-astra' })).status, 409);
    assert.equal((await (await relay.request(`${path}/scheduled`)).json())[0].status, 'canceled');
    const directory = relay.directory;
    await relay.stop();
    relay = await startRelay({ dataDirectory: directory });
    await relay.pairNative();
    assert.deepEqual(await (await relay.request('/conversations')).json(), []);
    assert.equal((await relay.request(`${path}/restore`, {})).status, 200);
    assert.equal((await (await relay.request('/conversations')).json())[0].id, thread.id);
    assert.equal((await (await relay.request(`${path}/scheduled`)).json())[0].status, 'canceled');
    assert.equal((await relay.request('/conversations/foreign/archive', {})).status, 404);
  } finally {
    await relay.stop();
  }
});

test('legacy database migration retains conversation folders and saved history', async () => {
  const directory = resolve(root, '.local', `legacy-conversations-${randomUUID()}`);
  await mkdir(directory, { recursive: true });
  const id = randomUUID();
  const database = new DatabaseSync(resolve(directory, 'pocketagent.db'));
  database.exec(
    'CREATE TABLE conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, created_at INTEGER NOT NULL, repository TEXT NOT NULL)',
  );
  database
    .prepare('INSERT INTO conversations VALUES (?,?,?,?)')
    .run(id, 'Existing conversation', Math.floor(Date.now() / 1000), root);
  database.close();
  await writeFile(
    resolve(directory, 'fixture-history.json'),
    JSON.stringify({
      [id]: { id, cwd: root, historyMode: 'legacy', turns: [], status: { type: 'idle' } },
    }),
  );
  const relay = await startRelay({
    dataDirectory: directory,
    extraEnv: { PocketAgent__RepositoryPath: directory },
  });
  try {
    await relay.pairNative();
    const existing = await (await relay.request(`/conversations/${id}`)).json();
    assert.equal(existing.title, 'Existing conversation');
    assert.equal(existing.workingDirectory, root);
    assert.equal(existing.model, null);
    assert.equal(existing.archived, false);
    assert.equal(
      (await relay.request(`/conversations/${id}/messages`, { text: 'hold' })).status,
      202,
    );
  } finally {
    await relay.stop();
  }
});

test('Unicode prompt and streamed response text survive provider and HTTP transport', async () => {
  const relay = await startRelay();
  try {
    await relay.pairNative();
    const thread = await (await relay.request('/conversations', {})).json();
    const path = `/conversations/${thread.id}`;
    const text = 'unicode: \u201cHello\u201d \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00';
    await relay.request(`${path}/messages`, { text });
    const completed = await until(async () => {
      const state = await (await relay.request(path)).json();
      return state.status === 'completed' && state;
    });
    assert.ok(completed.items.some((item) => item.type === 'userMessage' && item.text === text));
    assert.ok(
      completed.items.some(
        (item) =>
          item.type === 'agentMessage' &&
          item.text === 'It\u2019s ready \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00.',
      ),
    );
  } finally {
    await relay.stop();
  }
});
