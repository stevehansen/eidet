namespace Eidet.Core.Update;

/// <summary>
/// Starts a self-replacement to a specific version.
///
/// Fire-and-forget by contract, because the installer's first act is to stop and replace the very
/// process that asked for it: on Windows the updater hands off to a detached trampoline and this
/// call returns long before the new binary exists. A caller must therefore have persisted
/// everything it cares about *before* invoking it, and must not expect to observe the outcome.
/// </summary>
public interface IUpdateInstaller
{
    Task LaunchAsync(string targetVersion, CancellationToken ct = default);
}
