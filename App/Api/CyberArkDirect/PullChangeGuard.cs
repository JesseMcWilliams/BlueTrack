using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-248: the check that stops a pull from replacing good data with an incomplete picture. PVWA only returns what
/// the service user can see, so a removed Safe membership or a wrong credential shows up as Safes and accounts that
/// vanish, and the nightly Load would soft-delete them. A pull whose Safe or account count moves further from the
/// last good pull than the admin's percentage is rejected, unless an admin accepts it. Discovered (pending)
/// accounts aren't counted: they're a separate pool that can be cleared in bulk.
/// </summary>
public static class PullChangeGuard
{
    /// <summary>A change this small is always allowed, so a Vault with a few Safes isn't blocked by one new Safe.</summary>
    public const int AlwaysAllowed = 5;

    /// <summary>Null when the counts are acceptable; otherwise why the pull is rejected. With no previous pull there is nothing to compare, so it is accepted.</summary>
    public static string? Evaluate(PullCounts? previous, PullCounts current, int maxPercent)
    {
        if (previous is null) return null;
        var reasons = new List<string>();
        Check("Safes", previous.SafeCount, current.SafeCount);
        Check("accounts", previous.AccountCount, current.AccountCount);
        return reasons.Count == 0 ? null
            : $"{string.Join("; ", reasons)}. Check the service user's Safe memberships, then accept the change from Admin > Data Sources, or raise the allowed change.";

        void Check(string what, int before, int after)
        {
            var allowed = Math.Max(AlwaysAllowed, (int)Math.Floor(before * maxPercent / 100.0));
            var change = after - before;
            if (Math.Abs(change) > allowed)
            {
                reasons.Add($"{what} went from {ServerTextCulture.Count(before)} to {ServerTextCulture.Count(after)} ({(change > 0 ? "+" : "-")}{ServerTextCulture.Count(Math.Abs(change))}; at most {ServerTextCulture.Count(allowed)} allowed at {maxPercent}%)");
            }
        }
    }
}
