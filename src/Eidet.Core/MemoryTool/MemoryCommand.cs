using System.Text.Json;

namespace Eidet.Core.MemoryTool;

/// <summary>
/// The closed <c>memory_20250818</c> command set, plus <see cref="Invalid"/> so transport
/// binding never throws: <see cref="Parse"/> turns malformed input into a command that
/// executes to an <c>is_error</c> result instead of an exception.
/// </summary>
public abstract record MemoryCommand
{
    public sealed record View(MemoryPath Path, (int Start, int End)? Range = null) : MemoryCommand;
    public sealed record Create(MemoryPath Path, string FileText) : MemoryCommand;
    public sealed record StrReplace(MemoryPath Path, string OldStr, string? NewStr) : MemoryCommand;
    public sealed record Insert(MemoryPath Path, int InsertLine, string InsertText) : MemoryCommand;
    public sealed record Delete(MemoryPath Path) : MemoryCommand;
    public sealed record Rename(MemoryPath OldPath, MemoryPath NewPath) : MemoryCommand;

    /// <summary>Malformed transport input — executing it yields an <c>is_error</c> result carrying <paramref name="Message"/>.</summary>
    public sealed record Invalid(string Message) : MemoryCommand;

    private MemoryCommand() { }

    /// <summary>
    /// Transport-boundary binding: maps a raw memory-tool <c>tool_use</c> input envelope
    /// (snake_case wire fields) to a typed command. Never throws — unknown commands, missing
    /// fields, and unsafe paths all come back as <see cref="Invalid"/>.
    /// </summary>
    public static MemoryCommand Parse(JsonElement toolUseInput)
    {
        if (toolUseInput.ValueKind != JsonValueKind.Object)
            return new Invalid("Invalid memory command: expected a JSON object.");

        var command = GetString(toolUseInput, "command");
        return command switch
        {
            "view" => ParseView(toolUseInput),
            "create" => Bind(toolUseInput, "path", (path, input) =>
                GetString(input, "file_text") is { } text
                    ? new Create(path, text)
                    : new Invalid("Invalid `create` command: `file_text` is required.")),
            "str_replace" => Bind(toolUseInput, "path", (path, input) =>
                GetString(input, "old_str") is { } oldStr
                    ? new StrReplace(path, oldStr, GetString(input, "new_str"))
                    : new Invalid("Invalid `str_replace` command: `old_str` is required.")),
            "insert" => Bind(toolUseInput, "path", (path, input) =>
                input.TryGetProperty("insert_line", out var line) &&
                line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var lineNumber)
                    ? GetString(input, "insert_text") is { } text
                        ? new Insert(path, lineNumber, text)
                        : new Invalid("Invalid `insert` command: `insert_text` is required.")
                    : new Invalid("Invalid `insert` command: `insert_line` must be an integer.")),
            "delete" => Bind(toolUseInput, "path", (path, _) => new Delete(path)),
            "rename" => Bind(toolUseInput, "old_path", (oldPath, input) =>
                TryPath(input, "new_path", out var newPath, out var invalid)
                    ? new Rename(oldPath, newPath)
                    : invalid),
            null => new Invalid("Invalid memory command: `command` is required."),
            _ => new Invalid($"Unknown memory command `{command}`."),
        };
    }

    private static MemoryCommand ParseView(JsonElement input)
    {
        (int, int)? range = null;
        if (input.TryGetProperty("view_range", out var vr))
        {
            if (vr.ValueKind != JsonValueKind.Array || vr.GetArrayLength() != 2 ||
                vr[0].ValueKind != JsonValueKind.Number || vr[1].ValueKind != JsonValueKind.Number ||
                !vr[0].TryGetInt32(out var start) || !vr[1].TryGetInt32(out var end))
                return new Invalid("Invalid `view` command: `view_range` must be an array of two integers.");
            range = (start, end);
        }
        return Bind(input, "path", (path, _) => new View(path, range));
    }

    private static MemoryCommand Bind(JsonElement input, string pathField, Func<MemoryPath, JsonElement, MemoryCommand> make) =>
        TryPath(input, pathField, out var path, out var invalid) ? make(path, input) : invalid;

    private static bool TryPath(JsonElement input, string field, out MemoryPath path, out MemoryCommand invalid)
    {
        var raw = GetString(input, field);
        if (raw is null)
        {
            path = default;
            invalid = new Invalid($"Invalid memory command: `{field}` is required.");
            return false;
        }
        if (!MemoryPath.TryParse(raw, out path, out var error))
        {
            invalid = new Invalid(error);
            return false;
        }
        invalid = null!;
        return true;
    }

    private static string? GetString(JsonElement input, string name) =>
        input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
