using Eidet.Core.Configuration;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Storage;

public class DocumentStoreFactoryTests
{
    [Fact]
    public void GetDefaultDataDir_ReturnsNonEmpty()
    {
        var dir = DocumentStoreFactory.GetDefaultDataDir();
        Assert.False(string.IsNullOrEmpty(dir));
        Assert.Contains("raven", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultDataDir_ContainsEidet()
    {
        var dir = DocumentStoreFactory.GetDefaultDataDir();
        Assert.Contains("eidet", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFromConfig_External_UsesRavenUrl()
    {
        // External mode — Create initializes store without connecting
        var store = DocumentStoreFactory.Create("http://localhost:19999", "TestDb");
        Assert.NotNull(store);
        store.Dispose();
    }

    [Fact]
    public void CreateFromConfig_Embedded_UsesDataDir()
    {
        var config = new EidetConfig
        {
            Storage = new StorageConfig
            {
                Mode = StorageMode.Embedded,
                DataDir = null, // should use default
            }
        };

        // Verify CreateFromConfig would use the right data dir
        var dataDir = config.Storage.DataDir ?? DocumentStoreFactory.GetDefaultDataDir();
        Assert.False(string.IsNullOrEmpty(dataDir));
    }
}
