using System.Security.Cryptography;
using System.Text;
using BlueTrack.Api.Ldap;

namespace BlueTrack.Api.Auth;

/// <summary>D-303: where a resolution took the user's groups from; Reload Rights tells the user which.</summary>
public static class GroupSources
{
    /// <summary>A Windows user's own-domain groups were read from AD just now.</summary>
    public const string Directory = "Directory";
    /// <summary>AD was set up but couldn't be read; the Windows sign-in's groups were used.</summary>
    public const string DirectoryUnavailable = "DirectoryUnavailable";
    /// <summary>The Windows sign-in's groups: no LDAP domain is set up for the user, or no Reload Rights asked for AD.</summary>
    public const string WindowsSignIn = "WindowsSignIn";
    /// <summary>The OIDC or SAML sign-in's group claims (or DevFakeAuth's); they change only at the next sign-in.</summary>
    public const string SignIn = "SignIn";
}

/// <summary>
/// D-303 (D-13's live query): a Windows user's groups, from AD after a Reload Rights. Reload Rights (self-service,
/// or an admin's for another user) reads the user's tokenGroups through the LDAP domains on Admin &gt; Credentials
/// &amp; LDAP. Rights are cached for at most 15 minutes and then re-resolved from the sign-in token, whose groups
/// stay as they were at the Windows sign-in, so after a Reload Rights each re-resolve reads AD again, until the
/// token's groups change (a new Windows sign-in, whose token is current) or 7 days pass. A user who never pressed
/// Reload Rights is resolved from the token, with no directory call.
/// </summary>
public sealed class DirectoryGroups(IDirectoryGroupReader reader, UserRightsCache rightsCache, ILogger<DirectoryGroups> logger)
{
    /// <param name="reload">A Reload Rights: always read AD. Otherwise only when a Reload Rights asked for it.</param>
    public async Task<(IReadOnlyList<string> GroupIdentifiers, string GroupSource)> ResolveAsync(
        int providerKey, string externalIdentifier, string userSid, IReadOnlyList<string> tokenSids, bool reload)
    {
        var fingerprint = Fingerprint(tokenSids);
        if (!reload)
        {
            var marked = await rightsCache.GetDirectoryGroupsTokenAsync(providerKey, externalIdentifier);
            if (marked is null) return (tokenSids, GroupSources.WindowsSignIn);
            // "" is an admin's Reload Rights: whatever token the user's next request brings.
            if (marked.Length > 0 && marked != fingerprint)
            {
                await rightsCache.ClearDirectoryGroupsTokenAsync(providerKey, externalIdentifier);
                return (tokenSids, GroupSources.WindowsSignIn);
            }
        }

        IReadOnlyList<string>? directorySids;
        try
        {
            directorySids = await reader.ReadGroupSidsAsync(userSid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't read {UserSid}'s groups from AD; using the Windows sign-in's groups.", userSid);
            return (tokenSids, GroupSources.DirectoryUnavailable);
        }
        if (directorySids is null)
        {
            await rightsCache.ClearDirectoryGroupsTokenAsync(providerKey, externalIdentifier);
            return (tokenSids, GroupSources.WindowsSignIn);
        }
        await rightsCache.SetDirectoryGroupsTokenAsync(providerKey, externalIdentifier, fingerprint);
        return (GroupIdentifierExtractor.MergeDirectoryGroups(tokenSids, userSid, directorySids), GroupSources.Directory);
    }

    private static string Fingerprint(IReadOnlyList<string> tokenSids) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', tokenSids.Order(StringComparer.OrdinalIgnoreCase)))));
}
