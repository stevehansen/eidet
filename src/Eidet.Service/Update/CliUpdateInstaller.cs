using System.Diagnostics;
using Eidet.Core;
using Eidet.Core.Update;

namespace Eidet.Service.Update;

/// <summary>
/// Runs the unattended install by handing off to the same `eidet update` the user would type.
///
/// Deliberately a shell-out rather than a shared code path: the updater's job is to stop this
/// service, replace the binaries it is executing from, and start it again, none of which an
/// in-process call can survive. Detaching also means the platform-specific work — the Windows
/// trampoline, systemd, launchd — stays in one place and is exercised identically whether a human
/// or the scheduler triggered it.
/// </summary>
public sealed class CliUpdateInstaller : IUpdateInstaller
{
    public Task LaunchAsync(string targetVersion, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("eidet", $"update --to {targetVersion} --json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            // Not awaited by design — see IUpdateInstaller. The child outlives us.
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            EidetLog.Error("[update] could not launch the updater", ex);
        }

        return Task.CompletedTask;
    }
}
