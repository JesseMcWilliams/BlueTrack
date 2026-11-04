using BlueTrack.Api.AdDiscovery;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-333: the shortest chain of nested groups from a Safe's AD group to each user.</summary>
public class NestedGroupPathsTests
{
    // A tiny directory: group name -> (its user members, its nested groups).
    private static readonly Dictionary<string, (string[] Users, string[] Groups)> Tree = new()
    {
        ["Root"] = (["direct"], ["Team A", "Team B"]),
        ["Team A"] = (["alice"], ["Sub"]),
        ["Team B"] = (["bob", "alice"], ["Root"]),   // alice again, and a loop back to Root
        ["Sub"] = (["carol", "bob"], ["Team A"])     // bob again, deeper; a loop back to Team A
    };

    private static (List<string>, List<(string, string, string)>) Direct(string group) =>
        (Tree[group].Users.ToList(), Tree[group].Groups.Select(g => (g, g, g)).ToList());

    [Fact]
    public void EachUserGetsTheShortestPath_DirectMembersAnEmptyOne_AndLoopsEnd()
    {
        var paths = NestedGroupPaths.Shortest("Root", "Root", Direct);

        Assert.Empty(paths["direct"]);
        Assert.Equal(["Team A"], paths["alice"]);   // same length through Team B: the first found wins
        Assert.Equal(["Team B"], paths["bob"]);     // not Team A > Sub
        Assert.Equal(["Team A", "Sub"], paths["carol"]);
        Assert.Equal(4, paths.Count);
    }

    [Fact]
    public void StopsAddingGroups_AtTheLimit()
    {
        var paths = NestedGroupPaths.Shortest("Root", "Root", Direct, maxGroups: 2);

        // Root and Team A are walked; Team B and Sub aren't reached.
        Assert.Equal(["Team A"], paths["alice"]);
        Assert.False(paths.ContainsKey("bob"));
        Assert.False(paths.ContainsKey("carol"));
    }
}
