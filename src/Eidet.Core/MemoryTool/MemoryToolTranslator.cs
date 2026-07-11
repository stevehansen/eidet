using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;

namespace Eidet.Core.MemoryTool;

/// <summary>
/// The deep module behind the Claude <c>memory_20250818</c> tool: a single
/// <see cref="ExecuteAsync"/> entry handling all six commands over faithful path-keyed blobs.
/// It hides the line-number rendering, directory listings, exact success/error strings,
/// <c>str_replace</c> occurrence counting, <c>insert</c> bounds math, root protection, secret
/// gating, the file-size cap, and repo isolation (bound at construction). Never throws for
/// expected failures — everything the model can cause comes back as an <c>is_error</c> result.
/// </summary>
public sealed class MemoryToolTranslator
{
    // Reserved read-only subtree that surfaces IMemoryBridge.RecallAsync as a virtual directory.
    private const string RecallDir = MemoryPath.Root + "/.recall";
    private const int RecallLimit = 5;

    private readonly IMemoryFileStore _files;
    private readonly IMemoryBridge _bridge;
    private readonly MemoryToolOptions _options;
    private readonly string _repoId;

    public MemoryToolTranslator(IMemoryFileStore files, string repoId, IMemoryBridge? bridge = null,
        MemoryToolOptions? options = null)
    {
        _files = files;
        _repoId = RepoIdNormalizer.Normalize(repoId);
        _bridge = bridge ?? NullMemoryBridge.Instance;
        _options = options ?? new MemoryToolOptions();
    }

    /// <summary>THE entry point — all six commands, plus malformed input via <see cref="MemoryCommand.Invalid"/>.</summary>
    public async Task<MemoryToolResult> ExecuteAsync(MemoryCommand cmd, CancellationToken ct = default)
    {
        try
        {
            return cmd switch
            {
                MemoryCommand.Invalid c => Error(c.Message),
                MemoryCommand.View c => await ViewAsync(c, ct),
                MemoryCommand.Create c => await CreateAsync(c, ct),
                MemoryCommand.StrReplace c => await StrReplaceAsync(c, ct),
                MemoryCommand.Insert c => await InsertAsync(c, ct),
                MemoryCommand.Delete c => await DeleteAsync(c, ct),
                MemoryCommand.Rename c => await RenameAsync(c, ct),
                _ => Error($"Unsupported memory command `{cmd.GetType().Name}`."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Storage faults are not the model's doing; keep the tool loop alive with a
            // generic error and log the details server-side (no ex.Message leak to the model).
            EidetLog.Error($"memory-tool command {cmd.GetType().Name} failed for repo {_repoId}", ex);
            return Error("The memory tool encountered an internal error. Please try again.");
        }
    }

    // ─── view ─────────────────────────────────────────────────────────────

    private async Task<MemoryToolResult> ViewAsync(MemoryCommand.View cmd, CancellationToken ct)
    {
        if (IsRecallPath(cmd.Path))
            return await RecallViewAsync(cmd.Path, ct);

        var content = await _files.ReadAsync(_repoId, cmd.Path.Value, ct);
        if (content is not null)
            return Ok(RenderFile(cmd.Path, content, cmd.Range));

        var children = await _files.ListAsync(_repoId, cmd.Path.Value, ct);
        if (cmd.Path.IsRoot || children.Count > 0)
            return Ok(RenderDirectory(cmd.Path, children));

        return Error($"The path {cmd.Path} does not exist. Please provide a valid path.");
    }

    private static string RenderFile(MemoryPath path, string content, (int Start, int End)? range)
    {
        var lines = SplitLines(content);
        var start = 1;
        var end = lines.Count;
        if (range is { } r)
        {
            start = Math.Max(1, r.Start);
            end = r.End == -1 ? lines.Count : Math.Min(lines.Count, r.End);
        }

        var sb = new StringBuilder();
        sb.Append("Here's the content of ").Append(path.Value).Append(" with line numbers:\n");
        for (var i = start; i <= end; i++)
            sb.Append(i.ToString().PadLeft(6)).Append('\t').Append(lines[i - 1]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderDirectory(MemoryPath dir, IReadOnlyList<string> filePaths)
    {
        // Blobs have no stored directories — subdirectories are implied by deeper paths.
        var prefixLength = dir.Value.Length + 1;
        var subdirs = new SortedSet<string>(StringComparer.Ordinal);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in filePaths)
        {
            var remainder = path[prefixLength..];
            var slash = remainder.IndexOf('/');
            if (slash >= 0) subdirs.Add(remainder[..slash]);
            else files.Add(remainder);
        }

        var sb = new StringBuilder();
        sb.Append("Here're the files and directories in ").Append(dir.Value).Append(':');
        foreach (var name in subdirs)
            sb.Append('\n').Append("DIR\t").Append(name).Append('/');
        foreach (var name in files)
            sb.Append('\n').Append(name);
        return sb.ToString();
    }

    private async Task<MemoryToolResult> RecallViewAsync(MemoryPath path, CancellationToken ct)
    {
        if (path.Value.Length <= RecallDir.Length + 1)
            return Error($"Provide a query as {RecallDir}/<query>.");

        var query = Uri.UnescapeDataString(path.Value[(RecallDir.Length + 1)..]);
        var hits = await _bridge.RecallAsync(_repoId, query, RecallLimit, ct);
        if (hits.Count == 0)
            return Ok($"No recall results for \"{query}\".");

        var sb = new StringBuilder();
        sb.Append("Recall results for \"").Append(query).Append("\":");
        foreach (var (hitPath, snippet) in hits)
            sb.Append('\n').Append("- ").Append(hitPath).Append(": ").Append(snippet);
        return Ok(sb.ToString());
    }

    // ─── create ───────────────────────────────────────────────────────────

    private async Task<MemoryToolResult> CreateAsync(MemoryCommand.Create cmd, CancellationToken ct)
    {
        if (GuardWritablePath(cmd.Path) is { } pathError) return pathError;

        // Overwriting a FILE in place is the tool contract; shadowing a directory is not.
        if (!await _files.ExistsAsync(_repoId, cmd.Path.Value, ct) &&
            (await _files.ListAsync(_repoId, cmd.Path.Value, ct)).Count > 0)
            return Error($"The path {cmd.Path} is a directory and cannot be overwritten with a file.");

        var content = cmd.FileText;
        if (GuardContent(ref content, out var redactions) is { } contentError) return contentError;

        await _files.WriteAsync(_repoId, cmd.Path.Value, content, ct);
        await TryPromoteAsync(cmd.Path, content, ct);
        return Ok($"File created successfully at: {cmd.Path}" + RedactionNote(redactions));
    }

    // ─── str_replace ──────────────────────────────────────────────────────

    private async Task<MemoryToolResult> StrReplaceAsync(MemoryCommand.StrReplace cmd, CancellationToken ct)
    {
        if (GuardWritablePath(cmd.Path) is { } pathError) return pathError;

        var content = await _files.ReadAsync(_repoId, cmd.Path.Value, ct);
        if (content is null)
            return Error($"The path {cmd.Path} does not exist. Please provide a valid path.");

        var occurrenceLines = FindOccurrenceLines(content, cmd.OldStr);
        if (occurrenceLines.Count == 0)
            return Error($"No replacement was performed, old_str `{cmd.OldStr}` did not appear verbatim in {cmd.Path}.");
        if (occurrenceLines.Count > 1)
            return Error($"No replacement was performed. Multiple occurrences of old_str `{cmd.OldStr}` " +
                $"in lines: {string.Join(", ", occurrenceLines)}. Please ensure it is unique");

        var updated = content.Replace(cmd.OldStr, cmd.NewStr ?? "", StringComparison.Ordinal);
        if (GuardContent(ref updated, out var redactions) is { } contentError) return contentError;

        await _files.WriteAsync(_repoId, cmd.Path.Value, updated, ct);
        await TryPromoteAsync(cmd.Path, updated, ct);
        return Ok($"The memory file {cmd.Path} has been edited." + RedactionNote(redactions));
    }

    private static List<int> FindOccurrenceLines(string content, string oldStr)
    {
        var lines = new List<int>();
        if (oldStr.Length == 0) return lines;
        var index = content.IndexOf(oldStr, StringComparison.Ordinal);
        while (index != -1)
        {
            lines.Add(content.AsSpan(0, index).Count('\n') + 1);
            index = content.IndexOf(oldStr, index + oldStr.Length, StringComparison.Ordinal);
        }
        return lines;
    }

    // ─── insert ───────────────────────────────────────────────────────────

    private async Task<MemoryToolResult> InsertAsync(MemoryCommand.Insert cmd, CancellationToken ct)
    {
        if (GuardWritablePath(cmd.Path) is { } pathError) return pathError;

        var content = await _files.ReadAsync(_repoId, cmd.Path.Value, ct);
        if (content is null)
            return Error($"The path {cmd.Path} does not exist. Please provide a valid path.");

        // Splice on logical lines but preserve the original trailing-newline shape byte-exactly.
        var hadTrailingNewline = content.EndsWith('\n');
        var lines = SplitLines(content);
        if (cmd.InsertLine < 0 || cmd.InsertLine > lines.Count)
            return Error($"Invalid `insert_line` parameter: {cmd.InsertLine}. " +
                $"It should be within the range of lines of the file: [0, {lines.Count}]");

        lines.Insert(cmd.InsertLine, cmd.InsertText);
        var updated = string.Join('\n', lines) + (hadTrailingNewline ? "\n" : "");
        if (GuardContent(ref updated, out var redactions) is { } contentError) return contentError;

        await _files.WriteAsync(_repoId, cmd.Path.Value, updated, ct);
        await TryPromoteAsync(cmd.Path, updated, ct);
        return Ok($"The file {cmd.Path} has been edited." + RedactionNote(redactions));
    }

    // ─── delete ───────────────────────────────────────────────────────────

    private async Task<MemoryToolResult> DeleteAsync(MemoryCommand.Delete cmd, CancellationToken ct)
    {
        if (cmd.Path.IsRoot)
            return Error($"The root directory {MemoryPath.Root} cannot be deleted.");
        if (IsRecallPath(cmd.Path))
            return Error($"The path {RecallDir} is read-only.");

        if (await _files.ExistsAsync(_repoId, cmd.Path.Value, ct))
        {
            await _files.DeleteAsync(_repoId, cmd.Path.Value, ct);
            return Ok($"Successfully deleted {cmd.Path}");
        }

        var children = await _files.ListAsync(_repoId, cmd.Path.Value, ct);
        if (children.Count == 0)
            return Error($"The path {cmd.Path} does not exist");

        foreach (var child in children)
            await _files.DeleteAsync(_repoId, child, ct);
        return Ok($"Successfully deleted {cmd.Path}");
    }

    // ─── rename ───────────────────────────────────────────────────────────

    private async Task<MemoryToolResult> RenameAsync(MemoryCommand.Rename cmd, CancellationToken ct)
    {
        if (cmd.OldPath.IsRoot || cmd.NewPath.IsRoot)
            return Error($"The root directory {MemoryPath.Root} cannot be renamed.");
        if (IsRecallPath(cmd.OldPath) || IsRecallPath(cmd.NewPath))
            return Error($"The path {RecallDir} is read-only.");

        if (await _files.ExistsAsync(_repoId, cmd.NewPath.Value, ct) ||
            (await _files.ListAsync(_repoId, cmd.NewPath.Value, ct)).Count > 0)
            return Error($"The destination {cmd.NewPath} already exists");

        if (await _files.ExistsAsync(_repoId, cmd.OldPath.Value, ct))
        {
            await _files.MoveAsync(_repoId, cmd.OldPath.Value, cmd.NewPath.Value, ct);
            return Ok($"Successfully renamed {cmd.OldPath} to {cmd.NewPath}");
        }

        var children = await _files.ListAsync(_repoId, cmd.OldPath.Value, ct);
        if (children.Count == 0)
            return Error($"The path {cmd.OldPath} does not exist");

        foreach (var child in children)
            await _files.MoveAsync(_repoId, child, cmd.NewPath.Value + child[cmd.OldPath.Value.Length..], ct);
        return Ok($"Successfully renamed {cmd.OldPath} to {cmd.NewPath}");
    }

    // ─── shared guards + helpers ──────────────────────────────────────────

    private MemoryToolResult? GuardWritablePath(MemoryPath path)
    {
        if (path.IsRoot)
            return Error($"The root directory {MemoryPath.Root} is not a file.");
        if (IsRecallPath(path))
            return Error($"The path {RecallDir} is read-only.");
        return null;
    }

    /// <summary>
    /// The write gate for memory files: ONLY the always-on secret scan plus the size cap.
    /// The semantic store's low-signal/self-talk gates are deliberately not applied —
    /// rejecting Claude's legitimately short scratch files would break the filesystem contract.
    /// Under <see cref="SecretPolicy.Redact"/> the content is rewritten in place.
    /// </summary>
    private MemoryToolResult? GuardContent(ref string content, out int redactions)
    {
        redactions = 0;
        if (Encoding.UTF8.GetByteCount(content) > _options.MaxFileBytes)
            return Error($"File exceeds the maximum size of {_options.MaxFileBytes} bytes. Nothing was stored.");

        var scan = WriteValidator.ScanSecrets(content);
        if (scan.Passed) return null;

        if (_options.Secrets == SecretPolicy.Reject)
        {
            const string prefix = "Blocked: ";
            var detail = scan.Reason!.StartsWith(prefix, StringComparison.Ordinal) ? scan.Reason[prefix.Length..] : scan.Reason;
            return Error($"Write blocked: {detail}. Nothing was stored — remove the secret and retry.");
        }

        content = WriteValidator.RedactSecrets(content, out redactions);
        return null;
    }

    private async Task TryPromoteAsync(MemoryPath path, string content, CancellationToken ct)
    {
        try
        {
            await _bridge.PromoteAsync(_repoId, path.Value, content, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort by contract: the blob write already succeeded and is the source of truth.
            EidetLog.Warn($"memory-tool promotion failed for {path} in repo {_repoId}: {ex.Message}");
        }
    }

    private static bool IsRecallPath(MemoryPath path) =>
        path.Value == RecallDir || path.Value.StartsWith(RecallDir + "/", StringComparison.Ordinal);

    /// <summary>Appended to write successes under <see cref="SecretPolicy.Redact"/> — round-trip honesty about the altered bytes.</summary>
    private static string RedactionNote(int redactions) =>
        redactions == 0 ? "" : $" Warning: {redactions} potential secret(s) were redacted before storage.";

    /// <summary>Logical lines: split on <c>\n</c>, dropping the phantom element a trailing newline creates.</summary>
    private static List<string> SplitLines(string content)
    {
        if (content.Length == 0) return [];
        var lines = new List<string>(content.Split('\n'));
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static MemoryToolResult Ok(string text) => new(false, text);
    private static MemoryToolResult Error(string text) => new(true, text);
}
