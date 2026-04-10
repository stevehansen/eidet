using System.Text.Json;
using Eidet.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class ConfigGetCommand : AsyncCommand<ConfigGetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; set; } = "";
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var value = ConfigHelper.GetValue(config, settings.Key);

        if (value == null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown key:[/] {Markup.Escape(settings.Key)}");
            AnsiConsole.MarkupLine("[dim]Run 'eidet config list' to see all keys.[/]");
            return Task.FromResult(1);
        }

        Console.WriteLine(value);
        return Task.FromResult(0);
    }
}

public sealed class ConfigSetCommand : AsyncCommand<ConfigSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KEY>")]
        public string Key { get; set; } = "";

        [CommandArgument(1, "<VALUE>")]
        public string Value { get; set; } = "";
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        if (!ConfigHelper.SetValue(config, settings.Key, settings.Value))
        {
            AnsiConsole.MarkupLine($"[red]Unknown key:[/] {Markup.Escape(settings.Key)}");
            AnsiConsole.MarkupLine("[dim]Run 'eidet config list' to see all keys.[/]");
            return Task.FromResult(1);
        }

        ConfigManager.Save(config);
        AnsiConsole.MarkupLine($"[green]Set[/] {Markup.Escape(settings.Key)} = {Markup.Escape(settings.Value)}");
        return Task.FromResult(0);
    }
}

public sealed class ConfigListCommand : AsyncCommand<ConfigListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        if (settings.Json)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
            Console.WriteLine(json);
            return Task.FromResult(0);
        }

        var pairs = ConfigHelper.GetAllValues(config);
        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Key")
            .AddColumn("Value");

        foreach (var (key, value) in pairs)
            table.AddRow(Markup.Escape(key), Markup.Escape(value));

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}

internal static class ConfigHelper
{
    private static readonly Dictionary<string, (Func<EidetConfig, string> Get, Action<EidetConfig, string> Set)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Service
        ["service.port"] = (c => c.Service.Port.ToString(), (c, v) => c.Service.Port = int.Parse(v)),
        ["service.bindAddress"] = (c => c.Service.BindAddress, (c, v) => c.Service.BindAddress = v),

        // Storage
        ["storage.mode"] = (c => c.Storage.Mode.ToString(), (c, v) => c.Storage.Mode = Enum.Parse<StorageMode>(v, true)),
        ["storage.ravenUrl"] = (c => c.Storage.RavenUrl, (c, v) => c.Storage.RavenUrl = v),
        ["storage.databaseName"] = (c => c.Storage.DatabaseName, (c, v) => c.Storage.DatabaseName = v),
        ["storage.dataDir"] = (c => c.Storage.DataDir ?? "", (c, v) => c.Storage.DataDir = string.IsNullOrEmpty(v) ? null : v),

        // Memory
        ["memory.l1Count"] = (c => c.Memory.L1Count.ToString(), (c, v) => c.Memory.L1Count = int.Parse(v)),
        ["memory.l1MaxTokens"] = (c => c.Memory.L1MaxTokens.ToString(), (c, v) => c.Memory.L1MaxTokens = int.Parse(v)),
        ["memory.duplicateThreshold"] = (c => c.Memory.DuplicateThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), (c, v) => c.Memory.DuplicateThreshold = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)),
        ["memory.vectorSimilarityMinimum"] = (c => c.Memory.VectorSimilarityMinimum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), (c, v) => c.Memory.VectorSimilarityMinimum = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)),
        ["memory.observationRetentionDays"] = (c => c.Memory.ObservationRetentionDays.ToString(), (c, v) => c.Memory.ObservationRetentionDays = int.Parse(v)),
        ["memory.autoIntakeOnFirstSession"] = (c => c.Memory.AutoIntakeOnFirstSession.ToString(), (c, v) => c.Memory.AutoIntakeOnFirstSession = bool.Parse(v)),
        ["memory.crossRepoRecallEnabled"] = (c => c.Memory.CrossRepoRecallEnabled.ToString(), (c, v) => c.Memory.CrossRepoRecallEnabled = bool.Parse(v)),
        ["memory.stalenessWarningDays"] = (c => c.Memory.StalenessWarningDays.ToString(), (c, v) => c.Memory.StalenessWarningDays = int.Parse(v)),
        ["memory.recallCacheEnabled"] = (c => c.Memory.RecallCacheEnabled.ToString(), (c, v) => c.Memory.RecallCacheEnabled = bool.Parse(v)),

        // Maintenance
        ["maintenance.intervalHours"] = (c => c.Maintenance.IntervalHours.ToString(), (c, v) => c.Maintenance.IntervalHours = int.Parse(v)),
        ["maintenance.consolidationIntervalHours"] = (c => c.Maintenance.ConsolidationIntervalHours.ToString(), (c, v) => c.Maintenance.ConsolidationIntervalHours = int.Parse(v)),

        // Enrichment
        ["enrichment.ollamaEnabled"] = (c => c.Enrichment.OllamaEnabled.ToString(), (c, v) => c.Enrichment.OllamaEnabled = bool.Parse(v)),
        ["enrichment.ollamaUrl"] = (c => c.Enrichment.OllamaUrl, (c, v) => c.Enrichment.OllamaUrl = v),
        ["enrichment.ollamaModel"] = (c => c.Enrichment.OllamaModel, (c, v) => c.Enrichment.OllamaModel = v),
        ["enrichment.autoOneLiner"] = (c => c.Enrichment.AutoOneLiner.ToString(), (c, v) => c.Enrichment.AutoOneLiner = bool.Parse(v)),
        ["enrichment.autoForesight"] = (c => c.Enrichment.AutoForesight.ToString(), (c, v) => c.Enrichment.AutoForesight = bool.Parse(v)),
        ["enrichment.autoConsolidation"] = (c => c.Enrichment.AutoConsolidation.ToString(), (c, v) => c.Enrichment.AutoConsolidation = bool.Parse(v)),

        // Auth
        ["auth.enabled"] = (c => c.Auth.Enabled.ToString(), (c, v) => c.Auth.Enabled = bool.Parse(v)),
        ["auth.requireForNonLocalhost"] = (c => c.Auth.RequireForNonLocalhost.ToString(), (c, v) => c.Auth.RequireForNonLocalhost = bool.Parse(v)),
    };

    public static string? GetValue(EidetConfig config, string key)
    {
        return Map.TryGetValue(key, out var entry) ? entry.Get(config) : null;
    }

    public static bool SetValue(EidetConfig config, string key, string value)
    {
        if (!Map.TryGetValue(key, out var entry))
            return false;

        entry.Set(config, value);
        return true;
    }

    public static List<(string Key, string Value)> GetAllValues(EidetConfig config)
    {
        return Map.Select(kvp => (kvp.Key, kvp.Value.Get(config))).ToList();
    }
}
