import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createServer } from 'node:net';
import { request as httpsRequest } from 'node:https';
import { existsSync, readFileSync } from 'node:fs';

export const root = resolve(import.meta.dirname, '..');
const localSdk = resolve(root, '.tools/dotnet/dotnet.exe');
export const dotnet =
  process.env.POCKETAGENT_TEST_DOTNET ?? (existsSync(localSdk) ? localSdk : 'dotnet');

export async function freePort() {
  const listener = createServer();
  await new Promise((resolve) => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port;
  await new Promise((resolve) => listener.close(resolve));
  return port;
}

export async function startRelay({
  dataDirectory,
  realCodex = false,
  extraEnv = {},
  certificateAuthority,
  published = false,
} = {}) {
  const directory = dataDirectory ?? resolve(root, '.local', `check-${randomUUID()}`);
  await mkdir(directory, { recursive: true });
  const port = await freePort();
  const origin = `${certificateAuthority ? 'https' : 'http'}://127.0.0.1:${port}`;
  const fetchServer = certificateAuthority
    ? (url, options = {}) =>
        new Promise((resolve, reject) => {
          const request = httpsRequest(
            url,
            { ca: certificateAuthority, method: options.method, headers: options.headers },
            (response) => {
              const chunks = [];
              response.on('data', (chunk) => chunks.push(chunk));
              response.on('end', () => {
                const headers = Object.fromEntries(
                  Object.entries(response.headers).map(([key, value]) => [
                    key,
                    Array.isArray(value) ? value.join(', ') : value,
                  ]),
                );
                resolve(
                  new Response(Buffer.concat(chunks), { status: response.statusCode, headers }),
                );
              });
            },
          );
          request.on('error', reject);
          request.end(options.body);
        })
    : fetch;
  const hostDirectory = published
    ? resolve(
        root,
        `artifacts/PocketAgent-${JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8')).version}-desktop`,
      )
    : resolve(root, 'src/PocketAgent.Server');
  const child = spawn(
    dotnet,
    [
      published
        ? resolve(hostDirectory, 'PocketAgent.Server.dll')
        : (process.env.POCKETAGENT_TEST_SERVER_DLL ??
          resolve(root, 'src/PocketAgent.Server/bin/Debug/net10.0/PocketAgent.Server.dll')),
    ],
    {
      cwd: hostDirectory,
      windowsHide: true,
      env: {
        ...process.env,
        PocketAgent__DataDirectory: directory,
        PocketAgent__RepositoryPath: root,
        PocketAgent__ListenAddress: '127.0.0.1',
        PocketAgent__Port: String(port),
        ...(!realCodex
          ? {
              PocketAgent__CodexExecutable: process.execPath,
              PocketAgent__CodexArguments__0: resolve(root, 'tests/fixtures/codex.mjs'),
              PocketAgent__CodexArguments__1: '',
              PocketAgent__CodexArguments__2: '',
              POCKETAGENT_FIXTURE_STATE: resolve(directory, 'fixture-history.json'),
            }
          : {}),
        ...extraEnv,
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    },
  );
  let output = '';
  let code;
  child.stdout.on('data', (data) => {
    output += data.toString();
    code = output.match(/Pairing code: (\d+) /)?.[1];
  });
  // Keep diagnostic output in memory; never echo pairing credentials to test output.
  child.stderr.on('data', (data) => {
    output += data.toString();
  });
  let readinessIssue = '';
  try {
    await until(async () => {
      try {
        return code && (await fetchServer(`${origin}/api/session`)).ok;
      } catch (error) {
        readinessIssue = `${error.code ?? 'network'}: ${error.message}`;
        return false;
      }
    }, 15_000);
  } catch {
    child.kill();
    throw new Error(
      `Relay failed to start (${readinessIssue}): ${output.replace(/Pairing code: \d+/g, 'Pairing code: [redacted]')}`,
    );
  }
  let cookie = '';
  let bearer = '';
  return {
    child,
    origin,
    directory,
    get cookie() {
      return cookie;
    },
    get pairingCode() {
      return code;
    },
    async request(path, body, { authenticated = true, headers = {}, native = false } = {}) {
      return fetchServer(`${origin}/api${path}`, {
        method: body === undefined ? 'GET' : 'POST',
        headers: {
          ...(cookie && authenticated ? { Cookie: cookie } : {}),
          ...(bearer && authenticated ? { Authorization: `Bearer ${bearer}` } : {}),
          ...(body === undefined
            ? {}
            : {
                'Content-Type': 'application/json',
                ...(!native ? { Origin: origin } : {}),
                'X-PocketAgent': '1',
              }),
          ...headers,
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    },
    async pairNative() {
      const response = await this.request(
        '/devices/pair',
        { code, deviceName: 'Native verification phone' },
        { authenticated: false, native: true },
      );
      assertResponse(response.status, 200);
      const paired = await response.json();
      bearer = paired.token;
      return paired;
    },
    async pair() {
      const response = await this.request(
        '/pair',
        { code, deviceName: 'Verification phone' },
        { authenticated: false },
      );
      if (response.status !== 200) throw new Error('Pairing failed.');
      cookie = response.headers.get('set-cookie').split(';')[0];
      return cookie;
    },
    async stop() {
      if (child.exitCode !== null) return;
      child.kill('SIGTERM');
      await new Promise((resolve) => child.once('exit', resolve));
    },
  };
}

function assertResponse(actual, expected) {
  if (actual !== expected)
    throw new Error(`Native pairing returned ${actual}; expected ${expected}.`);
}

export async function until(check, timeout = 10_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    try {
      const value = await check();
      if (value) return value;
    } catch {
      /* Wait for the observable state. */
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error('Timed out waiting for expected state.');
}
