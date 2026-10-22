using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>D-288 (50_BlueTrack_CpmHealth.sql): the accounts whose CPM status is failure (dbo.vw_cpm_health).</summary>
public sealed class CpmHealthRepository(IDbConnectionFactory connectionFactory)
{
    private const string Columns = """
        AccountKey, AccountName, UserName, Address, SafeName, PlatformID AS PlatformId, SourceSystemName, CPMStatus, CpmFailureSince,
        AutoManaged, ManualReason, LastCPMAction, LastCPMActionResult, LastCPMActionTime, LastVerifiedDate, LastReconciledDate,
        LastCPMModifiedDate, OwnerName, StageName, StatusName
        """;

    /// <summary>Every failing account, the longest failing first.</summary>
    public async Task<IReadOnlyList<CpmHealthRow>> GetAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<CpmHealthRow>(
            $"SELECT {Columns} FROM dbo.vw_cpm_health ORDER BY CpmFailureSince, AccountName, AccountKey")).ToList();
    }

    /// <summary>
    /// The accounts that started failing after the last CpmHealth email (or in the last day, before the first one),
    /// by SQL Server's UTC clock, like the notification log's SentDate.
    /// </summary>
    public async Task<IReadOnlyList<CpmHealthRow>> GetNewlyFailingAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<CpmHealthRow>($"""
            DECLARE @Since DATETIME2 = ISNULL(
                (SELECT MAX(nl.SentDate) FROM web.notification_log nl
                 JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey AND nt.NotificationTypeName = 'CpmHealth'),
                DATEADD(DAY, -1, SYSUTCDATETIME()));
            SELECT {Columns} FROM dbo.vw_cpm_health WHERE CpmFailureSince > @Since ORDER BY AccountName, AccountKey
            """)).ToList();
    }
}
