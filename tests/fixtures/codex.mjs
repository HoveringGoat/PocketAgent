// Deterministic app-server peer for focused relay checks. It never runs a model or a shell.
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const path = process.env.POCKETAGENT_FIXTURE_STATE;
const threads = path && existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : {};
const pending = new Map();
const permissionMode = process.env.POCKETAGENT_EXPECT_PERMISSION_MODE ?? 'inherit';
const permissions = {
  sandbox: { type: permissionMode === 'full-access' ? 'dangerFullAccess' : 'workspaceWrite' },
  approvalPolicy: permissionMode === 'full-access' ? 'never' : 'on-request',
};
function checkPermissions(params, turn = false) {
  if (permissionMode === 'inherit') {
    if ('sandbox' in params || 'sandboxPolicy' in params || 'approvalPolicy' in params)
      throw new Error('Inherited provider policy must not be overridden.');
  } else if (
    params.approvalPolicy !== permissions.approvalPolicy ||
    (turn
      ? params.sandboxPolicy?.type !== permissions.sandbox.type
      : params.sandbox !==
        (permissionMode === 'full-access' ? 'danger-full-access' : 'workspace-write'))
  ) {
    throw new Error('Desktop permission selection did not reach the provider.');
  }
  if (turn && permissionMode === 'ask' && params.sandboxPolicy.writableRoots[0] !== params.cwd)
    throw new Error('Workspace-write roots must match this conversation, not the host default.');
}
const write = (message) => process.stdout.write(`${JSON.stringify(message)}\n`);
const save = () => {
  if (path) writeFileSync(path, JSON.stringify(threads));
};
const notify = (method, params) => write({ method, params });
const complete = (threadId, status = 'completed') => {
  const thread = threads[threadId];
  const turn = thread.turns.at(-1);
  turn.status = status;
  turn.completedAt = Math.floor(Date.now() / 1000);
  turn.durationMs = Math.max(0, Date.now() - turn.startedAt * 1000);
  if (status === 'completed')
    turn.items.push({
      id: `${turn.id}-reply`,
      type: 'agentMessage',
      text: 'Done. Your desktop work is up to date.',
    });
  save();
  notify('turn/completed', { threadId, turn });
};

createInterface({ input: process.stdin }).on('line', (line) => {
  const request = JSON.parse(line);
  if (!request.method) {
    const entry = pending.get(JSON.stringify(request.id));
    if (entry) {
      pending.delete(JSON.stringify(request.id));
      notify('serverRequest/resolved', { threadId: entry.threadId, requestId: request.id });
      complete(entry.threadId, request.result?.decision === 'cancel' ? 'interrupted' : 'completed');
    }
    return;
  }
  if (!('id' in request)) return;
  const reply = (result) => write({ id: request.id, result });
  const params = request.params;
  switch (request.method) {
    case 'initialize':
      reply({ userAgent: 'fixture', platformFamily: 'windows', platformOs: 'windows' });
      break;
    case 'account/read':
      if (params.refreshToken !== false)
        throw new Error('Diagnostics must not refresh credentials.');
      reply({
        account:
          process.env.POCKETAGENT_FIXTURE_SIGNED_OUT === '1'
            ? null
            : {
                type: 'chatgpt',
                email: 'private-test-account@example.com',
                id: 'private-account-id',
              },
        requiresOpenaiAuth: true,
      });
      break;
    case 'config/read':
      reply({ config: { model: 'gpt-6.1-sol', model_provider: null } });
      break;
    case 'account/rateLimits/read':
      if (process.env.POCKETAGENT_FIXTURE_USAGE_UNSUPPORTED === '1') {
        write({
          id: request.id,
          error: { code: -32601, message: 'Usage is unavailable for this account.' },
        });
        break;
      }
      reply({
        rateLimits: {
          limitId: 'codex',
          primary: {
            usedPercent: process.env.POCKETAGENT_FIXTURE_EXHAUSTED === '1' ? 100 : 25,
            windowDurationMins: 300,
            resetsAt:
              process.env.POCKETAGENT_FIXTURE_USAGE_WITHOUT_RESET === '1'
                ? null
                : Math.floor(Date.now() / 1000) + 3600,
          },
          secondary: {
            usedPercent: process.env.POCKETAGENT_FIXTURE_WEEKLY_EXHAUSTED === '1' ? 100 : 60,
            windowDurationMins: 10080,
            resetsAt: Math.floor(Date.now() / 1000) + 86400,
          },
        },
        accountId: 'private-account-id',
      });
      break;
    case 'model/list':
      reply(
        params.cursor
          ? {
              data: [{ model: 'gpt-5.6-sol', displayName: 'GPT-5.6 Sol', isDefault: false }],
              nextCursor: null,
            }
          : {
              data: [{ model: 'gpt-6-astra', displayName: 'GPT-6 Astra', isDefault: true }],
              nextCursor: 'more',
            },
      );
      break;
    case 'thread/start': {
      checkPermissions(params);
      if (
        Object.keys(params).some(
          (key) => !['cwd', 'model', 'sandbox', 'approvalPolicy'].includes(key),
        )
      )
        throw new Error('Unexpected thread override.');
      const thread = {
        id: randomUUID(),
        cwd: params.cwd,
        model: params.model,
        historyMode: 'legacy',
        turns: [],
        status: { type: 'idle' },
      };
      if (process.env.POCKETAGENT_FIXTURE_HISTORICAL === '1')
        thread.turns.push({
          id: randomUUID(),
          status: 'completed',
          startedAt: 1700000000,
          completedAt: 1700000010,
          durationMs: 10000,
          items: [
            {
              id: randomUUID(),
              type: 'userMessage',
              content: [{ type: 'text', text: 'Historical prompt' }],
            },
            { id: randomUUID(), type: 'agentMessage', text: 'Historical reply' },
          ],
        });
      threads[thread.id] = thread;
      save();
      reply({ thread, ...permissions });
      break;
    }
    case 'thread/read':
      reply({ thread: threads[params.threadId] });
      break;
    case 'thread/resume':
      checkPermissions(params);
      if (params.cwd !== threads[params.threadId].cwd)
        throw new Error('Resumed in the wrong folder.');
      threads[params.threadId].model = params.model;
      reply({ thread: threads[params.threadId], ...permissions });
      break;
    case 'turn/start': {
      checkPermissions(params, true);
      const threadId = params.threadId;
      const thread = threads[threadId];
      if (params.cwd !== thread.cwd) throw new Error('Turn started in the wrong folder.');
      thread.model = params.model;
      const text = params.input[0].text;
      const turn = {
        id: randomUUID(),
        startedAt: Math.floor(Date.now() / 1000),
        status: 'inProgress',
        items: [{ id: randomUUID(), type: 'userMessage', content: params.input }],
      };
      thread.turns.push(turn);
      save();
      if (text === 'drop-before-ack') {
        process.exit(0);
        break;
      }
      if (text === 'quota-after-accept' || text === 'quota-after-work') {
        reply({ turn });
        if (text === 'quota-after-work') {
          const item = {
            id: randomUUID(),
            type: 'commandExecution',
            command: 'fixture activity only',
            status: 'inProgress',
          };
          turn.items.push(item);
          notify('item/started', { threadId, turnId: turn.id, item });
        }
        setTimeout(() => {
          turn.status = 'failed';
          turn.error = { message: 'Usage limit reached.', codexErrorInfo: 'usageLimitExceeded' };
          turn.completedAt = Math.floor(Date.now() / 1000);
          save();
          notify('turn/completed', { threadId, turn });
        }, 40);
        break;
      }
      reply({ turn });
      notify('turn/started', { threadId, turn });
      if (text === 'drop') {
        setTimeout(() => process.exit(0), 100);
        break;
      }
      if (text === 'hold') break;
      if (text.startsWith('unicode:')) {
        const item = {
          id: randomUUID(),
          type: 'agentMessage',
          text: 'It\u2019s ready \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00.',
        };
        const deliver = () => {
          turn.items.push(item);
          notify('item/agentMessage/delta', {
            threadId,
            turnId: turn.id,
            itemId: item.id,
            delta: item.text,
          });
          complete(threadId);
        };
        if (text === 'unicode:focus') setTimeout(deliver, 1200);
        else deliver();
        break;
      }
      const itemId = randomUUID();
      const method =
        text === 'questions'
          ? 'item/tool/requestUserInput'
          : text === 'unsupported'
            ? 'item/permissions/requestApproval'
            : 'item/commandExecution/requestApproval';
      const requestId = text === 'questions' ? 'question-request' : 101;
      const details =
        text === 'questions'
          ? {
              questions: [
                {
                  id: 'approach',
                  header: 'Approach',
                  question: 'Which approach should I use?',
                  options: [{ label: 'Minimal', description: 'Smallest practical change.' }],
                },
              ],
            }
          : {
              command: 'git status --short',
              cwd: thread.cwd,
              reason: 'Check the working tree before continuing.',
              availableDecisions: ['accept', 'decline', 'cancel'],
            };
      if (text === 'restricted') {
        details.availableDecisions = ['decline', 'cancel'];
        details.opaqueCredential = 'fixture-private-metadata';
      }
      notify('item/started', {
        threadId,
        turnId: turn.id,
        item: {
          id: itemId,
          type: 'commandExecution',
          command: 'git status --short',
          status: 'inProgress',
        },
      });
      const requestParams = { threadId, turnId: turn.id, itemId, ...details };
      pending.set(JSON.stringify(requestId), { threadId });
      write({ id: requestId, method, params: requestParams });
      if (text === 'resolved')
        setTimeout(() => {
          pending.delete(JSON.stringify(requestId));
          notify('serverRequest/resolved', { threadId, requestId });
          complete(threadId);
        }, 150);
      break;
    }
    case 'turn/steer': {
      const thread = threads[params.threadId];
      const turn = thread.turns.at(-1);
      if (turn.status !== 'inProgress' || turn.id !== params.expectedTurnId) {
        write({
          id: request.id,
          error: { code: -32600, message: 'Expected turn is no longer active.' },
        });
        break;
      }
      if (Object.keys(params).some((key) => !['threadId', 'expectedTurnId', 'input'].includes(key)))
        throw new Error('Unexpected steering policy override.');
      const item = { id: randomUUID(), type: 'userMessage', content: params.input };
      turn.items.push(item);
      save();
      reply({ turnId: turn.id });
      notify('item/completed', { threadId: thread.id, turnId: turn.id, item });
      break;
    }
    case 'turn/interrupt':
      reply({});
      complete(params.threadId, 'interrupted');
      break;
    default:
      write({ id: request.id, error: { code: -32601, message: 'Unsupported fixture method.' } });
  }
});
