using System.Text.Json.Nodes;

namespace Eidet.Service.Mcp;

public static class McpToolDefinitions
{
    public static List<McpToolDefinition> GetAll() =>
    [
        new()
        {
            Name = "eidet_store",
            Description = "Store a memory (observation, insight, procedure, or heuristic). Content is validated through secret scanning and signal gates before storage.",
            InputSchema = Schema([
                Prop("content", "string", "The memory content to store. Must be 20+ chars, specific, and self-contained."),
                Prop("type", "string", "Memory type: observation, insight, procedure, or heuristic."),
                PropOptional("tags", "array", "Tags for filtering and discovery.", items: "string"),
                PropOptional("importance", "number", "Importance score 0.0-1.0 (default 0.5)."),
                PropOptional("supersedes", "string", "ID of memory this replaces (creates version chain)."),
                PropOptional("provenance", "string", "Origin: user_stated, agent_inferred, tool_output."),
            ], ["content", "type"]),
        },
        new()
        {
            Name = "eidet_recall",
            Description = "Search memories using hybrid retrieval (vector similarity + full-text + metadata filters). Returns scored results with staleness warnings.",
            InputSchema = Schema([
                Prop("query", "string", "Natural language search query."),
                PropOptional("type", "string", "Filter by type: observation, insight, procedure, heuristic."),
                PropOptional("tags", "array", "Filter by tags (AND logic).", items: "string"),
                PropOptional("limit", "integer", "Max results 1-50 (default 10)."),
                PropOptional("include_expired", "boolean", "Include forgotten/expired memories (default false)."),
                PropOptional("cross_repo", "boolean", "Search linked repos and layers (default true)."),
            ], ["query"]),
        },
        new()
        {
            Name = "eidet_context",
            Description = "Get compact L0 (identity) + L1 (top-K scored memories) context block for session start. Under 600 tokens.",
            InputSchema = Schema([
                PropOptional("max_tokens", "integer", "Token budget (default 600)."),
            ], []),
        },
        new()
        {
            Name = "eidet_forget",
            Description = "Soft-delete a memory by setting its validity end date. Creates an audit trail observation.",
            InputSchema = Schema([
                Prop("id", "string", "Memory ID to forget."),
                PropOptional("reason", "string", "Why this memory is being forgotten."),
            ], ["id"]),
        },
        new()
        {
            Name = "eidet_feedback",
            Description = "Provide echo (used) or fizzle (not used) feedback on a recalled memory. Adjusts importance and confidence scores.",
            InputSchema = Schema([
                Prop("id", "string", "Memory ID to provide feedback on."),
                Prop("used", "boolean", "true = echo (memory was useful), false = fizzle (memory was irrelevant)."),
            ], ["id", "used"]),
        },
        new()
        {
            Name = "eidet_history",
            Description = "Get the version chain for a memory, showing how it evolved over time via supersession.",
            InputSchema = Schema([
                Prop("id", "string", "Memory ID to get history for."),
            ], ["id"]),
        },
        new()
        {
            Name = "eidet_intake",
            Description = "Ingest project files (CLAUDE.md, README, .editorconfig, dependencies) as seed memories. Idempotent — skips duplicates.",
            InputSchema = Schema([
                PropOptional("path", "string", "Directory to ingest docs from (default: project root)."),
                PropOptional("pattern", "string", "File glob pattern (default: *.md)."),
                PropOptional("recursive", "boolean", "Recurse into subdirectories (default: true)."),
                PropOptional("importance", "number", "Default importance for ingested memories (default: 0.6)."),
                PropOptional("tags", "array", "Extra tags to add to all ingested memories.", items: "string"),
                PropOptional("dry_run", "boolean", "Preview what would be ingested without storing."),
            ], []),
        },
        new()
        {
            Name = "eidet_link",
            Description = "Create a cross-repo or memory-to-memory relationship link.",
            InputSchema = Schema([
                Prop("target_repo", "string", "Target repository path or repoId."),
                Prop("relation", "string", "Relationship: depends-on, uses-library, forked-from, related, supports, conflicts, refines."),
                PropOptional("target_memory_id", "string", "Specific memory ID to link to (omit for repo-level link)."),
            ], ["target_repo", "relation"]),
        },
        new()
        {
            Name = "eidet_consolidate",
            Description = "Merge related observations into stable insights. Groups observations by shared tags.",
            InputSchema = Schema([
                PropOptional("dry_run", "boolean", "Preview consolidation candidates without creating insights."),
            ], []),
        },
        new()
        {
            Name = "eidet_maintenance",
            Description = "Run the maintenance pipeline: TTL expiry, observation retention, dedup sweep, importance decay, orphan cleanup, auto-consolidation.",
            InputSchema = Schema([
                PropOptional("dry_run", "boolean", "Preview only (not yet supported)."),
            ], []),
        },
        new()
        {
            Name = "eidet_edit",
            Description = "Edit an existing memory. Can update content (creates new version), tags, importance, confidence, or type. Use for curating and correcting memories.",
            InputSchema = Schema([
                Prop("id", "string", "Memory ID to edit."),
                PropOptional("content", "string", "New content (creates versioned update if changed)."),
                PropOptional("tags", "array", "New tags (replaces existing).", items: "string"),
                PropOptional("importance", "number", "New importance score 0.0-1.0."),
                PropOptional("confidence", "number", "New confidence score 0.0-1.0."),
                PropOptional("type", "string", "New type: observation, insight, procedure, heuristic."),
            ], ["id"]),
        },
    ];

    private static JsonObject Schema((string name, JsonObject prop)[] properties, string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        var props = new JsonObject();
        foreach (var (name, prop) in properties)
            props[name] = prop;
        schema["properties"] = props;

        if (required.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var r in required) arr.Add(r);
            schema["required"] = arr;
        }

        return schema;
    }

    private static (string name, JsonObject prop) Prop(string name, string type, string description) =>
        (name, new JsonObject { ["type"] = type, ["description"] = description });

    private static (string name, JsonObject prop) PropOptional(string name, string type, string description, string? items = null)
    {
        var obj = new JsonObject { ["type"] = type, ["description"] = description };
        if (items != null)
            obj["items"] = new JsonObject { ["type"] = items };
        return (name, obj);
    }
}
