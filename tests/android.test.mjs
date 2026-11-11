import test from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { resolve } from 'node:path';
import { mkdir, writeFile } from 'node:fs/promises';
import { startRelay, root } from './helpers.mjs';

const run = promisify(execFile);
const adb =
  process.env.POCKETAGENT_TEST_ADB ??
  'C:/Users/Spence/AppData/Local/Android/Sdk/platform-tools/adb.exe';
const serial = process.env.POCKETAGENT_TEST_ANDROID;

test(
  'native Android pairing steering and restoration',
  { skip: !serial, timeout: 150000 },
  async () => {
    const relay = await startRelay();
    const second = await startRelay();
    const port = new URL(relay.origin).port;
    const command = async (...args) => {
      try {
        return await run(adb, ['-s', serial, ...args], {
          windowsHide: true,
          timeout: 100000,
          maxBuffer: 4 * 1024 * 1024,
        });
      } catch {
        throw new Error('The isolated Android command failed; pairing credentials are redacted.');
      }
    };
    try {
      assert.match(
        serial,
        /^emulator-\d+$/,
        'Use an isolated emulator, never clear data on a physical phone.',
      );
      await command('reverse', `tcp:${port}`, `tcp:${port}`);
      const secondPort = new URL(second.origin).port;
      await command('reverse', `tcp:${secondPort}`, `tcp:${secondPort}`);
      await command(
        'install',
        '-r',
        resolve(root, 'src/PocketAgent.Android/app/build/outputs/apk/debug/app-debug.apk'),
      );
      await command('shell', 'pm', 'clear', 'dev.pocketagent');
      const first = await command(
        'shell',
        'am',
        'instrument',
        '-w',
        '-e',
        'origin',
        relay.origin,
        '-e',
        'code',
        relay.pairingCode,
        '-e',
        'secondOrigin',
        second.origin,
        '-e',
        'secondCode',
        second.pairingCode,
        '-e',
        'workspaceBase64',
        Buffer.from(relay.directory, 'utf8').toString('base64'),
        'dev.pocketagent/.SmokeInstrumentation',
      );
      assert.ok(
        first.stdout.includes('INSTRUMENTATION_CODE: -1'),
        first.stdout.replaceAll(relay.pairingCode, '[redacted]'),
      );
      const screenshot = await run(
        adb,
        ['-s', serial, 'exec-out', 'run-as', 'dev.pocketagent', 'cat', 'cache/native-chat.png'],
        { windowsHide: true, encoding: 'buffer', maxBuffer: 10 * 1024 * 1024 },
      );
      await mkdir(resolve(root, '.local/previews'), { recursive: true });
      await writeFile(resolve(root, '.local/previews/native-android-chat.png'), screenshot.stdout);
      await command('shell', 'am', 'force-stop', 'dev.pocketagent');
      const restored = await command(
        'shell',
        'am',
        'instrument',
        '-w',
        '-e',
        'phase',
        'restore',
        'dev.pocketagent/.SmokeInstrumentation',
      );
      assert.ok(restored.stdout.includes('INSTRUMENTATION_CODE: -1'), restored.stdout);
    } finally {
      await command('reverse', '--remove', `tcp:${port}`).catch(() => {});
      await command('reverse', '--remove', `tcp:${new URL(second.origin).port}`).catch(() => {});
      await relay.stop();
      await second.stop();
    }
  },
);
