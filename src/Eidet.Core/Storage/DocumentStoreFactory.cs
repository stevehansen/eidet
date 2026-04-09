using Raven.Client.Documents;

namespace Eidet.Core.Storage;

public static class DocumentStoreFactory
{
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
}
