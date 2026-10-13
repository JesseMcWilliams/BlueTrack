using System.Security.AccessControl;
using System.Security.Principal;

namespace BlueTrack.Api.Backups;

/// <summary>
/// D-203: a backup is a full copy of the database, including credentials that
/// any local process can decrypt while they're Machine-scope DPAPI (D-116), so
/// broad groups must not be able to read the backup folder or its files.
/// Found 2026-10-12: a folder created under C:\ inherited "Users: Read".
///
/// Before every backup (scheduled, and the Deployment page's), the folder and
/// the backup files already in it are checked. Where a broad group can read
/// one, inheritance is turned off (keeping the other entries) and the broad
/// groups' entries are removed. If the App Pool's account isn't allowed to
/// change the permissions, the backup is refused with the reason (chosen by
/// the user 2026-10-12: fix it, else refuse). The installer and
/// Backup-BlueTrack.ps1 set the folder the same way (BlueTrack.Rollback.psm1).
/// </summary>
public static class BackupFolderAccess
{
    public sealed record Outcome(bool MayBackUp, string? Note);

    private static readonly SecurityIdentifier[] BroadGroups =
    [
        new(WellKnownSidType.WorldSid, null),              // Everyone
        new(WellKnownSidType.BuiltinUsersSid, null),       // BUILTIN\Users
        new(WellKnownSidType.AuthenticatedUserSid, null),  // Authenticated Users
        new(WellKnownSidType.BuiltinGuestsSid, null),      // BUILTIN\Guests
        new(WellKnownSidType.InteractiveSid, null),        // INTERACTIVE
        new(WellKnownSidType.AnonymousSid, null),          // ANONYMOUS LOGON
    ];

    private const int ReadDataOrGeneric = (int)FileSystemRights.ReadData | unchecked((int)0x80000000) /* GENERIC_READ */ | 0x10000000 /* GENERIC_ALL */;

    /// <summary>A broad group, including any domain's Domain Users (RID 513) and Domain Guests (514).</summary>
    public static bool IsBroadGroup(SecurityIdentifier sid)
    {
        if (BroadGroups.Contains(sid)) return true;
        var value = sid.Value;
        return value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && (value.EndsWith("-513", StringComparison.Ordinal) || value.EndsWith("-514", StringComparison.Ordinal));
    }

    /// <summary>The broad groups that this ACL lets read, as account names (SIDs where a name can't be found).</summary>
    public static IReadOnlyList<string> BroadReaders(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow
                        && ((int)r.FileSystemRights & ReadDataOrGeneric) != 0
                        && r.IdentityReference is SecurityIdentifier sid && IsBroadGroup(sid))
            .Select(r => Name((SecurityIdentifier)r.IdentityReference))
            .Distinct()
            .ToList();

    /// <param name="readFolderSecurity">How the folder's permissions are read; tests replace it to
    /// act as an account that can't read them.</param>
    public static Outcome Enforce(string folder, Func<DirectoryInfo, DirectorySecurity>? readFolderSecurity = null)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
        {
            // SQL Server on another machine: this server can't see the folder, so can't check it.
            return new Outcome(true, "The backup folder's permissions weren't checked: this server can't see the folder (is SQL Server on another machine?).");
        }

        // Found in CI 2026-10-13: an account that can't read the folder's permissions (SQL Server's
        // own default backup folder, for a non-admin) can't check them either -- back up with a note.
        DirectorySecurity folderSecurity;
        List<FileInfo> files;
        try
        {
            folderSecurity = (readFolderSecurity ?? (d => d.GetAccessControl()))(directory);
            files = directory.EnumerateFiles().Where(f => f.Extension is ".bak" or ".json").ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new Outcome(true, "The backup folder's permissions weren't checked: the App Pool's account can't read them.");
        }

        var fixedItems = new List<string>();
        try
        {
            if (Restrict(folderSecurity, s => directory.SetAccessControl((DirectorySecurity)s), () => directory.GetAccessControl()) is { } folderGroups)
            {
                fixedItems.Add($"the folder ({string.Join(", ", folderGroups)})");
            }
            foreach (var file in files)
            {
                FileSecurity fileSecurity;
                try { fileSecurity = file.GetAccessControl(); }
                catch (UnauthorizedAccessException) { continue; } // can't read it, so can't judge it
                if (Restrict(fileSecurity, s => file.SetAccessControl((FileSecurity)s), () => file.GetAccessControl()) is not null)
                {
                    fixedItems.Add(file.Name);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            IReadOnlyList<string> readers;
            try { readers = BroadReaders(directory.GetAccessControl()); }
            catch (UnauthorizedAccessException) { readers = []; }
            return new Outcome(false,
                $"Backup refused: {string.Join(", ", readers.DefaultIfEmpty("a broad group"))} can read the backup folder or a backup in it, and BlueTrack couldn't remove that access ({ex.Message}). " +
                "Re-run the installer's backup step or Backup-BlueTrack.ps1 as an administrator, or remove that access from the folder yourself.");
        }

        return new Outcome(true, fixedItems.Count == 0 ? null : $"Removed broad groups' read access from {string.Join("; ", fixedItems)}.");
    }

    /// <summary>
    /// Null when nothing needed changing; otherwise turns inheritance off (keeping
    /// the inherited entries as explicit ones), removes the broad groups' entries
    /// and returns which groups they were. Throws if it can't, or if they remain.
    /// </summary>
    private static IReadOnlyList<string>? Restrict(FileSystemSecurity security, Action<FileSystemSecurity> save, Func<FileSystemSecurity> reload)
    {
        var readers = BroadReaders(security);
        if (readers.Count == 0) return null;

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        save(security);

        var explicitRules = reload();
        foreach (var rule in explicitRules.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                     .Where(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference is SecurityIdentifier sid && IsBroadGroup(sid)))
        {
            explicitRules.RemoveAccessRuleSpecific(rule);
        }
        save(explicitRules);

        if (BroadReaders(reload()).Count > 0)
        {
            throw new InvalidOperationException("the broad groups' access was still there after removing it");
        }
        return readers;
    }

    private static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
    }
}
