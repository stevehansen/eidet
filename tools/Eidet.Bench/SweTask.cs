namespace Eidet.Bench;

/// <summary>
/// One SWE Context Bench task, carrying exactly the canonical SWE-bench columns published by the
/// dataset (huggingface.co/datasets/jiayuanz3/SWEContextBench): repo, instance_id, base_commit,
/// patch, test_patch, problem_statement, hints_text, created_at, version, FAIL_TO_PASS,
/// PASS_TO_PASS, environment_setup_commit.
/// </summary>
/// <remarks>
/// <see cref="IsRelated"/> is NOT a dataset column — the base/related split is conveyed
/// structurally by the dataset release (separate files), so the <see cref="ISweDatasetPort"/>
/// adapter assigns it. Related tasks feed the memory-ingestion phase; base tasks are the ones
/// a run is scored on.
/// </remarks>
public sealed record SweTask(
    string Repo,
    string InstanceId,
    string BaseCommit,
    string Patch,
    string TestPatch,
    string ProblemStatement,
    string HintsText,
    string CreatedAt,
    double Version,
    string FailToPass,
    string PassToPass,
    string EnvironmentSetupCommit,
    bool IsRelated);
