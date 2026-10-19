using System.Data;

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

    private readonly Dictionary<string, long> _locationIds;
    private long _nextLocationId;

    /// <param name="knownLocations">dim_location's Self-Hosted rows (name to the EVD's numeric id), so a location keeps its id when the source changes.</param>
    public SelfHostedStagingMapper(IReadOnlyDictionary<string, long>? knownLocations = null)
    {
        _locationIds = new Dictionary<string, long>(knownLocations ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
        _nextLocationId = (_locationIds.Count == 0 ? -1 : _locationIds.Values.Max()) + 1;
    }

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

        foreach (var m in snapshot.Members)
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
        if (longNames > 0) tables.Warnings.Add($"{longNames} account propert{(longNames == 1 ? "y" : "ies")} with a name over 29 characters {(longNames == 1 ? "was" : "were")} not staged (the staging column holds 29).");

        foreach (var p in snapshot.Platforms)
        {
            tables.Platforms.Rows.Add(batchId, source, Cut(p.Id, 100), Db(Cut(p.Name, 200)), Db(Cut(p.Description, 1000)), Db(p.Active), Db(p.PlatformType?.Trim().ToLowerInvariant()));
        }
        return tables;
    }

    /// <summary>PVWA names a location by path; the EVD numbers it. A known name keeps its number, a new one takes the next free number.</summary>
    private (long? Id, string? Name) Location(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);
        var name = Cut(path.Trim(), 128)!;
        if (!_locationIds.TryGetValue(name, out var id)) _locationIds[name] = id = _nextLocationId++;
        return (id, name);
    }

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

    /// <summary>Problems the mapping found (a value that didn't fit); shown with the pull.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Every table, in the order they are written.</summary>
    public IEnumerable<DataTable> All => [Users, Groups, GroupMembers, Safes, Owners, Files, ObjectProperties, Platforms];

    private static DataTable Table(string name, params (string Name, Type Type)[] columns)
    {
        var table = new DataTable(name);
        foreach (var (columnName, type) in columns) table.Columns.Add(columnName, type);
        return table;
    }
}
