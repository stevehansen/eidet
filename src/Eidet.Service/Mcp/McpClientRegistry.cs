using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eidet.Service.Mcp;

/// <summary>
/// MCP client integration state. <see cref="NotAvailable"/> means the host
/// tool isn't installed on this machine (no CLI on PATH and no config file);
/// <see cref="NotConfigured"/> means the tool is installed but has no eidet
/// MCP entry; <see cref="Configured"/> means the entry is present.
/// </summary>
public enum McpInstallStatus { NotAvailable, NotConfigured, Configured }

/// <summary>
/// Abstraction over an MCP-aware AI client (Claude Code, Codex, Gemini, etc).
/// Implementations prefer the client's own CLI when present (so we ride on the
/// upstream schema), falling back to a direct config file write when not.
/// </summary>
public abstract class McpClient
{
    public abstract string Name { get; }

    /// <summary>Path to the config file we'd touch in fallback mode (null if N/A on this OS).</summary>
    public abstract string? ConfigPath { get; }

    /// <summary>Name of the upstream CLI we shell out to (e.g. "claude", "codex"). Null = file-only client.</summary>
    protected virtual string? CliCommand => null;

    public async Task<McpInstallStatus> CheckAsync(CancellationToken ct = default)
    {
        var cliOnPath = CliCommand != null && IsExecutableOnPath(CliCommand);
        var fileExists = ConfigPath != null && File.Exists(ConfigPath);

        if (!cliOnPath && !fileExists)
            return McpInstallStatus.NotAvailable;

        return await IsConfiguredAsync(ct)
            ? McpInstallStatus.Configured
            : McpInstallStatus.NotConfigured;
    }

    public async Task<(bool Success, string Detail)> InstallAsync(CancellationToken ct = default)
    {
        if (CliCommand != null && IsExecutableOnPath(CliCommand))
        {
            var (ok, detail) = await InstallViaCliAsync(ct);
            if (ok) return (true, $"{Name}: installed via `{CliCommand}` CLI");
            // Fall through to file fallback if CLI failed.
        }

        if (ConfigPath != null)
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir != null && !Directory.Exists(dir))
                return (false, $"{Name}: config dir not present ({dir}) — install the client first");

            try
            {
                return await InstallViaFileAsync(ct);
            }
            catch (Exception ex)
            {
                return (false, $"{Name}: file write failed — {ex.Message}");
            }
        }

        return (false, $"{Name}: no install path available");
    }

    /// <summary>True when the eidet entry is present in this client's config.</summary>
    protected abstract Task<bool> IsConfiguredAsync(CancellationToken ct);

    /// <summary>Try to register eidet via the upstream CLI (e.g. `claude mcp add ...`).</summary>
    protected abstract Task<(bool Success, string Detail)> InstallViaCliAsync(CancellationToken ct);

    /// <summary>Fallback: write the eidet entry directly to <see cref="ConfigPath"/>.</summary>
    protected abstract Task<(bool Success, string Detail)> InstallViaFileAsync(CancellationToken ct);

    // ------- shared helpers -------

    protected static bool IsExecutableOnPath(string name) => FindExecutableOnPath(name) != null;

    /// <summary>Resolves a bare command name (e.g. "gemini") to its full path on
    /// the user's PATH, honouring PATHEXT on Windows so .cmd / .bat are
    /// discovered alongside .exe.</summary>
    protected static string? FindExecutableOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>Runs a bare-name command, resolving it against PATH first so
    /// non-.exe entry points (gemini.cmd, claude.cmd from npm) actually launch
    /// on Windows. On Unix the lookup is a no-op and the caller's bare name
    /// is passed through.</summary>
    protected static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var resolved = FindExecutableOnPath(fileName) ?? fileName;
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows() &&
                (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                 resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                // .cmd / .bat must go through cmd.exe — Process.Start won't launch
                // them directly when UseShellExecute is false.
                psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{resolved}\" {arguments}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo(resolved, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "process start failed");

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var error = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrEmpty(output) ? error : output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    protected static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// Static directory of supported MCP clients. Order matters — used as the
/// display/install priority in interactive prompts.
/// </summary>
public static class McpClientRegistry
{
    public static IReadOnlyList<McpClient> All { get; } = new McpClient[]
    {
        new ClaudeCodeClient(),
        new ClaudeDesktopClient(),
        new CodexClient(),
        new GeminiClient(),
    };

    public static McpClient? FindByName(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

// ---------------- Claude Code ----------------

internal sealed class ClaudeCodeClient : McpClient
{
    public override string Name => "claude-code";
    public override string? ConfigPath => Path.Combine(Home, ".claude.json");
    protected override string? CliCommand => "claude";

    protected override Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        var path = ConfigPath;
        if (path == null || !File.Exists(path)) return Task.FromResult(false);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            return Task.FromResult(root?["mcpServers"]?.AsObject()?.ContainsKey("eidet") == true);
        }
        catch { return Task.FromResult(false); }
    }

    protected override async Task<(bool, string)> InstallViaCliAsync(CancellationToken ct)
    {
        var (code, output) = await RunProcessAsync(
            "claude", "mcp add --transport stdio -s user eidet -- eidet mcp", ct);
        return code == 0
            ? (true, output.Trim())
            : (false, $"claude mcp add failed: {output.Trim()}");
    }

    protected override Task<(bool, string)> InstallViaFileAsync(CancellationToken ct) =>
        Task.FromResult(McpJsonFile.AddEidetServer(ConfigPath!, Name));
}

// ---------------- Claude Desktop ----------------

internal sealed class ClaudeDesktopClient : McpClient
{
    public override string Name => "claude-desktop";

    public override string? ConfigPath
    {
        get
        {
            string dir;
            if (OperatingSystem.IsWindows())
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Claude");
            else if (OperatingSystem.IsMacOS())
                dir = Path.Combine(Home, "Library", "Application Support", "Claude");
            else
                return null;

            return Path.Combine(dir, "claude_desktop_config.json");
        }
    }

    protected override Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        var path = ConfigPath;
        if (path == null || !File.Exists(path)) return Task.FromResult(false);
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            return Task.FromResult(root?["mcpServers"]?.AsObject()?.ContainsKey("eidet") == true);
        }
        catch { return Task.FromResult(false); }
    }

    // Claude Desktop has no install CLI — only direct file edits.
    protected override Task<(bool, string)> InstallViaCliAsync(CancellationToken ct) =>
        Task.FromResult((false, "no CLI"));

    protected override Task<(bool, string)> InstallViaFileAsync(CancellationToken ct) =>
        Task.FromResult(McpJsonFile.AddEidetServer(ConfigPath!, Name));
}

// ---------------- Codex ----------------

internal sealed class CodexClient : McpClient
{
    public override string Name => "codex";
    public override string? ConfigPath => Path.Combine(Home, ".codex", "config.toml");
    protected override string? CliCommand => "codex";

    private static readonly Regex EidetHeader =
        new(@"^\s*\[mcp_servers\.eidet\]\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    protected override Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        var path = ConfigPath;
        if (path == null || !File.Exists(path)) return Task.FromResult(false);
        try
        {
            var text = File.ReadAllText(path);
            return Task.FromResult(EidetHeader.IsMatch(text));
        }
        catch { return Task.FromResult(false); }
    }

    protected override async Task<(bool, string)> InstallViaCliAsync(CancellationToken ct)
    {
        var (code, output) = await RunProcessAsync("codex", "mcp add eidet -- eidet mcp", ct);
        return code == 0
            ? (true, output.Trim())
            : (false, $"codex mcp add failed: {output.Trim()}");
    }

    protected override Task<(bool, string)> InstallViaFileAsync(CancellationToken ct)
    {
        var path = ConfigPath!;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (EidetHeader.IsMatch(existing))
            return Task.FromResult((false, $"{Name}: already present in {path}"));

        var append = (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "")
            + "\n[mcp_servers.eidet]\ncommand = \"eidet\"\nargs = [\"mcp\"]\n";
        File.WriteAllText(path, existing + append);
        return Task.FromResult((true, $"{Name}: appended [mcp_servers.eidet] to {path}"));
    }
}

// ---------------- Gemini CLI ----------------

internal sealed class GeminiClient : McpClient
{
    public override string Name => "gemini";

    /// <summary>Gemini's `mcp add` writes project-scoped settings; there is no
    /// well-known global config we can reliably patch. We rely on the CLI.</summary>
    public override string? ConfigPath => null;

    protected override string? CliCommand => "gemini";

    protected override async Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        if (!IsExecutableOnPath("gemini")) return false;
        var (code, output) = await RunProcessAsync("gemini", "mcp list", ct);
        if (code != 0) return false;
        return output.Contains("eidet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installs at user scope so it survives across projects.
    /// The command + args are quoted as one positional so Gemini parses
    /// them as a single command line (matching `gemini mcp add &lt;name&gt; "cmd args"`).</summary>
    protected override async Task<(bool, string)> InstallViaCliAsync(CancellationToken ct)
    {
        var (code, output) = await RunProcessAsync(
            "gemini", "mcp add eidet \"eidet mcp\" -s user", ct);
        return code == 0
            ? (true, output.Trim())
            : (false, $"gemini mcp add failed: {output.Trim()}");
    }

    protected override Task<(bool, string)> InstallViaFileAsync(CancellationToken ct) =>
        Task.FromResult((false, $"{Name}: no fallback — install via the `gemini` CLI"));
}

// ---------------- shared file-write helper ----------------

/// <summary>
/// Adds <c>mcpServers.eidet = {command:"eidet", args:["mcp"]}</c> to a JSON
/// config file. Preserves any existing top-level keys and other servers.
/// Returns <c>(false, "...already present")</c> if the entry exists.
/// </summary>
internal static class McpJsonFile
{
    public static (bool Success, string Detail) AddEidetServer(string configPath, string clientName)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null) Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (!root.ContainsKey("mcpServers"))
            root["mcpServers"] = new JsonObject();
        var servers = root["mcpServers"]!.AsObject();

        if (servers.ContainsKey("eidet"))
            return (false, $"{clientName}: already present in {configPath}");

        servers["eidet"] = new JsonObject
        {
            ["command"] = "eidet",
            ["args"] = new JsonArray("mcp"),
        };

        File.WriteAllText(configPath, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return (true, $"{clientName}: wrote mcpServers.eidet to {configPath}");
    }
}
