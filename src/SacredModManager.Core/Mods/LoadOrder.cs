namespace SacredModManager.Core.Mods;

/// <summary>One mod as the load order sees it.</summary>
public sealed record LoadOrderEntry(string Id, double Priority, IReadOnlyList<string> Requires);

/// <summary>
/// The order in which Sacred Gear loads mods, worked out the same way Gear does: the mods in the saved list (<c>"order"</c> in
/// <c>gear_mods.json</c>) first, in list order, then the others by priority (lower first) and id; a mod always comes after the
/// mods it requires. Requirements that are not installed are ignored here (Gear skips such a mod).
/// </summary>
public static class LoadOrder
{
    public static List<string> Compute(IEnumerable<LoadOrderEntry> mods, IReadOnlyList<string> saved)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < saved.Count; i++)
        {
            rank.TryAdd(saved[i], i);
        }

        var sorted = mods.GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        sorted.Sort((a, b) =>
        {
            bool ra = rank.TryGetValue(a.Id, out int ia);
            bool rb = rank.TryGetValue(b.Id, out int ib);
            if (ra && rb)
            {
                return ia.CompareTo(ib);
            }

            if (ra || rb)
            {
                return ra ? -1 : 1;
            }

            int p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : string.CompareOrdinal(a.Id, b.Id);
        });

        Dictionary<string, LoadOrderEntry> byId = sorted.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var result = new List<string>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false: being visited, true: done

        void Visit(LoadOrderEntry m)
        {
            if (state.ContainsKey(m.Id))
            {
                return; // done, or a cycle (Gear skips those mods; here the mod simply stays where it is)
            }

            state[m.Id] = false;
            foreach (string dep in m.Requires)
            {
                if (byId.TryGetValue(dep, out LoadOrderEntry? d))
                {
                    Visit(d);
                }
            }

            state[m.Id] = true;
            result.Add(m.Id);
        }

        foreach (LoadOrderEntry m in sorted)
        {
            Visit(m);
        }

        return result;
    }

    /// <summary>
    /// The list with <paramref name="id"/> moved by <paramref name="delta"/> places (negative: earlier); null when
    /// the mod is not in the list or would leave it.
    /// </summary>
    public static List<string>? Move(IReadOnlyList<string> current, string id, int delta)
    {
        int at = current.ToList().IndexOf(id);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= current.Count)
        {
            return null;
        }

        var list = current.ToList();
        list.RemoveAt(at);
        list.Insert(to, id);
        return list;
    }
}
