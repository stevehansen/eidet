using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Union-find grouping of memory entries by shared tag. Transitive: a≈b and b≈c → {a, b, c}.
/// Case-insensitive tag matching. Empty tag set → own group.
/// </summary>
internal static class TagOverlapGrouper
{
    public static List<List<MemoryEntry>> Group(List<MemoryEntry> entries)
    {
        var n = entries.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        var tagMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            foreach (var tag in entries[i].Tags)
            {
                if (!tagMap.TryGetValue(tag, out var list))
                {
                    list = [];
                    tagMap[tag] = list;
                }
                list.Add(i);
            }
        }

        foreach (var indices in tagMap.Values)
        {
            for (var i = 1; i < indices.Count; i++)
                Union(indices[0], indices[i]);
        }

        var groups = new Dictionary<int, List<MemoryEntry>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups[root] = group;
            }
            group.Add(entries[i]);
        }

        return groups.Values.ToList();
    }
}
