using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>D-211: web.cyberark_source_config (12_BlueTrack_CyberArkSourceConfig.sql), a single row with key 1.</summary>
public sealed class CyberArkSourceConfigRepository(IDbConnectionFactory connectionFactory)
{
    private sealed class Row
    {
        public bool ImportPrivilegeCloud { get; init; }
        public string? ExportFolderPath { get; init; }
        public string PlatformsFileName { get; init; } = "";
        public string UsersFileName { get; init; } = "";
        public string GroupsFileName { get; init; } = "";
        public string GroupMembersFileName { get; init; } = "";
        public string SafesFileName { get; init; } = "";
        public string AccountsFileName { get; init; } = "";
        public string EntitlementsFileName { get; init; } = "";
        public bool ImportSelfHosted { get; init; }
        public string SelfHostedSource { get; init; } = "Direct";
        public string? PvwaBaseUrl { get; init; }
        public int? PvwaCredentialKey { get; init; }
        public string PvwaLogonMethod { get; init; } = "CyberArk";
        public string DirectPullTime { get; init; } = "01:30";
        public int DirectMaxChangePercent { get; init; } = 20;
        public int DirectPullTimeLimitMinutes { get; init; } = 45;
        public DateTime ModifiedDate { get; init; }
        public string? ModifiedByName { get; init; }
    }

    public async Task<CyberArkSourceSettingsResponse> GetAsync()
    {
        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<Row>("""
            SELECT c.ImportPrivilegeCloud, c.ExportFolderPath, c.PlatformsFileName, c.UsersFileName, c.GroupsFileName,
                   c.GroupMembersFileName, c.SafesFileName, c.AccountsFileName, c.EntitlementsFileName,
                   c.ImportSelfHosted, c.SelfHostedSource, c.PvwaBaseUrl, c.PvwaCredentialKey,
                   c.PvwaLogonMethod, c.DirectPullTime, c.DirectMaxChangePercent, c.DirectPullTimeLimitMinutes, c.ModifiedDate, u.DisplayName AS ModifiedByName
            FROM web.cyberark_source_config c
            LEFT JOIN web.app_user u ON u.UserKey = c.ModifiedBy
            WHERE c.CyberArkSourceConfigKey = 1
            """);
        if (row is null)
        {
            return new CyberArkSourceSettingsResponse { IsConfigured = false, Settings = new CyberArkSourceSettings() };
        }

        return new CyberArkSourceSettingsResponse
        {
            IsConfigured = true,
            ModifiedDate = row.ModifiedDate,
            ModifiedByName = row.ModifiedByName,
            Settings = new CyberArkSourceSettings
            {
                ImportPrivilegeCloud = row.ImportPrivilegeCloud,
                ExportFolderPath = row.ExportFolderPath,
                PlatformsFileName = row.PlatformsFileName,
                UsersFileName = row.UsersFileName,
                GroupsFileName = row.GroupsFileName,
                GroupMembersFileName = row.GroupMembersFileName,
                SafesFileName = row.SafesFileName,
                AccountsFileName = row.AccountsFileName,
                EntitlementsFileName = row.EntitlementsFileName,
                ImportSelfHosted = row.ImportSelfHosted,
                SelfHostedSource = row.SelfHostedSource,
                PvwaBaseUrl = row.PvwaBaseUrl,
                PvwaCredentialKey = row.PvwaCredentialKey,
                PvwaLogonMethod = row.PvwaLogonMethod,
                DirectPullTime = row.DirectPullTime,
                DirectMaxChangePercent = row.DirectMaxChangePercent,
                DirectPullTimeLimitMinutes = row.DirectPullTimeLimitMinutes
            }
        };
    }

    /// <summary>Creates the row, or replaces its values.</summary>
    public async Task SaveAsync(CyberArkSourceSettings settings, int userKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            MERGE web.cyberark_source_config WITH (HOLDLOCK) AS t
            USING (SELECT 1 AS CyberArkSourceConfigKey) AS s ON t.CyberArkSourceConfigKey = s.CyberArkSourceConfigKey
            WHEN MATCHED THEN UPDATE SET
                ImportPrivilegeCloud = @ImportPrivilegeCloud, ExportFolderPath = @ExportFolderPath,
                PlatformsFileName = @PlatformsFileName, UsersFileName = @UsersFileName, GroupsFileName = @GroupsFileName,
                GroupMembersFileName = @GroupMembersFileName, SafesFileName = @SafesFileName, AccountsFileName = @AccountsFileName,
                EntitlementsFileName = @EntitlementsFileName, ImportSelfHosted = @ImportSelfHosted,
                SelfHostedSource = @SelfHostedSource, PvwaBaseUrl = @PvwaBaseUrl, PvwaCredentialKey = @PvwaCredentialKey,
                PvwaLogonMethod = @PvwaLogonMethod, DirectPullTime = @DirectPullTime, DirectMaxChangePercent = @DirectMaxChangePercent, DirectPullTimeLimitMinutes = @DirectPullTimeLimitMinutes,
                ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName,
                 GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted,
                 SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent, DirectPullTimeLimitMinutes, ModifiedBy)
                VALUES (1, @ImportPrivilegeCloud, @ExportFolderPath, @PlatformsFileName, @UsersFileName, @GroupsFileName,
                 @GroupMembersFileName, @SafesFileName, @AccountsFileName, @EntitlementsFileName, @ImportSelfHosted,
                 @SelfHostedSource, @PvwaBaseUrl, @PvwaCredentialKey, @PvwaLogonMethod, @DirectPullTime, @DirectMaxChangePercent, @DirectPullTimeLimitMinutes, @userKey);
            """, new
        {
            settings.ImportPrivilegeCloud, settings.ExportFolderPath, settings.PlatformsFileName, settings.UsersFileName,
            settings.GroupsFileName, settings.GroupMembersFileName, settings.SafesFileName, settings.AccountsFileName,
            settings.EntitlementsFileName, settings.ImportSelfHosted, settings.SelfHostedSource, settings.PvwaBaseUrl,
            settings.PvwaCredentialKey, settings.PvwaLogonMethod, settings.DirectPullTime, settings.DirectMaxChangePercent, settings.DirectPullTimeLimitMinutes, userKey
        });
    }

    /// <summary>D-228: every Privilege Cloud file's columns (web.cyberark_import_column), as the import reads them.</summary>
    public async Task<IReadOnlyList<DataFeeds.CyberArkImportColumn>> GetImportColumnsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<DataFeeds.CyberArkImportColumn>("""
            SELECT FileKind, ColumnName, IsRequired, ColumnOrder
            FROM web.cyberark_import_column
            ORDER BY FileKind, ColumnOrder
            """)).ToList();
    }

    /// <summary>D-248: whether a named credential exists (web.credential), for validating the Direct source's credential.</summary>
    public async Task<bool> CredentialExistsAsync(int credentialKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM web.credential WHERE CredentialKey = @credentialKey", new { credentialKey }) > 0;
    }
}
