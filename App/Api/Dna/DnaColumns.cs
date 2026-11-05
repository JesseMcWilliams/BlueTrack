using BlueTrack.Api.AccountProgress;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Dna;

/// <summary>D-341, D-344: the DNA account columns the page sorts on and the ones an admin can restrict.</summary>
public static class DnaColumns
{
    /// <summary>
    /// D-344: the columns that can need ViewDnaSensitiveColumns. The finding, the account's name and its BlueTrack link
    /// can't: without them a row means nothing.
    /// </summary>
    public static readonly IReadOnlyList<string> Restrictable =
    [
        "MachineName", "MachineType", "OsVersion", "DisplayName", "AccountDescription", "AccountType", "AccountCategory", "AccountGroup",
        "PrivilegedDomainGroup", "InstanceName", "DatabaseName", "Roles", "DbPermissions", "ComplianceStatus", "AccountState", "ThreatCause",
        "PassTheHashVulnerable", "PassTheHashHashFound", "ServiceAccountType", "ServiceAccountDescription", "SpnDescription", "LocalMappedAccount",
        "PasswordNeverExpires", "PasswordAgeDays", "PasswordLastSet", "LastLoginDate", "AccountExpirationDate", "Details"
    ];

    /// <summary>D-341: the page's sortable columns and the SQL each sorts by (web.dna_account d, dbo.fact_account fa).</summary>
    public static readonly IReadOnlyDictionary<string, string> Sortable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SheetCode"] = "d.SheetCode",
        ["MachineName"] = "d.MachineName",
        ["AccountName"] = "d.AccountName",
        ["AccountCategory"] = "d.AccountCategory",
        ["AccountGroup"] = "COALESCE(d.PrivilegedDomainGroup, d.AccountGroup)",
        ["AccountState"] = "d.AccountState",
        ["ComplianceStatus"] = "d.ComplianceStatus",
        ["PasswordAgeDays"] = "d.PasswordAgeDays",
        ["LastLoginDate"] = "d.LastLoginDate",
        ["Tracked"] = "fa.AccountName"
    };

    /// <summary>
    /// D-344: <paramref name="row"/> with the columns in <paramref name="hidden"/> blanked, so they never leave the API
    /// for someone without ViewDnaSensitiveColumns.
    /// </summary>
    public static DnaAccount Blank(DnaAccount row, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return row;
        bool H(string c) => hidden.Contains(c);
        return new DnaAccount
        {
            DnaAccountKey = row.DnaAccountKey, SheetCode = row.SheetCode, IsDomainAccount = row.IsDomainAccount, AccountName = row.AccountName,
            TrackedAccountKey = row.TrackedAccountKey, TrackedAccountName = row.TrackedAccountName, TrackedBy = row.TrackedBy, IsUnresolvedSid = row.IsUnresolvedSid,
            MachineName = H("MachineName") ? null : row.MachineName,
            MachineType = H("MachineType") ? null : row.MachineType,
            OsVersion = H("OsVersion") ? null : row.OsVersion,
            DisplayName = H("DisplayName") ? null : row.DisplayName,
            AccountDescription = H("AccountDescription") ? null : row.AccountDescription,
            AccountType = H("AccountType") ? null : row.AccountType,
            AccountCategory = H("AccountCategory") ? null : row.AccountCategory,
            AccountGroup = H("AccountGroup") ? null : row.AccountGroup,
            PrivilegedDomainGroup = H("PrivilegedDomainGroup") ? null : row.PrivilegedDomainGroup,
            InstanceName = H("InstanceName") ? null : row.InstanceName,
            DatabaseName = H("DatabaseName") ? null : row.DatabaseName,
            Roles = H("Roles") ? null : row.Roles,
            DbPermissions = H("DbPermissions") ? null : row.DbPermissions,
            ComplianceStatus = H("ComplianceStatus") ? null : row.ComplianceStatus,
            AccountState = H("AccountState") ? null : row.AccountState,
            ThreatCause = H("ThreatCause") ? null : row.ThreatCause,
            PassTheHashVulnerable = H("PassTheHashVulnerable") ? null : row.PassTheHashVulnerable,
            PassTheHashHashFound = H("PassTheHashHashFound") ? null : row.PassTheHashHashFound,
            ServiceAccountType = H("ServiceAccountType") ? null : row.ServiceAccountType,
            ServiceAccountDescription = H("ServiceAccountDescription") ? null : row.ServiceAccountDescription,
            SpnDescription = H("SpnDescription") ? null : row.SpnDescription,
            LocalMappedAccount = H("LocalMappedAccount") ? null : row.LocalMappedAccount,
            PasswordNeverExpires = H("PasswordNeverExpires") ? null : row.PasswordNeverExpires,
            PasswordAgeDays = H("PasswordAgeDays") ? null : row.PasswordAgeDays,
            PasswordLastSet = H("PasswordLastSet") ? null : row.PasswordLastSet,
            LastLoginDate = H("LastLoginDate") ? null : row.LastLoginDate,
            AccountExpirationDate = H("AccountExpirationDate") ? null : row.AccountExpirationDate,
            Details = H("Details") ? null : row.Details
        };
    }
}

/// <summary>
/// D-342: the DNA fields a tracking rule's condition can test, and the operators each takes (the Progress Rules'
/// operators and matching, dbo.fn_ProgressConditionMatches, so they ignore case).
/// </summary>
public static class DnaTrackRuleFields
{
    public static readonly IReadOnlyList<ProgressRuleField> All =
    [
        Field("Finding", ProgressRuleFields.Text), Field("MachineName", ProgressRuleFields.Text), Field("MachineType", ProgressRuleFields.Text),
        Field("OsVersion", ProgressRuleFields.Text), Field("AccountName", ProgressRuleFields.Text), Field("AccountType", ProgressRuleFields.Text),
        Field("AccountCategory", ProgressRuleFields.Text), Field("AccountGroup", ProgressRuleFields.Text), Field("PrivilegedDomainGroup", ProgressRuleFields.Text),
        Field("ComplianceStatus", ProgressRuleFields.Text), Field("AccountState", ProgressRuleFields.Text), Field("ThreatCause", ProgressRuleFields.Text),
        Field("ServiceAccountType", ProgressRuleFields.Text), Field("DomainAccount", ProgressRuleFields.YesNo),
        Field("PasswordNeverExpires", ProgressRuleFields.YesNo), Field("PasswordLastSet", ProgressRuleFields.Date), Field("LastLogin", ProgressRuleFields.Date),
        Field("UnresolvedSid", ProgressRuleFields.YesNo)   // D-347
    ];

    // The Progress Rules' operators for the same kind of field.
    private static ProgressRuleField Field(string name, string kind) => new(name, kind, ProgressRuleFields.All.First(f => f.Kind == kind).Operators);

    /// <summary>Each field's value for web.dna_account d, as (FieldName, TextValue, DateValue) rows.</summary>
    internal const string ValuesSql = """
        VALUES (N'Finding', d.SheetCode, NULL), (N'MachineName', d.MachineName, NULL), (N'MachineType', d.MachineType, NULL),
               (N'OsVersion', d.OsVersion, NULL), (N'AccountName', d.AccountName, NULL), (N'AccountType', d.AccountType, NULL),
               (N'AccountCategory', d.AccountCategory, NULL), (N'AccountGroup', d.AccountGroup, NULL), (N'PrivilegedDomainGroup', d.PrivilegedDomainGroup, NULL),
               (N'ComplianceStatus', d.ComplianceStatus, NULL), (N'AccountState', d.AccountState, NULL), (N'ThreatCause', d.ThreatCause, NULL),
               (N'ServiceAccountType', d.ServiceAccountType, NULL),
               (N'DomainAccount', CASE WHEN d.IsDomainAccount = 1 THEN N'Yes' ELSE N'No' END, NULL),
               (N'PasswordNeverExpires', CASE d.PasswordNeverExpires WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
               (N'PasswordLastSet', NULL, d.PasswordLastSet), (N'LastLogin', NULL, d.LastLoginDate),
               (N'UnresolvedSid', CASE WHEN d.IsUnresolvedSid = 1 THEN N'Yes' ELSE N'No' END, NULL)
        """;

    /// <summary>Why the condition can't be saved, or null when it can.</summary>
    public static string? Problem(ProgressRuleCondition condition)
    {
        var field = All.FirstOrDefault(f => string.Equals(f.FieldName, condition.FieldName, StringComparison.Ordinal));
        if (field is null) return $"'{condition.FieldName}' isn't a DNA field a condition can test.";
        if (condition.Operator is null || !field.Operators.Contains(condition.Operator)) return $"'{condition.Operator}' can't be used with {field.FieldName}.";
        if (ProgressRuleFields.NoValue.Contains(condition.Operator)) return null;
        if (string.IsNullOrWhiteSpace(condition.Value)) return $"{field.FieldName} {condition.Operator} needs a value.";
        if (field.Kind == ProgressRuleFields.Date && !int.TryParse(condition.Value, out _)) return $"{field.FieldName} needs a number of days.";
        if (field.Kind == ProgressRuleFields.YesNo && condition.Value is not ("Yes" or "No")) return $"{field.FieldName} is Yes or No.";
        return null;
    }
}
