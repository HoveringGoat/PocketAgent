const assert = require('node:assert/strict');
const test = require('node:test');
const prune = require('../.github/scripts/prune-dev-artifacts.cjs');

test('keeps the newest three complete builds and deletes only older dev build artifacts', async () => {
  const run = number => ({ id: number, run_number: number, head_sha: String(number) });
  const pair = number => [
    { id: number * 10, name: `pocketagent-windows-${number}` },
    { id: number * 10 + 1, name: `pocketagent-android-debug-${number}` },
  ];
  const artifacts = {
    7: [{ ...pair(7)[0], expired: true }, pair(7)[1]],
    6: pair(6),
    5: pair(5),
    4: pair(4),
    3: pair(3),
    2: [pair(2)[0], { id: 99, name: 'unrelated-artifact' }],
  };
  const deleted = [];
  const actions = {
    getWorkflowRun: async () => ({ data: { ...run(4), workflow_id: 100 } }),
    listWorkflowRuns: Symbol('listWorkflowRuns'),
    listWorkflowRunArtifacts: Symbol('listWorkflowRunArtifacts'),
    deleteArtifact: async ({ artifact_id }) => {
      deleted.push(artifact_id);
      if (artifact_id === 30) throw Object.assign(new Error('Already removed'), { status: 404 });
    },
  };
  const github = {
    rest: { actions },
    paginate: async (method, parameters) => {
      if (method === actions.listWorkflowRuns) {
        assert.equal(parameters.workflow_id, 100);
        assert.equal(parameters.branch, 'dev');
        assert.equal(parameters.status, 'completed');
        return [2, 7, 3, 5, 6].map(run);
      }
      assert.equal(method, actions.listWorkflowRunArtifacts);
      return artifacts[parameters.run_id];
    },
  };

  await prune(github, { repo: { owner: 'owner', repo: 'repo' }, runId: 4 }, { info() {} });
  assert.deepEqual(deleted, [30, 31, 20]);
});
