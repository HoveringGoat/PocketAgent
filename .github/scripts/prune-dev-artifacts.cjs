module.exports = async function pruneDevArtifacts(github, context, core) {
  const actions = github.rest.actions;
  const { data: currentRun } = await actions.getWorkflowRun({
    ...context.repo,
    run_id: context.runId,
  });
  const completedRuns = await github.paginate(actions.listWorkflowRuns, {
    ...context.repo,
    workflow_id: currentRun.workflow_id,
    branch: 'dev',
    event: 'push',
    status: 'completed',
    per_page: 100,
  });
  const runs = [currentRun, ...completedRuns.filter(run => run.id !== currentRun.id)]
    .sort((left, right) => right.run_number - left.run_number);

  let retainedBuilds = 0;
  for (const run of runs) {
    const artifacts = await github.paginate(actions.listWorkflowRunArtifacts, {
      ...context.repo,
      run_id: run.id,
      per_page: 100,
    });
    const expectedNames = [
      `pocketagent-windows-${run.head_sha}`,
      `pocketagent-android-debug-${run.head_sha}`,
    ];
    const devArtifacts = artifacts.filter(artifact =>
      !artifact.expired && expectedNames.includes(artifact.name));

    // A complete pair proves both uploads succeeded, even if cleanup previously failed.
    const complete = expectedNames.every(name => devArtifacts.some(artifact => artifact.name === name));
    if (retainedBuilds < 3) {
      if (complete) {
        retainedBuilds += 1;
        core.info(`Keeping artifacts from dev run #${run.run_number}.`);
      }
      continue;
    }

    for (const artifact of devArtifacts) {
      try {
        await actions.deleteArtifact({ ...context.repo, artifact_id: artifact.id });
        core.info(`Deleted ${artifact.name} from dev run #${run.run_number}.`);
      } catch (error) {
        // A concurrent cleanup or expiration may have already removed this artifact.
        if (error.status !== 404) throw error;
      }
    }
  }
};
