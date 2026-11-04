namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// D-333: the shortest chain of nested groups from a group to each of its users, found level by level (breadth
/// first), so "Via AD group" can name the groups in between. Kept apart from the LDAP code so it can be tested
/// without a directory. A group seen once isn't walked again (AD allows membership loops), and the walk stops
/// adding groups after <see cref="MaxGroups"/>, so a huge tree can't stall the nightly pull.
/// </summary>
public static class NestedGroupPaths
{
    public const string Separator = " > ";
    public const int MaxGroups = 2000;

    /// <summary>
    /// For each user key reached, the names of the nested groups between <paramref name="root"/> and the user,
    /// outermost first; an empty list for a direct member. With several paths of the same length, the first found
    /// (in the order <paramref name="directMembers"/> lists them) wins.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> Shortest<TGroup>(
        TGroup root,
        string rootKey,
        Func<TGroup, (List<string> Users, List<(string Key, string Name, TGroup Group)> Groups)> directMembers,
        int maxGroups = MaxGroups)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootKey };
        var queue = new Queue<(TGroup Group, IReadOnlyList<string> Path)>();
        queue.Enqueue((root, []));
        while (queue.Count > 0)
        {
            var (group, path) = queue.Dequeue();
            var (users, groups) = directMembers(group);
            foreach (var user in users) result.TryAdd(user, path);
            foreach (var (key, name, nested) in groups)
            {
                if (seen.Count >= maxGroups) break;
                if (seen.Add(key)) queue.Enqueue((nested, [.. path, name]));
            }
        }
        return result;
    }
}
