using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Service.Mcp;

/// <summary>
/// MCP-only side effect: the first <c>eidet_context</c> call against a repo
/// auto-ingests the repo if it has no memories yet. Stateful per-instance —
/// fires at most once. Failures are swallowed so context calls never break.
/// </summary>
public sealed class AutoIntakeOnContext
{
    private readonly MemoryService _svc;
    private readonly IntakeService _intake;
    private readonly string _repoId;
    private readonly string _triggerTool;
    private bool _done;

    public AutoIntakeOnContext(MemoryService svc, IntakeService intake, string repoId,
        string triggerTool = "eidet_context")
    {
        _svc = svc;
        _intake = intake;
        _repoId = repoId;
        _triggerTool = triggerTool;
    }

    public async Task OnToolCalledAsync(string toolName, CancellationToken ct)
    {
        if (toolName != _triggerTool || _done) return;
        _done = true;
        try
        {
            var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
            var counts = await _svc.GetCountsByTypeAsync(normalizedRepoId, ct);
            var totalForRepo = counts.Values.Sum();
            if (totalForRepo == 0)
                await _intake.IngestAsync(_repoId, _repoId, dryRun: false, ct: ct);
        }
        catch { /* Non-critical — don't fail the triggering tool for auto-intake issues */ }
    }
}
