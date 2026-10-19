namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-250: which Safes a pull no longer returns, and which are new, compared with the last good pull's staged Safes
/// (stg_sh_safes still holds them: a rejected pull never touches staging). It goes into the rejection message so the
/// admin deciding whether to accept a change sees the Safe names, not only the counts.
/// </summary>
public static class SafeDifference
{
    public const int MaxNames = 20;

    /// <summary>The lines to add to a rejection message; empty when the two lists have the same Safes.</summary>
    public static IReadOnlyList<string> Describe(IEnumerable<(long Number, string Name)> previous, IEnumerable<(long Number, string Name)> current)
    {
        var before = previous.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First().Name);
        var after = current.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First().Name);
        var lines = new List<string>();
        Add("Safes no longer returned", before.Where(s => !after.ContainsKey(s.Key)).Select(s => s.Value));
        Add("Safes new since the last good pull", after.Where(s => !before.ContainsKey(s.Key)).Select(s => s.Value));
        return lines;

        void Add(string label, IEnumerable<string> names)
        {
            var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (sorted.Count == 0) return;
            var shown = string.Join(", ", sorted.Take(MaxNames));
            lines.Add($"{label} ({sorted.Count}): {shown}{(sorted.Count > MaxNames ? $" and {sorted.Count - MaxNames} more" : "")}");
        }
    }
}
