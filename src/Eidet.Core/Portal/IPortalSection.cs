namespace Eidet.Core.Portal;

/// <summary>
/// One section of the Portal. Implementations apply the section's deterministic
/// off-mode selection rule (see docs/domains/portal.md), render an HTML fragment
/// with hyperlink citations, and either return a <see cref="PortalSection"/> or
/// <c>null</c> to be omitted from the
/// document. <see cref="AlwaysPresent"/> sections never return <c>null</c> —
/// they emit a stub instead.
/// </summary>
internal interface IPortalSection
{
    string Id { get; }
    string Title { get; }
    bool AlwaysPresent { get; }

    Task<PortalSection?> RenderAsync(PortalContext ctx, CancellationToken ct);
}
