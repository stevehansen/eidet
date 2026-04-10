using Raven.Client.Documents;
using Raven.Embedded;

namespace Eidet.Core.Storage;

public static class DocumentStoreFactory
{
    private static bool _embeddedStarted;
    private static readonly object _lock = new();

    public static IDocumentStore Create(string url, string database)
    {
        var store = new DocumentStore
        {
            Urls = [url],
            Database = database,
        };
        store.Initialize();
        return store;
    }

    public static IDocumentStore CreateEmbedded(string dataDir, string database)
    {
        EnsureEmbeddedStarted(dataDir);
        return EmbeddedServer.Instance.GetDocumentStore(database);
    }

    public static IDocumentStore CreateFromConfig(Configuration.EidetConfig config)
    {
        return config.Storage.Mode == Configuration.StorageMode.Embedded
            ? CreateEmbedded(config.Storage.DataDir ?? GetDefaultDataDir(), config.Storage.DatabaseName)
            : Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
    }

    internal static void EnsureEmbeddedStarted(string dataDir)
    {
        if (_embeddedStarted) return;
        lock (_lock)
        {
            if (_embeddedStarted) return;

            Directory.CreateDirectory(dataDir);

            var options = new ServerOptions
            {
                DataDirectory = dataDir,
            };

            EmbeddedServer.Instance.StartServer(options);
            _embeddedStarted = true;
        }
    }

    public static string GetDefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Eidet", "data", "raven");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".eidet", "data", "raven");
    }
}
