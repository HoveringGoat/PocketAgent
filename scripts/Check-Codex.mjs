// Read-only protocol/authentication probe. Prints no account identifiers or credentials.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const executable = process.argv[2];
if (!executable) throw new Error('Usage: node scripts/Check-Codex.mjs <native-codex-executable>');
const child = spawn(executable, ['app-server', '--listen', 'stdio://'], {
  windowsHide: true,
  stdio: ['pipe', 'pipe', 'pipe'],
});
let initialized = false;
let accountChecked = false;
let homeUnavailable = false;
const timer = setTimeout(() => {
  child.kill();
  process.exitCode = 1;
  console.error('Codex protocol probe timed out. Check CLI setup and CODEX_HOME.');
}, 15_000);
child.stderr.on('data', (data) => {
  homeUnavailable ||= /home directory/i.test(data.toString());
});
const send = (message) => child.stdin.write(`${JSON.stringify(message)}\n`);
createInterface({ input: child.stdout }).on('line', (line) => {
  const message = JSON.parse(line);
  if (message.id === 1) {
    if (message.error) {
      console.error('Codex initialization failed.');
      child.kill();
      return;
    }
    console.log('Codex app-server initialization: passed');
    initialized = true;
    send({ method: 'initialized' });
    send({ id: 2, method: 'account/read', params: { refreshToken: false } });
  }
  if (message.id === 2) {
    accountChecked = true;
    console.log(`Desktop account available: ${Boolean(message.result?.account)}`);
    console.log(`Authentication required: ${message.result?.requiresOpenaiAuth ?? 'unknown'}`);
    child.kill();
  }
});
child.on('error', () => {
  clearTimeout(timer);
  console.error('Could not launch the native Codex executable.');
  process.exitCode = 1;
});
child.on('exit', (code) => {
  clearTimeout(timer);
  if (!initialized || !accountChecked) {
    console.error(
      `Codex exited before the probe completed (exit ${code}; home unavailable: ${homeUnavailable}). Set CODEX_HOME to your desktop Codex directory if needed.`,
    );
    process.exitCode = 1;
  }
});
send({
  id: 1,
  method: 'initialize',
  params: {
    clientInfo: { name: 'pocketagent_probe', title: 'PocketAgent setup check', version: '0.1.0' },
  },
});
