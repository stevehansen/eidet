using Eidet.Core.Domain;
using Raven.Client.Documents.Indexes;

namespace Eidet.Core.Indexes;

public class Memories_CountByType : AbstractIndexCreationTask<MemoryEntry, Memories_CountByType.Result>
{
    public class Result
    {
        public string RepoId { get; set; } = "";
        public MemoryType Type { get; set; }
        public int Count { get; set; }
    }

    public Memories_CountByType()
    {
        Map = entries => from e in entries
            where e.Validity.ValidUntil == null
            select new Result
            {
                RepoId = e.RepoId,
                Type = e.Type,
                Count = 1,
            };

        Reduce = results => from r in results
            group r by new { r.RepoId, r.Type }
            into g
            select new Result
            {
                RepoId = g.Key.RepoId,
                Type = g.Key.Type,
                Count = g.Sum(x => x.Count),
            };
    }
}
