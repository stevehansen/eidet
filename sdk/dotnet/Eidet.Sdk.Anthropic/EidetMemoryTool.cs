using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic.Core;
using Anthropic.Helpers.Beta;
using Anthropic.Models.Beta.Messages;

namespace Eidet.Sdk.Anthropic;

/// <summary>
/// Drop-in Claude memory-tool backend powered by a local Eidet daemon: pass an instance to
/// <c>client.Beta.Messages.ToolRunner(...)</c> and Claude's <c>memory_20250818</c> commands
/// (view/create/str_replace/insert/delete/rename) are served by Eidet's faithful blob store —
/// path-safe, secret-gated, size-capped, persisted across sessions.
/// <code>
/// var memory = new EidetMemoryTool(repo: "P:/MyApp");   // localhost daemon, cwd default
/// var runner = client.Beta.Messages.ToolRunner(parameters, [memory]);
/// </code>
/// Each command is relayed verbatim to <c>POST /api/eidet/memory-tool</c>; an <c>is_error</c>
/// reply surfaces to the model as a tool error (<see cref="BetaToolError"/>).
/// </summary>
public sealed class EidetMemoryTool : BetaAbstractMemoryTool, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _repo;

    /// <param name="repo">Repository the memory space is bound to; defaults to the current working directory.</param>
    /// <param name="url">Base URL of the Eidet daemon; defaults to the localhost daemon.</param>
    /// <param name="apiKey">API key, required only when the daemon has auth enabled.</param>
    public EidetMemoryTool(string? repo = null, string url = "http://localhost:19380", string? apiKey = null)
    {
        _repo = repo ?? Environment.CurrentDirectory;
        _http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    protected override Task<BetaToolResultBlockParamContent> ViewAsync(
        BetaMemoryTool20250818ViewCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    protected override Task<BetaToolResultBlockParamContent> CreateAsync(
        BetaMemoryTool20250818CreateCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    protected override Task<BetaToolResultBlockParamContent> StrReplaceAsync(
        BetaMemoryTool20250818StrReplaceCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    protected override Task<BetaToolResultBlockParamContent> InsertAsync(
        BetaMemoryTool20250818InsertCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    protected override Task<BetaToolResultBlockParamContent> DeleteAsync(
        BetaMemoryTool20250818DeleteCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    protected override Task<BetaToolResultBlockParamContent> RenameAsync(
        BetaMemoryTool20250818RenameCommand command, CancellationToken cancellationToken) =>
        RelayAsync(command, cancellationToken);

    /// <summary>
    /// Relay one command envelope to the daemon. The SDK's command models serialize back to the
    /// raw wire JSON (command discriminator included), so the daemon-side parser sees exactly
    /// what Claude sent — this adapter adds no interpretation of its own.
    /// </summary>
    private async Task<BetaToolResultBlockParamContent> RelayAsync<TCommand>(
        TCommand command, CancellationToken ct) where TCommand : JsonModel
    {
        using var body = new StringContent(JsonSerializer.Serialize(command), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(
            $"api/eidet/memory-tool?repo={Uri.EscapeDataString(_repo)}", body, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new BetaToolError($"Eidet memory backend returned HTTP {(int)response.StatusCode}: {payload}");

        var result = JsonSerializer.Deserialize<RelayResult>(payload, JsonOptions)
            ?? throw new BetaToolError("Eidet memory backend returned an empty response.");
        if (result.IsError)
            throw new BetaToolError(result.Text);
        return result.Text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record RelayResult(bool IsError, string Text);

    public void Dispose() => _http.Dispose();
}
