using Eidet.Core.Services;

namespace Eidet.Core.LooseEnds.Promotion;

/// <summary>
/// Prod <see cref="IPromotionPort"/>: promotes a confirmed Loose End into a real
/// <see cref="MemoryEntry"/> through the full <see cref="MemoryService.StoreAsync(StoreOptions, CancellationToken)"/>
/// gate (secret + signal scan, dedup, hooks). When <see cref="PromoteOptions.ExternalRef"/> is set,
/// the note is linked to an external issue instead of minted — no memory is created and the gate is
/// not entered. This is the single gate-split enforcement point: park never reaches StoreAsync, and
/// promote only ever reaches it via this adapter.
/// </summary>
public sealed class MemoryServicePromotionAdapter : IPromotionPort
{
    private readonly MemoryService _memory;

    public MemoryServicePromotionAdapter(MemoryService memory)
    {
        _memory = memory;
    }

    public async Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default)
    {
        // Link-only: record the external ref, do not mint a memory.
        if (!string.IsNullOrEmpty(opts.ExternalRef))
            return new PromotionResult(true, MemoryId: null, opts.ExternalRef, Reason: null);

        var result = await _memory.StoreAsync(new StoreOptions(e.RepoId, e.Note, opts.Type)
        {
            Importance = opts.Importance,
            Tags = e.Tags,
            Source = "promoted-loose-end",
        }, ct);

        // A near-duplicate means the knowledge already exists — promote onto the existing memory
        // rather than stranding the Loose End open and dropping the duplicate id on the floor.
        if (result.DuplicateId is not null)
            return new PromotionResult(true, result.DuplicateId, ExternalRef: null, Reason: null);

        if (!result.Success)
            return new PromotionResult(false, MemoryId: null, ExternalRef: null,
                Reason: result.Reason ?? "promotion rejected at the memory gate");

        return new PromotionResult(true, result.Id, ExternalRef: null, Reason: null);
    }
}
