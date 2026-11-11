import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID, X509Certificate } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { request } from 'node:https';
import { dotnet, root, startRelay } from './helpers.mjs';

const dll =
  process.env.POCKETAGENT_TEST_SERVER_DLL ??
  resolve(root, 'src/PocketAgent.Server/bin/Release/net10.0/PocketAgent.Server.dll');

async function localCommand(argument, extraEnv) {
  const child = spawn(dotnet, [dll, argument], {
    cwd: root,
    windowsHide: true,
    env: { ...process.env, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.on('data', (chunk) => (output += chunk.toString('utf8')));
  child.stderr.resume();
  const code = await new Promise((resolve) => child.once('exit', resolve));
  return { code, output };
}

test('direct IP HTTPS validates the public identity locally without a relay or external probe', async () => {
  const directory = resolve(root, '.local', `direct-internet-${randomUUID()}`);
  const publicOrigin = 'https://203.0.113.10:65147'; // Documentation address; no public network is contacted.
  const configuration = {
    PocketAgent__DataDirectory: directory,
    PocketAgent__ListenAddress: '127.0.0.1',
    PocketAgent__DirectOrigin: publicOrigin,
    PocketAgent__PublicOrigin: '',
    PocketAgent__CertificatePath: '',
  };
  assert.equal((await localCommand('--setup', configuration)).code, 0);
  const authority = new X509Certificate(
    await readFile(resolve(directory, 'pocketagent-root.cer')),
  ).toString();
  const certificate = resolve(directory, 'server.pfx');
  let relay = await startRelay({
    dataDirectory: directory,
    certificateAuthority: authority,
    extraEnv: {
      PocketAgent__DirectOrigin: publicOrigin,
      PocketAgent__CertificatePath: certificate,
    },
  });
  try {
    const device = await relay.pairNative();
    const status = (host) =>
      new Promise((resolve, reject) => {
        const outgoing = request(
          `${relay.origin}/api/conversations`,
          { ca: authority, headers: { Host: host, Authorization: `Bearer ${device.token}` } },
          (response) => {
            response.resume();
            resolve(response.statusCode);
          },
        );
        outgoing.on('error', reject);
        outgoing.end();
      });
    assert.equal(await status('203.0.113.10:65147'), 200);
    assert.equal(await status('203.0.113.10:65148'), 400);
    assert.equal(
      (
        await relay.request(
          '/conversations',
          {},
          { headers: { Origin: 'https://attacker.example' } },
        )
      ).status,
      403,
    );
    const result = await localCommand('--diagnose', {
      ...configuration,
      PocketAgent__CertificatePath: certificate,
      PocketAgent__Port: new URL(relay.origin).port,
    });
    assert.equal(result.code, 0);
    const report = JSON.parse(result.output);
    assert.equal(
      report.LocalConnectionVerified,
      true,
      'Verify the advertised public IP certificate identity, not merely the local IP SAN.',
    );
    assert.equal(report.Address, publicOrigin);
    assert.ok(report.Checks.some((check) => check.includes('External access is NOT verified')));
    assert.ok(report.Checks.some((check) => check.includes('external TCP 65147')));
    await relay.stop();
    relay = await startRelay({
      dataDirectory: directory,
      certificateAuthority: authority,
      extraEnv: {
        PocketAgent__DirectOrigin: 'https://203.0.113.11:65147',
        PocketAgent__CertificatePath: certificate,
      },
    });
    const mismatch = await localCommand('--diagnose', {
      ...configuration,
      PocketAgent__DirectOrigin: 'https://203.0.113.11:65147',
      PocketAgent__CertificatePath: certificate,
      PocketAgent__Port: new URL(relay.origin).port,
    });
    assert.equal(
      JSON.parse(mismatch.output).LocalConnectionVerified,
      false,
      'A public IP certificate mismatch must fail; never bypass TLS identity verification.',
    );
  } finally {
    await relay.stop();
  }
});

test('direct internet configuration rejects cleartext, private WAN addresses and mixed proxy modes', async () => {
  const directory = resolve(root, '.local', `invalid-direct-${randomUUID()}`);
  const base = {
    PocketAgent__DataDirectory: directory,
    PocketAgent__ListenAddress: '127.0.0.1',
    PocketAgent__PublicOrigin: '',
    PocketAgent__CertificatePath: resolve(directory, 'server.pfx'),
  };
  for (const invalid of [
    { PocketAgent__DirectOrigin: 'http://203.0.113.10:5147' },
    { PocketAgent__DirectOrigin: 'https://192.168.1.10:5147' },
    { PocketAgent__DirectOrigin: 'https://100.64.1.10:5147' },
    { PocketAgent__DirectOrigin: 'https://203.0.113.10:0' },
    {
      PocketAgent__DirectOrigin: 'https://203.0.113.10:5147',
      PocketAgent__PublicOrigin: 'https://proxy.example.com',
    },
    { PocketAgent__DirectOrigin: 'https://203.0.113.10:5147', PocketAgent__CertificatePath: '' },
  ])
    assert.notEqual((await localCommand('--diagnose', { ...base, ...invalid })).code, 0);
});
