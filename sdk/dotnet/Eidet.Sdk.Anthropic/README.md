<div align="center">
  <img src="https://raw.githubusercontent.com/stevehansen/eidet/main/logo-512x512.png" alt="Eidet" width="96" height="96">
</div>

# Eidet.Sdk.Anthropic

Anthropic SDK adapter for [Eidet](https://github.com/stevehansen/eidet) — plug Eidet in as the Claude memory-tool (`memory_20250818`) backend.

## Install

```bash
dotnet add package Eidet.Sdk.Anthropic
```

## Usage

Two lines to make a locally running Eidet daemon the persistent memory backend for any Claude tool-use loop:

```csharp
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Eidet.Sdk.Anthropic;

var client = new AnthropicClient();
using var memory = new EidetMemoryTool(repo: "P:/MyApp"); // defaults: localhost daemon, cwd

var runner = client.Beta.Messages.ToolRunner(
    new MessageCreateParams
    {
        Model = "claude-opus-4-8",
        MaxTokens = 4096,
        Messages = [new() { Role = Role.User, Content = "Refactor auth; remember what you learn." }],
    },
    [memory]);

await foreach (var message in runner) { /* Claude drives view/create/str_replace against Eidet */ }
```

Memory files are stored byte-exact in Eidet's `memoryfiles` collection — path-safe (everything constrained under `/memories`), secret-gated (writes containing credentials are rejected with a visible tool error), and size-capped. They persist across sessions and processes.

Requires a running Eidet daemon (`dotnet tool install -g eidet && eidet serve`). Pass `apiKey:` when the daemon has API-key auth enabled.
