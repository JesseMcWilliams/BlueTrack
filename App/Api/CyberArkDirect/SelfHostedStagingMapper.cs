using System.Data;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-248: turns what a PVWA pull read into rows for the stg_sh_* tables, in the shape the EVD import produces, so
/// the Load is unchanged. Every mapping below was checked against the lab on 2026-10-19 by comparing the REST data
/// with the EVD database for the same Vault: account ids, user ids and group ids are the EVD's own numbers, and
/// the 88 Safe members compared agreed with the permission mapping.
/// </summary>
public sealed class SelfHostedStagingMapper
{
    /// <summary>The EVD's CAUUserTypeID for each REST userType. Seen in the lab; a type not listed stays NULL.</summary>
    private static readonly Dictionary<string, int> UserTypeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Built-InAdmins"] = 10, ["ENE"] = 11, ["CPM"] = 31, ["PVWA"] = 32, ["AppProvider"] = 33, ["EPVUser"] = 34,
        ["AIMAccount"] = 35, ["PSM"] = 36, ["DRUser"] = 504
    };

    /// <summary>REST permission name to the EVD's CAOwners column. Permissions with no REST counterpart (CAOValidateSafeContent, CAOEventsList, CAOEventsAdd) stay NULL.</summary>
    internal static readonly (string Permission, string Column)[] PermissionColumns =
    [
        ("listAccounts", "CAOList"), ("retrieveAccounts", "CAORetrieve"), ("addAccounts", "CAOCreateObject"),
        ("updateAccountContent", "CAOUpdateObject"), ("updateAccountProperties", "CAOUpdateObjectProperties"),
        ("renameAccounts", "CAORenameObject"), ("deleteAccounts", "CAODelete"), ("viewAuditLog", "CAOViewAudit"),
        ("viewSafeMembers", "CAOViewOwners"), ("useAccounts", "CAOUsePassword"),
        ("initiateCPMAccountManagementOperations", "CAOInitiateCPMChange"),
        ("specifyNextAccountContent", "CAOInitiateCPMChangeWithManualPassword"),
        ("createFolders", "CAOCreateFolder"), ("deleteFolders", "CAODeleteFolder"), ("unlockAccounts", "CAOUnlockObject"),
        ("moveAccountsAndFolders", "CAOMoveFrom"), ("moveAccountsAndFolders", "CAOMoveInto"),
        ("manageSafe", "CAOManageSafe"), ("manageSafeMembers", "CAOManageSafeOwners"), ("backupSafe", "CAOBackup"),
        ("accessWithoutConfirmation", "CAONoConfirmRequired"), ("requestsAuthorizationLevel1", "CAOConfirm")
    ];

    public StagingTables Map(PvwaSnapshot snapshot, Guid batchId, string sourceName)
    {
        var tables = new StagingTables();
        var source = Cut(sourceName, 260)!;

        foreach (var u in snapshot.Users)
        {
            var (locId, locName) = Location(u.Location);
            tables.Users.Rows.Add(batchId, source, u.Id, Cut(u.UserName, 128), Db(locId), Db(locName), Db(Cut(u.FirstName, 30)), Db(Cut(u.LastName, 30)),
                Db(Cut(u.Email, 50)), u.Enabled ? "NO" : "YES",
                Db(u.Source is null ? null : string.Equals(u.Source, "CyberArk", StringComparison.OrdinalIgnoreCase) ? 1 : 2),
                Db(u.UserType is not null && UserTypeIds.TryGetValue(u.UserType, out var typeId) ? typeId : null));
        }

        // Users and groups share one id space in the Vault, and a group's member list names a nested group the same way
        // as a user, so a member whose id is a group's is a group (the EVD flags it MemberIsGroup = YES; checked in the lab).
        var groupIds = snapshot.Groups.Select(g => g.Id).ToHashSet();
        foreach (var g in snapshot.Groups)
        {
            var (locId, locName) = Location(g.Location);
            tables.Groups.Rows.Add(batchId, source, g.Id, Cut(g.Name, 128), Db(locId), Db(locName), Db(Cut(g.Description, 100)),
                Db(g.GroupType is null ? null : string.Equals(g.GroupType, "Vault", StringComparison.OrdinalIgnoreCase) ? 1 : 2));
            foreach (var memberId in g.MemberIds) tables.GroupMembers.Rows.Add(batchId, source, g.Id, memberId, groupIds.Contains(memberId) ? "YES" : "NO");
        }

        foreach (var s in snapshot.Safes)
        {
            var (locId, locName) = Location(s.Location);
            tables.Safes.Rows.Add(batchId, source, s.SafeNumber, Cut(s.SafeName, 28), Db(locId), Db(locName), Db(Cut(s.CreatorName, 513)),
                Db(s.CreationTime), Db(s.VersionsRetention), Db(s.DaysRetention));
        }

        // Predefined members are read for stg_shd_* only (D-270): the EVD-shaped owners keep what the pull always staged.
        foreach (var m in snapshot.Members.Where(m => m.IsPredefinedUser != true))
        {
            var row = tables.Owners.NewRow();
            row["ImportBatchId"] = batchId;
            row["SourceFileName"] = source;
            row["CAOSafeID"] = m.SafeNumber;
            row["CAOSafeName"] = Cut(m.SafeName, 28);
            row["CAOOwnerID"] = m.MemberId;
            row["CAOOwnerName"] = Cut(m.MemberName, 128);
            row["CAOOwnerType"] = m.IsGroup ? 1 : 0;
            row["CAOExpirationDate"] = Db(m.ExpirationDate);
            foreach (var (permission, column) in PermissionColumns)
            {
                row[column] = m.Permissions.TryGetValue(permission, out var granted) ? (object)(granted ? "YES" : "NO") : DBNull.Value;
            }
            tables.Owners.Rows.Add(row);
        }

        var longNames = 0;
        foreach (var a in snapshot.Accounts)
        {
            tables.Files.Rows.Add(batchId, source, a.SafeNumber, Cut(a.SafeName, 28), "Root", a.FileId, Cut(a.Name, 170), 2,
                Db(a.CreatedTime), Db(a.LastModifiedTime), Db(a.LastVerifiedTime));

            void AddProperty(string name, string? value)
            {
                if (value is null) return;
                if (name.Length > 29) { longNames++; return; }
                tables.ObjectProperties.Rows.Add(batchId, source, a.SafeNumber, (int)a.FileId, name, Cut(value, 4000));
            }
            AddProperty("UserName", a.UserName);
            AddProperty("Address", a.Address);
            if (!string.IsNullOrWhiteSpace(a.PlatformId)) AddProperty("PolicyID", a.PlatformId);
            // CPMDisabled holds the reason automatic management is off (D-244); REST says it as a flag and a reason.
            if (a.AutoManaged == false) AddProperty("CPMDisabled", string.IsNullOrWhiteSpace(a.ManualReason) ? "No reason given" : a.ManualReason);
            foreach (var (name, value) in a.Properties)
            {
                if (name is "UserName" or "Address" or "PolicyID" or "CPMDisabled") continue;
                AddProperty(name, value);
            }
        }
        if (longNames > 0) tables.Warnings.Add($"{ServerTextCulture.Count(longNames)} account propert{(longNames == 1 ? "y" : "ies")} with a name over 29 characters {(longNames == 1 ? "was" : "were")} not staged (the staging column holds 29).");

        foreach (var p in snapshot.Platforms)
        {
            tables.Platforms.Rows.Add(batchId, source, Cut(p.Id, 100), Db(Cut(p.Name, 200)), Db(Cut(p.Description, 1000)), Db(p.Active), Db(p.PlatformType?.Trim().ToLowerInvariant()));
        }

        MapDirect(snapshot, tables, batchId, source);
        return tables;
    }

    /// <summary>
    /// D-270: the same pull in REST's own shape, for the stg_shd_* tables (41_BlueTrack_SelfHostedDirectStaging.sql):
    /// every field REST returned, cut only to the column widths. Nothing reads them yet.
    /// </summary>
    private static void MapDirect(PvwaSnapshot snapshot, StagingTables tables, Guid batchId, string source)
    {
        foreach (var p in snapshot.Platforms)
        {
            tables.DirectPlatforms.Rows.Add(batchId, source, Cut(p.Id, 100), Db(Cut(p.Name, 200)), Db(Cut(p.SystemType, 100)), Db(Cut(p.PlatformType, 50)),
                Db(Cut(p.PlatformBaseId, 100)), Db(p.Active), Db(Cut(p.Description, 1000)), Db(Cut(p.AllowedSafes, 1000)),
                Db(p.PerformPeriodicVerification), Db(p.VerifyEveryXDays), Db(p.AllowManualVerification),
                Db(p.PerformPeriodicChange), Db(p.ChangeEveryXDays), Db(p.AllowManualChange),
                Db(p.AutomaticReconcileWhenUnsynched), Db(p.AllowManualReconciliation), Db(p.RequirePsm), Db(p.RecordSessions), Db(Cut(p.PsmServerId, 200)),
                Db(p.RequireDualControl), Db(p.EnforceExclusiveAccess), Db(p.EnforceOneTimePassword),
                Db(p.RequireReasonForAccess), Db(p.VerifyOnAdd), Db(p.ChangeOnAdd), Db(p.ChangePasswordInResetMode));
            foreach (var (kind, name, displayName) in p.Properties)
            {
                tables.DirectPlatformProperties.Rows.Add(batchId, source, Cut(p.Id, 100), kind, Cut(name, 200), Db(Cut(displayName, 200)));
            }
        }

        foreach (var s in snapshot.Safes)
        {
            tables.DirectSafes.Rows.Add(batchId, source, s.SafeNumber, Cut(s.SafeUrlId, 300), Cut(s.SafeName, 300), Db(Cut(s.Description, 1000)), Db(Cut(s.Location, 400)),
                Db(Cut(s.CreatorId, 100)), Db(Cut(s.CreatorName, 513)), Db(s.OlacEnabled), Db(Cut(s.ManagingCpm, 200)), Db(s.VersionsRetention), Db(s.DaysRetention),
                Db(s.AutoPurgeEnabled), Db(s.CreationTime), Db(s.LastModificationTime), Db(s.IsExpiredMember));
        }

        foreach (var a in snapshot.Accounts)
        {
            tables.DirectAccounts.Rows.Add(batchId, source, a.Id, a.SafeNumber, a.FileId, Db(Cut(a.SafeName, 300)), Db(Cut(a.Name, 512)), Db(Cut(a.Address, 512)),
                Db(Cut(a.UserName, 512)), Db(Cut(a.PlatformId, 100)), Db(Cut(a.SecretType, 50)), Db(a.CreatedTime), Db(a.CategoryModificationTime),
                Db(a.AutoManaged), Db(Cut(a.ManualReason, 1000)), Db(Cut(a.CpmStatus, 50)), Db(a.LastModifiedTime), Db(a.LastReconciledTime), Db(a.LastVerifiedTime),
                Db(Cut(a.RemoteMachines, 4000)), Db(a.AccessRestrictedToRemoteMachines));
            foreach (var (name, value) in a.Properties)
            {
                tables.DirectAccountProperties.Rows.Add(batchId, source, a.Id, Cut(name, 256), Db(Cut(value, 4000)));
            }
            foreach (var c in a.CpmActivity)
            {
                tables.DirectCpmActivity.Rows.Add(batchId, source, a.Id, c.Code, Db(Cut(c.Activity, 200)), Db(c.Time), Db(Cut(c.UserName, 256)),
                    Db(Cut(c.Reason, 1000)), Db(Cut(c.MoreInfo, 1000)));
            }
            // D-280: read by this pull or kept from an earlier one, so staging has the details of every account known.
            if (a.Detail is { } d)
            {
                tables.DirectAccountDetails.Rows.Add(batchId, source, a.Id, Db(d.CategoryModificationTime), Db(Cut(d.DeviceType, 100)), Db(Cut(d.CreationMethod, 100)),
                    Db(Cut(d.LogonSafe, 300)), Db(Cut(d.LogonFolder, 300)), Db(Cut(d.LogonName, 512)),
                    Db(Cut(d.ReconcileSafe, 300)), Db(Cut(d.ReconcileFolder, 300)), Db(Cut(d.ReconcileName, 512)), a.DetailReadThisPull);
            }
        }

        foreach (var m in snapshot.Members)
        {
            var row = tables.DirectSafeMembers.NewRow();
            row["ImportBatchId"] = batchId;
            row["SourceFileName"] = source;
            row["SafeUrlId"] = Cut(m.SafeUrlId ?? m.SafeName, 300);
            row["SafeName"] = Cut(m.SafeName, 300);
            row["SafeNumber"] = m.SafeNumber;
            row["MemberId"] = m.MemberId;
            row["MemberName"] = Cut(m.MemberName, 300);
            row["MemberType"] = Db(Cut(m.MemberType, 20));
            row["MembershipExpirationDate"] = Db(m.ExpirationDate);
            row["IsExpiredMembershipEnable"] = Db(m.IsExpiredMembershipEnable);
            row["IsPredefinedUser"] = Db(m.IsPredefinedUser);
            row["IsReadOnly"] = Db(m.IsReadOnly);
            // Column names are the REST names in PascalCase, as in stg_pc_entitlements; a permission PVWA didn't send stays NULL.
            var permissions = m.Permissions.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var permission in DirectPermissionColumns)
            {
                row[permission] = permissions.TryGetValue(permission, out var granted) ? granted : DBNull.Value;
            }
            tables.DirectSafeMembers.Rows.Add(row);
        }

        foreach (var u in snapshot.Users)
        {
            tables.DirectUsers.Rows.Add(batchId, source, u.Id, Cut(u.UserName, 256), Db(Cut(u.Source, 50)), Db(Cut(u.UserType, 100)), Db(u.ComponentUser),
                Db(Cut(u.Location, 400)), u.Enabled, Db(u.Suspended), Db(Cut(u.FirstName, 200)), Db(Cut(u.MiddleName, 200)), Db(Cut(u.LastName, 200)),
                u.DetailsRead, Db(Cut(u.Organization, 200)), Db(Cut(u.Department, 200)), Db(Cut(u.Title, 200)), Db(Cut(u.Profession, 200)),
                Db(Cut(u.Email, 320)), Db(Cut(u.HomeEmail, 320)), Db(Cut(u.OtherEmail, 320)), Db(Cut(u.HomePage, 400)),
                Db(Cut(u.UserDn, 1000)), Db(Cut(u.DistinguishedName, 1000)), Db(Cut(u.Description, 400)),
                Db(u.ExpiryDate), Db(u.LastSuccessfulLoginDate), Db(u.PasswordNeverExpires), Db(u.ChangePassOnNextLogon));
            foreach (var (kind, value) in u.Authorizations)
            {
                tables.DirectUserAuthorizations.Rows.Add(batchId, source, u.Id, kind, Cut(value, 200));
            }
        }

        // D-281: the Safe-member groups' AD users.
        if (snapshot.AdGroups is { } ad) MapAdGroups(ad, tables, batchId, source);

        // D-282: only when the list was read, so a refused list leaves the Load's applications as they are.
        foreach (var app in snapshot.ApplicationsRead ? snapshot.Applications : [])
        {
            tables.DirectApplications.Rows.Add(batchId, source, Cut(app.AppId, 256), Db(Cut(app.Description, 1000)), Db(Cut(app.Location, 400)), Db(app.Disabled),
                Db(Cut(app.ExpirationDate, 100)), Db(app.AccessPermittedFrom), Db(app.AccessPermittedTo), Db(app.AllowExtendedAuthenticationRestrictions),
                Db(Cut(app.BusinessOwnerFirstName, 200)), Db(Cut(app.BusinessOwnerLastName, 200)), Db(Cut(app.BusinessOwnerEmail, 320)),
                Db(Cut(app.BusinessOwnerPhone, 100)), app.AuthenticationsRead);
            foreach (var a in app.Authentications)
            {
                tables.DirectApplicationAuthentications.Rows.Add(batchId, source, Cut(app.AppId, 256), Db(Cut(a.AuthId, 100)), Cut(a.AuthType, 100),
                    Db(Cut(a.AuthValue, 1000)), Db(Cut(a.Comment, 1000)), Db(a.IsFolder), Db(a.AllowInternalScripts),
                    Db(Cut(a.Subject, 1000)), Db(Cut(a.Issuer, 1000)), Db(Cut(a.SubjectAlternativeName, 1000)));
            }
        }

        foreach (var g in snapshot.Groups)
        {
            tables.DirectGroups.Rows.Add(batchId, source, g.Id, Cut(g.Name, 256), Db(Cut(g.GroupType, 50)), Db(Cut(g.Location, 400)), Db(Cut(g.Description, 1000)),
                Db(Cut(g.Directory, 256)), Db(Cut(g.Dn, 1000)));
            foreach (var memberId in g.MemberIds)
            {
                tables.DirectGroupMembers.Rows.Add(batchId, source, g.Id, memberId, Db(Cut(g.MemberNames.GetValueOrDefault(memberId), 256)));
            }
        }
    }

    /// <summary>D-281: the AD reads into stg_shd_ad_group_reads and stg_shd_ad_group_members (a pull's, or the standalone read's).</summary>
    public static void MapAdGroups(AdGroupExpansion ad, StagingTables tables, Guid batchId, string source)
    {
        foreach (var r in ad.Reads)
        {
            tables.DirectAdGroupReads.Rows.Add(batchId, source, r.Group.Source, Cut(r.Group.Ref, 256), Db(Cut(r.Group.Name, 256)), Db(Cut(r.Group.Dn, 1000)),
                Db(Cut(r.DomainName, 256)), r.Status, Db(r.MemberCount), Db(Cut(r.Message, 1000)));
        }
        foreach (var m in ad.Members)
        {
            tables.DirectAdGroupMembers.Rows.Add(batchId, source, m.Group.Source, Cut(m.Group.Ref, 256), Cut(m.DomainName, 256), Cut(m.User.SamAccountName, 256),
                Db(Cut(m.User.DistinguishedName, 1000)), Db(Cut(m.User.ObjectSid, 200)), Db(Cut(m.User.DisplayName, 400)), m.User.IsEnabled);
        }
    }

    /// <summary>The 22 REST Safe member permissions; stg_shd_safe_members has a BIT column for each, in PascalCase.</summary>
    public static readonly string[] DirectPermissionColumns =
    [
        "UseAccounts", "RetrieveAccounts", "ListAccounts", "AddAccounts", "UpdateAccountContent", "UpdateAccountProperties",
        "InitiateCPMAccountManagementOperations", "SpecifyNextAccountContent", "RenameAccounts", "DeleteAccounts", "UnlockAccounts",
        "ManageSafe", "ManageSafeMembers", "BackupSafe", "ViewAuditLog", "ViewSafeMembers", "AccessWithoutConfirmation",
        "CreateFolders", "DeleteFolders", "MoveAccountsAndFolders", "RequestsAuthorizationLevel1", "RequestsAuthorizationLevel2"
    ];

    /// <summary>PVWA names a location by path and has no id. D-277: the Load keys Self-Hosted locations by path, so no id is staged.</summary>
    private static (long? Id, string? Name) Location(string? path) =>
        string.IsNullOrWhiteSpace(path) ? (null, null) : (null, Cut(path.Trim(), 128));

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>One DataTable per staging table, with the columns Self-Hosted Direct fills; the rest stay NULL or take their defaults.</summary>
public sealed class StagingTables
{
    public DataTable Users { get; } = Table("stg_sh_users", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("CAUUserID", typeof(long)),
        ("CAUUserName", typeof(string)), ("CAULocationID", typeof(long)), ("CAULocationName", typeof(string)), ("CAUFirstName", typeof(string)),
        ("CAULastName", typeof(string)), ("CAUBusinessEmail", typeof(string)), ("CAUDisabled", typeof(string)), ("CAUExternalInternal", typeof(int)),
        ("CAUUserTypeID", typeof(int)));

    public DataTable Groups { get; } = Table("stg_sh_groups", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("CAGGroupID", typeof(long)),
        ("CAGGroupName", typeof(string)), ("CAGLocationID", typeof(long)), ("CAGLocationName", typeof(string)), ("CAGDescription", typeof(string)),
        ("CAGExternalInternal", typeof(int)));

    public DataTable GroupMembers { get; } = Table("stg_sh_groupmembers", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("CAGMGroupID", typeof(long)), ("CAGMUserID", typeof(long)), ("CAGMMemberIsGroup", typeof(string)));

    public DataTable Safes { get; } = Table("stg_sh_safes", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("CASSafeID", typeof(long)),
        ("CASSafeName", typeof(string)), ("CASLocationID", typeof(long)), ("CASLocationName", typeof(string)), ("CASCreatedBy", typeof(string)),
        ("CASCreationDate", typeof(DateTime)), ("CASYearlyVersions", typeof(int)), ("CASObjectsRetentionPeriod", typeof(int)));

    public DataTable Owners { get; } = Table("stg_sh_owners",
        [("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("CAOSafeID", typeof(long)), ("CAOSafeName", typeof(string)),
         ("CAOOwnerID", typeof(long)), ("CAOOwnerName", typeof(string)), ("CAOOwnerType", typeof(int)), ("CAOExpirationDate", typeof(DateTime)),
         .. SelfHostedStagingMapper.PermissionColumns.Select(p => p.Column).Distinct().Select(c => (c, typeof(string)))]);

    public DataTable Files { get; } = Table("stg_sh_files", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("CAFSafeID", typeof(long)),
        ("CAFSafeName", typeof(string)), ("CAFFolder", typeof(string)), ("CAFFileID", typeof(long)), ("CAFFileName", typeof(string)), ("CAFType", typeof(int)),
        ("CAFCreationDate", typeof(DateTime)), ("CAFModificationDate", typeof(DateTime)), ("CAFLastUsedDate", typeof(DateTime)));

    public DataTable ObjectProperties { get; } = Table("stg_sh_objectproperties", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("CAOPSafeId", typeof(long)), ("CAOPFileId", typeof(int)), ("CAOPObjectPropertyName", typeof(string)), ("CAOPObjectPropertyValue", typeof(string)));

    public DataTable Platforms { get; } = Table("stg_sh_platforms", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("PlatformID", typeof(string)),
        ("PlatformName", typeof(string)), ("Description", typeof(string)), ("IsActive", typeof(bool)), ("PlatformType", typeof(string)));

    // D-270: the stg_shd_* tables (41_BlueTrack_SelfHostedDirectStaging.sql), in REST's shape.
    public DataTable DirectPlatforms { get; } = Table("stg_shd_platforms", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("PlatformId", typeof(string)), ("Name", typeof(string)), ("SystemType", typeof(string)), ("PlatformType", typeof(string)), ("PlatformBaseId", typeof(string)),
        ("Active", typeof(bool)), ("Description", typeof(string)), ("AllowedSafes", typeof(string)),
        ("PerformPeriodicVerification", typeof(bool)), ("RequirePasswordVerificationEveryXDays", typeof(int)), ("AllowManualVerification", typeof(bool)),
        ("PerformPeriodicChange", typeof(bool)), ("RequirePasswordChangeEveryXDays", typeof(int)), ("AllowManualChange", typeof(bool)),
        ("AutomaticReconcileWhenUnsynched", typeof(bool)), ("AllowManualReconciliation", typeof(bool)),
        ("RequirePrivilegedSessionMonitoringAndIsolation", typeof(bool)), ("RecordAndSaveSessionActivity", typeof(bool)), ("PsmServerId", typeof(string)),
        ("RequireDualControlPasswordAccessApproval", typeof(bool)), ("EnforceCheckinCheckoutExclusiveAccess", typeof(bool)), ("EnforceOnetimePasswordAccess", typeof(bool)),
        ("RequireUsersToSpecifyReasonForAccess", typeof(bool)), ("VerifyOnAdd", typeof(bool)), ("ChangeOnAdd", typeof(bool)), ("ChangePasswordInResetMode", typeof(bool)));

    public DataTable DirectPlatformProperties { get; } = Table("stg_shd_platform_properties", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("PlatformId", typeof(string)), ("Kind", typeof(string)), ("Name", typeof(string)), ("DisplayName", typeof(string)));

    public DataTable DirectSafes { get; } = Table("stg_shd_safes", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("SafeNumber", typeof(long)), ("SafeUrlId", typeof(string)), ("SafeName", typeof(string)), ("Description", typeof(string)), ("Location", typeof(string)),
        ("CreatorId", typeof(string)), ("CreatorName", typeof(string)), ("OlacEnabled", typeof(bool)), ("ManagingCpm", typeof(string)),
        ("NumberOfVersionsRetention", typeof(int)), ("NumberOfDaysRetention", typeof(int)), ("AutoPurgeEnabled", typeof(bool)),
        ("CreationTime", typeof(DateTime)), ("LastModificationTime", typeof(DateTime)), ("IsExpiredMember", typeof(bool)));

    public DataTable DirectAccounts { get; } = Table("stg_shd_accounts", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("AccountId", typeof(string)), ("SafeNumber", typeof(long)), ("FileId", typeof(long)), ("SafeName", typeof(string)), ("Name", typeof(string)),
        ("Address", typeof(string)), ("UserName", typeof(string)), ("PlatformId", typeof(string)), ("SecretType", typeof(string)),
        ("CreatedTime", typeof(DateTime)), ("CategoryModificationTime", typeof(DateTime)), ("AutomaticManagementEnabled", typeof(bool)),
        ("ManualManagementReason", typeof(string)), ("CpmStatus", typeof(string)), ("LastModifiedTime", typeof(DateTime)),
        ("LastReconciledTime", typeof(DateTime)), ("LastVerifiedTime", typeof(DateTime)), ("RemoteMachines", typeof(string)),
        ("AccessRestrictedToRemoteMachines", typeof(bool)));

    public DataTable DirectAccountProperties { get; } = Table("stg_shd_account_properties", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("AccountId", typeof(string)), ("Name", typeof(string)), ("Value", typeof(string)));

    public DataTable DirectCpmActivity { get; } = Table("stg_shd_account_cpm_activity", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("AccountId", typeof(string)), ("ActivityCode", typeof(int)), ("Activity", typeof(string)), ("ActivityTime", typeof(DateTime)),
        ("UserName", typeof(string)), ("Reason", typeof(string)), ("MoreInfo", typeof(string)));

    // D-280 (48_BlueTrack_SelfHostedDirectExtras.sql).
    public DataTable DirectAccountDetails { get; } = Table("stg_shd_account_details", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("AccountId", typeof(string)), ("CategoryModificationTime", typeof(DateTime)), ("DeviceType", typeof(string)), ("CreationMethod", typeof(string)),
        ("LogonSafe", typeof(string)), ("LogonFolder", typeof(string)), ("LogonName", typeof(string)),
        ("ReconcileSafe", typeof(string)), ("ReconcileFolder", typeof(string)), ("ReconcileName", typeof(string)), ("ReadThisPull", typeof(bool)));

    public DataTable DirectSafeMembers { get; } = Table("stg_shd_safe_members",
        [("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)), ("SafeUrlId", typeof(string)), ("SafeName", typeof(string)),
         ("SafeNumber", typeof(long)), ("MemberId", typeof(long)), ("MemberName", typeof(string)), ("MemberType", typeof(string)),
         ("MembershipExpirationDate", typeof(DateTime)), ("IsExpiredMembershipEnable", typeof(bool)), ("IsPredefinedUser", typeof(bool)), ("IsReadOnly", typeof(bool)),
         .. SelfHostedStagingMapper.DirectPermissionColumns.Select(c => (c, typeof(bool)))]);

    public DataTable DirectUsers { get; } = Table("stg_shd_users", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("UserId", typeof(long)), ("UserName", typeof(string)), ("Source", typeof(string)), ("UserType", typeof(string)), ("ComponentUser", typeof(bool)),
        ("Location", typeof(string)), ("Enabled", typeof(bool)), ("Suspended", typeof(bool)), ("FirstName", typeof(string)), ("MiddleName", typeof(string)),
        ("LastName", typeof(string)), ("DetailsRead", typeof(bool)), ("Organization", typeof(string)), ("Department", typeof(string)), ("Title", typeof(string)),
        ("Profession", typeof(string)), ("BusinessEmail", typeof(string)), ("HomeEmail", typeof(string)), ("OtherEmail", typeof(string)), ("HomePage", typeof(string)),
        ("UserDn", typeof(string)), ("DistinguishedName", typeof(string)), ("Description", typeof(string)), ("ExpiryDate", typeof(DateTime)),
        ("LastSuccessfulLoginDate", typeof(DateTime)), ("PasswordNeverExpires", typeof(bool)), ("ChangePassOnNextLogon", typeof(bool)));

    public DataTable DirectUserAuthorizations { get; } = Table("stg_shd_user_authorizations", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("UserId", typeof(long)), ("Kind", typeof(string)), ("Value", typeof(string)));

    public DataTable DirectGroups { get; } = Table("stg_shd_groups", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("GroupId", typeof(long)), ("GroupName", typeof(string)), ("GroupType", typeof(string)), ("Location", typeof(string)), ("Description", typeof(string)),
        ("Directory", typeof(string)), ("Dn", typeof(string)));

    public DataTable DirectGroupMembers { get; } = Table("stg_shd_group_members", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("GroupId", typeof(long)), ("MemberId", typeof(long)), ("MemberName", typeof(string)));

    // D-281 (48_BlueTrack_SelfHostedDirectExtras.sql).
    public DataTable DirectAdGroupReads { get; } = Table("stg_shd_ad_group_reads", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("GroupSource", typeof(string)), ("GroupRef", typeof(string)), ("GroupName", typeof(string)), ("Dn", typeof(string)), ("DomainName", typeof(string)),
        ("Status", typeof(string)), ("MemberCount", typeof(int)), ("Message", typeof(string)));

    public DataTable DirectAdGroupMembers { get; } = Table("stg_shd_ad_group_members", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("GroupSource", typeof(string)), ("GroupRef", typeof(string)), ("DomainName", typeof(string)), ("SamAccountName", typeof(string)),
        ("DistinguishedName", typeof(string)), ("ObjectSid", typeof(string)), ("DisplayName", typeof(string)), ("IsEnabled", typeof(bool)));

    // D-282 (48_BlueTrack_SelfHostedDirectExtras.sql).
    public DataTable DirectApplications { get; } = Table("stg_shd_applications", ("ImportBatchId", typeof(Guid)), ("SourceFileName", typeof(string)),
        ("AppId", typeof(string)), ("Description", typeof(string)), ("Location", typeof(string)), ("Disabled", typeof(bool)), ("ExpirationDate", typeof(string)),
        ("AccessPermittedFrom", typeof(int)), ("AccessPermittedTo", typeof(int)), ("AllowExtendedAuthenticationRestrictions", typeof(bool)),
        ("BusinessOwnerFirstName", typeof(string)), ("BusinessOwnerLastName", typeof(string)), ("BusinessOwnerEmail", typeof(string)),
        ("BusinessOwnerPhone", typeof(string)), ("AuthenticationsRead", typeof(bool)));

    public DataTable DirectApplicationAuthentications { get; } = Table("stg_shd_application_authentications", ("ImportBatchId", typeof(Guid)),
        ("SourceFileName", typeof(string)), ("AppId", typeof(string)), ("AuthId", typeof(string)), ("AuthType", typeof(string)), ("AuthValue", typeof(string)),
        ("Comment", typeof(string)), ("IsFolder", typeof(bool)), ("AllowInternalScripts", typeof(bool)), ("Subject", typeof(string)), ("Issuer", typeof(string)),
        ("SubjectAlternativeName", typeof(string)));

    /// <summary>Problems the mapping found (a value that didn't fit); shown with the pull.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>The EVD-shaped tables, in the order they are written.</summary>
    public IEnumerable<DataTable> Evd => [Users, Groups, GroupMembers, Safes, Owners, Files, ObjectProperties, Platforms];

    /// <summary>The stg_shd_* tables (D-270).</summary>
    public IEnumerable<DataTable> Direct =>
        [DirectPlatforms, DirectPlatformProperties, DirectSafes, DirectAccounts, DirectAccountProperties, DirectCpmActivity, DirectAccountDetails,
         DirectSafeMembers, DirectUsers, DirectUserAuthorizations, DirectGroups, DirectGroupMembers, DirectApplications, DirectApplicationAuthentications,
         DirectAdGroupReads, DirectAdGroupMembers];

    /// <summary>Every table, in the order they are written.</summary>
    public IEnumerable<DataTable> All => [.. Evd, .. Direct];

    private static DataTable Table(string name, params (string Name, Type Type)[] columns)
    {
        var table = new DataTable(name);
        foreach (var (columnName, type) in columns) table.Columns.Add(columnName, type);
        return table;
    }
}
