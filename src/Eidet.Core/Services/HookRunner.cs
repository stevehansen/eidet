using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Configuration;

namespace Eidet.Core.Services;

public enum HookEvent
{
    PreStore, PostStore,
    PreRecall, PostRecall,
    PreForget, PostForget,
}

public class HookContext
{
    public string Event { get; init; } = "";
    public string Repo { get; init; } = "";
    public object? Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class HookResult
{
    public bool Allowed { get; init; } = true;
    public string? Reason { get; init; }
    public string? Output { get; init; }

    public static HookResult Ok(string? output = null) => new() { Output = output };
    public static HookResult Rejected(string reason) => new() { Allowed = false, Reason = reason };
}

public interface IHookRunner
{
    Task<HookResult> RunPreHooksAsync(HookEvent evt, HookContext context, CancellationToken ct);
    Task RunPostHooksAsync(HookEvent evt, HookContext context, CancellationToken ct);
    bool HasHooks(HookEvent evt);
}

public sealed class NullHookRunner : IHookRunner
{
    public static readonly NullHookRunner Instance = new();
    public Task<HookResult> RunPreHooksAsync(HookEvent evt, HookContext context, CancellationToken ct) =>
        Task.FromResult(HookResult.Ok());
    public Task RunPostHooksAsync(HookEvent evt, HookContext context, CancellationToken ct) =>
        Task.CompletedTask;
    public bool HasHooks(HookEvent evt) => false;
}

public sealed class HookRunner : IHookRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    private readonly HooksConfig _config;

    public HookRunner(HooksConfig config) => _config = config;

    public bool HasHooks(HookEvent evt) => GetHooks(evt).Any(h => h.Enabled);

    public async Task<HookResult> RunPreHooksAsync(HookEvent evt, HookContext context, CancellationToken ct)
    {
        var hooks = GetHooks(evt).Where(h => h.Enabled).ToList();
        if (hooks.Count == 0) return HookResult.Ok();

        var input = JsonSerializer.Serialize(context, JsonOptions);

        foreach (var hook in hooks)
        {
            var (exitCode, stdout, stderr) = await ExecuteAsync(hook.Command, input, hook.TimeoutSeconds, ct);

            if (exitCode != 0)
            {
                var reason = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                    : $"Hook '{hook.Command}' rejected with exit code {exitCode}";
                return HookResult.Rejected(reason);
            }
        }

        return HookResult.Ok();
    }

    public async Task RunPostHooksAsync(HookEvent evt, HookContext context, CancellationToken ct)
    {
        var hooks = GetHooks(evt).Where(h => h.Enabled).ToList();
        if (hooks.Count == 0) return;

        var input = JsonSerializer.Serialize(context, JsonOptions);

        foreach (var hook in hooks)
        {
            try
            {
                await ExecuteAsync(hook.Command, input, hook.TimeoutSeconds, ct);
            }
            catch
            {
                // Post-hooks are fire-and-forget — failures don't propagate
            }
        }
    }

    private List<HookDefinition> GetHooks(HookEvent evt) => evt switch
    {
        HookEvent.PreStore => _config.PreStore,
        HookEvent.PostStore => _config.PostStore,
        HookEvent.PreRecall => _config.PreRecall,
        HookEvent.PostRecall => _config.PostRecall,
        HookEvent.PreForget => _config.PreForget,
        HookEvent.PostForget => _config.PostForget,
        _ => [],
    };

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string command, string stdin, int timeoutSeconds, CancellationToken ct)
    {
        // Parse command: support "cmd arg1 arg2" or just "cmd"
        var (fileName, arguments) = ParseCommand(command);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return (-1, "", "Failed to start hook process");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            // Write context to stdin then close
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            await proc.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — kill the process
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, "", $"Hook '{command}' timed out after {timeoutSeconds}s");
        }
    }

    internal static (string FileName, string Arguments) ParseCommand(string command)
    {
        command = command.Trim();

        // Quoted executable: "C:\path to\tool.exe" args
        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var fileName = command[1..endQuote];
                var arguments = endQuote + 1 < command.Length ? command[(endQuote + 1)..].TrimStart() : "";
                return (fileName, arguments);
            }
        }

        // Simple split on first space
        var spaceIdx = command.IndexOf(' ');
        if (spaceIdx > 0)
            return (command[..spaceIdx], command[(spaceIdx + 1)..]);

        return (command, "");
    }
}
