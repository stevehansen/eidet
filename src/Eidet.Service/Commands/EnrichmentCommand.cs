using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

/// <summary>
/// Probes for enrichment backends: Ollama (native API) and OpenAI-compatible servers
/// (LM Studio, llama.cpp, vLLM — local or a private cluster behind a bearer token). Shared by
/// <c>eidet setup</c> and <c>eidet enrichment setup</c>.
/// </summary>
internal static class EnrichmentDetector
{
    public const string OllamaDefaultUrl = "http://localhost:11434";
    public const string OpenAiDefaultUrl = "http://localhost:1234"; // LM Studio's default port

    public sealed record DetectedBackend(EnrichmentProvider Provider, string Url, List<string> Models, string? ApiKey = null)
    {
        public string Label => Provider == EnrichmentProvider.Ollama
            ? "Ollama"
            : "OpenAI-compatible (LM Studio / llama.cpp / vLLM)";

        public string Describe() => $"{Label} @ {Url} — {Models.Count} model(s)";
    }

    /// <summary>
    /// Probes the configured URL plus both well-known local defaults, in parallel.
    /// One entry per reachable backend, Ollama first.
    /// </summary>
    public static async Task<List<DetectedBackend>> DetectAsync(EnrichmentConfig current, CancellationToken ct)
    {
        var candidates = new List<(EnrichmentProvider Provider, string Url, string? ApiKey)>
        {
            (EnrichmentProvider.Ollama, OllamaDefaultUrl, null),
            (EnrichmentProvider.OpenAiCompatible, OpenAiDefaultUrl, null),
        };
        if (!candidates.Any(c => c.Provider == current.Provider && UrlsEqual(c.Url, current.Url)))
            candidates.Insert(0, (current.Provider, current.Url, current.ApiKey));

        var results = await Task.WhenAll(candidates.Select(c => ProbeAsync(c.Provider, c.Url, c.ApiKey, ct)));
        return results.OfType<DetectedBackend>()
            .OrderBy(r => r.Provider == EnrichmentProvider.Ollama ? 0 : 1)
            .ToList();
    }

    /// <summary>Probes a single backend; null when it does not answer (offline, or rejecting the key).</summary>
    public static async Task<DetectedBackend?> ProbeAsync(EnrichmentProvider provider, string url, string? apiKey, CancellationToken ct)
    {
        try
        {
            if (provider == EnrichmentProvider.Ollama)
            {
                using var ollama = new OllamaService(url);
                if (!await ollama.IsAvailableAsync(ct)) return null;
                var models = await ollama.ListModelsAsync(ct);
                return new DetectedBackend(provider, url, models.Select(m => m.Name).ToList(), apiKey);
            }

            using var openAi = new OpenAiCompatibleService(url, apiKey);
            var ids = await openAi.TryListModelsAsync(ct);
            return ids is null ? null : new DetectedBackend(provider, url, ids, apiKey);
        }
        catch
        {
            return null;
        }
    }

    public static bool UrlsEqual(string a, string b) =>
        string.Equals(EnrichmentHttp.NormalizeBaseUrl(a), EnrichmentHttp.NormalizeBaseUrl(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Talks to the running service's <c>POST /api/config/enrichment/reload</c> endpoint so config
/// changes apply without a restart. Targets the instance from the service lock file when one
/// is running (it may be on a non-default port), falling back to the configured bind/port.
/// </summary>
internal static class EnrichmentReloadClient
{
    public static string BaseUrl(EidetConfig config)
    {
        var running = ServiceLock.Read();
        var bind = running?.BindAddress ?? config.Service.BindAddress;
        var port = running?.Port ?? config.Service.Port;
        if (bind is "0.0.0.0" or "+" or "*") bind = "127.0.0.1";
        return $"http://{bind}:{port}";
    }

    public static async Task<bool> IsServiceRunningAsync(EidetConfig config, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync($"{BaseUrl(config)}/api/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>POSTs the reload endpoint and reports the outcome on the console. 0 = applied.</summary>
    public static async Task<int> ReloadAndReportAsync(EidetConfig config, string? apiKey, CancellationToken ct)
    {
        var baseUrl = BaseUrl(config);
        int status;
        string body = "";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            using var resp = await http.PostAsync($"{baseUrl}/api/config/enrichment/reload", content: null, ct);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            AnsiConsole.MarkupLine($"  [yellow]No running service[/] at {baseUrl} — settings apply on next start ([dim]eidet serve[/]).");
            return 1;
        }

        switch (status)
        {
            case 200:
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    var enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean();
                    var healthy = root.TryGetProperty("healthy", out var h) && h.GetBoolean();
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() : "";
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : "";
                    if (!enabled)
                        AnsiConsole.MarkupLine("  [green]✓[/] Service reloaded — enrichment disabled");
                    else
                        AnsiConsole.MarkupLine($"  [green]✓[/] Service reloaded — {(healthy ? "[green]Connected[/]" : "[yellow]Unavailable[/]")} ({Markup.Escape(model ?? "")} @ {Markup.Escape(url ?? "")})");
                    // Same verdict as the startup banner, so "is the nightly model work on?" is
                    // answered by the reload rather than by the next restart.
                    if (root.TryGetProperty("nightlyModelWork", out var nightly))
                    {
                        var nightlyOn = root.TryGetProperty("nightlyModelWorkEnabled", out var ne) && ne.GetBoolean();
                        AnsiConsole.MarkupLine(nightlyOn
                            ? $"    Nightly AI: [green]Active[/] ({Markup.Escape(nightly.GetString() ?? "")})"
                            : $"    Nightly AI: [dim]Off[/] ({Markup.Escape(nightly.GetString() ?? "")})");
                    }
                }
                return 0;
            case 401 or 403:
                AnsiConsole.MarkupLine("  [yellow]Authentication required[/] — pass an admin-scope key: [dim]eidet enrichment reload --api-key <KEY>[/]");
                return 1;
            default:
                AnsiConsole.MarkupLine($"  [red]Reload failed[/] (HTTP {status})");
                return 1;
        }
    }
}

/// <summary>
/// Interactive wizard that configures the whole enrichment section in one pass —
/// provider, URL, model (picked from the backend's live model list), enabled —
/// so the config can never end up half-edited, then offers to apply it to the
/// running service without a restart.
/// </summary>
public sealed class EnrichmentSetupCommand : AsyncCommand<EnrichmentSetupCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{EidetVersion.Current} — Enrichment setup");
        AnsiConsole.WriteLine();

        var config = ConfigManager.Load();

        var detected = new List<EnrichmentDetector.DetectedBackend>();
        await AnsiConsole.Status().StartAsync("Detecting local enrichment backends...", async _ =>
        {
            detected = await EnrichmentDetector.DetectAsync(config.Enrichment, cancellation);
        });

        foreach (var d in detected)
            AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(d.Describe())}");
        if (detected.Count == 0)
            AnsiConsole.MarkupLine("  [yellow]No local backend found[/] (Ollama, or an OpenAI-compatible server like LM Studio)");
        AnsiConsole.WriteLine();

        const string customChoice = "Custom URL...";
        const string disableChoice = "Disable enrichment";
        var choices = detected.Select(d => d.Describe()).Append(customChoice).Append(disableChoice).ToList();
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Enrichment backend:")
            .AddChoices(choices));

        if (picked == disableChoice)
        {
            config.Enrichment.Enabled = false;
            ConfigManager.Save(config);
            AnsiConsole.MarkupLine($"  [green]✓[/] Enrichment disabled — config saved");
            await OfferLiveApplyAsync(config, cancellation);
            return 0;
        }

        var backend = picked == customChoice
            ? await AskCustomBackendAsync(config.Enrichment, cancellation)
            : detected[choices.IndexOf(picked)];

        var model = AskModel(backend, config.Enrichment.Model);
        var thinking = AskThinking(backend, config.Enrichment);
        var fallbacks = KeepPreviousAsFallback(config.Enrichment, backend);

        // The whole backend in one save — no half-configured state.
        config.Enrichment.Enabled = true;
        config.Enrichment.Provider = backend.Provider;
        config.Enrichment.Url = backend.Url;
        config.Enrichment.Model = model;
        config.Enrichment.ApiKey = backend.ApiKey;
        config.Enrichment.Thinking = thinking;
        config.Enrichment.Fallbacks = fallbacks;
        ConfigManager.Save(config);

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Simple).AddColumn("Setting").AddColumn("Value");
        table.AddRow("enrichment.enabled", "true");
        table.AddRow("enrichment.provider", backend.Provider.ToString());
        table.AddRow("enrichment.url", Markup.Escape(backend.Url));
        table.AddRow("enrichment.model", Markup.Escape(model));
        if (backend.ApiKey is { Length: > 0 })
            table.AddRow("enrichment.apiKey", "(set)");
        if (thinking is { } think)
            table.AddRow("enrichment.thinking", think.ToString().ToLowerInvariant());
        foreach (var (fallback, i) in fallbacks.Select((f, i) => (f, i)))
            table.AddRow($"enrichment.fallbacks[{i}]", Markup.Escape($"{fallback.Provider} @ {fallback.Url} ({fallback.Model})"));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"  [green]✓[/] Config saved to {Markup.Escape(ConfigManager.GetConfigPath())}");
        AnsiConsole.WriteLine();

        await OfferLiveApplyAsync(config, cancellation);
        return 0;
    }

    private static async Task<EnrichmentDetector.DetectedBackend> AskCustomBackendAsync(
        EnrichmentConfig current, CancellationToken ct)
    {
        var provider = AnsiConsole.Prompt(new SelectionPrompt<EnrichmentProvider>()
            .Title("Server type:")
            .UseConverter(p => p == EnrichmentProvider.Ollama
                ? "Ollama (native API)"
                : "OpenAI-compatible (LM Studio / llama.cpp / vLLM)")
            .AddChoices(EnrichmentProvider.Ollama, EnrichmentProvider.OpenAiCompatible));

        var defaultUrl = current.Provider == provider
            ? current.Url
            : provider == EnrichmentProvider.Ollama
                ? EnrichmentDetector.OllamaDefaultUrl
                : EnrichmentDetector.OpenAiDefaultUrl;
        var url = AnsiConsole.Ask("Server URL:", defaultUrl);

        // Only OpenAI-compatible servers are asked: that is where a private cluster behind a
        // gateway lives. An empty answer keeps the key already configured for this same server,
        // so re-running the wizard never silently drops working auth.
        string? apiKey = null;
        if (provider == EnrichmentProvider.OpenAiCompatible)
        {
            var sameServer = current.Provider == provider && EnrichmentDetector.UrlsEqual(current.Url, url);
            var keep = sameServer && !string.IsNullOrEmpty(current.ApiKey);
            var entered = AnsiConsole.Prompt(new TextPrompt<string>(
                    keep ? "API key [dim](empty keeps the current one)[/]:" : "API key [dim](empty if the server needs none)[/]:")
                .AllowEmpty()
                .Secret());
            apiKey = entered.Length > 0 ? entered : keep ? current.ApiKey : null;
        }

        var probed = await EnrichmentDetector.ProbeAsync(provider, url, apiKey, ct);
        if (probed is null)
        {
            var why = apiKey is null ? "the server may be offline right now" : "the server may be offline, or the API key rejected";
            AnsiConsole.MarkupLine($"  [yellow]~[/] No response at {Markup.Escape(url)} — configuring anyway ({why})");
            return new EnrichmentDetector.DetectedBackend(provider, url, [], apiKey);
        }

        AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(probed.Describe())}");
        return probed;
    }

    /// <summary>
    /// Only OpenAI-compatible servers get the question — the Ollama adapter already sends
    /// <c>think: false</c>. "No" leaves the setting unset, which puts nothing on the wire.
    /// </summary>
    private static bool? AskThinking(EnrichmentDetector.DetectedBackend backend, EnrichmentConfig current)
    {
        if (backend.Provider != EnrichmentProvider.OpenAiCompatible) return null;
        var off = AnsiConsole.Confirm(
            "Turn model thinking off? [dim](recommended for reasoning models on vLLM — DeepSeek, Qwen; ~5x faster per call)[/]",
            defaultValue: current.Thinking == false);
        return off ? false : null;
    }

    /// <summary>
    /// Switching backends offers to keep the old one behind the new — the common move is from an
    /// always-on local model to a faster network one that is not always reachable. Existing
    /// fallbacks survive; an entry that now equals the new primary is dropped.
    /// </summary>
    private static List<EnrichmentBackendConfig> KeepPreviousAsFallback(EnrichmentConfig current, EnrichmentDetector.DetectedBackend picked)
    {
        var fallbacks = current.Fallbacks.Where(f => !SameServer(f, picked.Provider, picked.Url)).ToList();
        var switching = current.Enabled && !SameServer(current, picked.Provider, picked.Url);
        if (!switching || fallbacks.Any(f => SameServer(f, current.Provider, current.Url)))
            return fallbacks;

        if (AnsiConsole.Confirm(
                $"Keep {current.Provider} @ {Markup.Escape(current.Url)} ({Markup.Escape(current.Model)}) as a fallback for when the new backend is offline?",
                defaultValue: true))
        {
            fallbacks.Insert(0, new EnrichmentBackendConfig
            {
                Provider = current.Provider,
                Url = current.Url,
                Model = current.Model,
                ApiKey = current.ApiKey,
                Thinking = current.Thinking,
            });
        }
        return fallbacks;
    }

    private static bool SameServer(EnrichmentBackendConfig a, EnrichmentProvider provider, string url) =>
        a.Provider == provider && EnrichmentDetector.UrlsEqual(a.Url, url);

    private static string AskModel(EnrichmentDetector.DetectedBackend backend, string currentModel)
    {
        const string manualChoice = "Enter manually...";

        if (backend.Models.Count == 0)
        {
            AnsiConsole.MarkupLine(backend.Provider == EnrichmentProvider.Ollama
                ? "  [yellow]No models installed[/] — pull one with [dim]eidet ollama pull gemma4[/]"
                : "  [yellow]No models listed[/] — load a model in the server app (e.g. LM Studio) first");
            return AnsiConsole.Ask("Model name:", currentModel);
        }

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Model:")
            .PageSize(15)
            .MoreChoicesText("[dim](scroll for more)[/]")
            .AddChoices(backend.Models.Append(manualChoice)));

        if (picked != manualChoice) return picked;

        var manual = AnsiConsole.Ask("Model name:", currentModel);
        if (!backend.Models.Contains(manual, StringComparer.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine($"  [yellow]~[/] \"{Markup.Escape(manual)}\" is not in the server's model list — double-check the name");
        return manual;
    }

    private static async Task OfferLiveApplyAsync(EidetConfig config, CancellationToken ct)
    {
        if (!await EnrichmentReloadClient.IsServiceRunningAsync(config, ct))
        {
            AnsiConsole.MarkupLine("  [dim]Service not running — settings apply on next start (eidet serve).[/]");
            return;
        }

        if (!AnsiConsole.Confirm("Apply to the running service now?", defaultValue: true))
        {
            AnsiConsole.MarkupLine("  [dim]Apply later with: eidet enrichment reload[/]");
            return;
        }

        await EnrichmentReloadClient.ReloadAndReportAsync(config, apiKey: null, ct);
    }
}

/// <summary>
/// Tells the running service to re-read the Enrichment section of config.json and apply it
/// live (swap adapter/model/url, start the enrich-on-store worker if newly enabled).
/// </summary>
public sealed class EnrichmentReloadCommand : AsyncCommand<EnrichmentReloadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--api-key <KEY>")]
        public string? ApiKey { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        return await EnrichmentReloadClient.ReloadAndReportAsync(config, settings.ApiKey, cancellation);
    }
}
