using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-349 (72_BlueTrack_RiskDefaultsAndFindings.sql): web.usp_DeriveRiskFindings puts a hard-coded credential on the
/// target it was found on and on the account it names, privileged when DNA says so; a linked DNA account's
/// Pass-the-Hash and non-compliant findings on its BlueTrack account; and web.ufn_ReachableRiskValues returns them.
/// </summary>
[Collection("DnaImport")]   // the findings are rebuilt from all the DNA rows, which the DNA tests replace
public class RiskFindingsTests : IAsyncLifetime
{
    private readonly string _t = $"rf{Guid.NewGuid():N}"[..10];
    private readonly List<long> _accounts = [];
    private readonly List<int> _targets = [];
    private int _importKey;
    private SqlConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqlConnection(TestDatabase.ConnectionString);
        await _connection.OpenAsync();
        _importKey = await _connection.QuerySingleAsync<int>(
            "INSERT INTO web.dna_import (FileName, AccountCount) OUTPUT inserted.DnaImportKey VALUES (@name, 0)", new { name = $"{_t}.xlsx" });
    }

    public async Task DisposeAsync()
    {
        await _connection.ExecuteAsync("""
            DELETE FROM web.dna_account WHERE DnaImportKey = @_importKey;
            DELETE FROM web.dna_hard_coded_credential WHERE DnaImportKey = @_importKey;
            DELETE FROM web.dna_import WHERE DnaImportKey = @_importKey;
            EXEC web.usp_DeriveRiskFindings;
            DELETE FROM web.account_target_map WHERE AccountKey IN @accounts OR TargetKey IN @targets;
            DELETE FROM web.account_risk_score WHERE AccountKey IN @accounts;
            DELETE FROM fact_account WHERE AccountKey IN @accounts;
            DELETE FROM web.target_identifier WHERE TargetKey IN @targets;
            DELETE FROM web.dim_target WHERE TargetKey IN @targets;
            """, new { _importKey, accounts = _accounts.DefaultIfEmpty(-1), targets = _targets.DefaultIfEmpty(-1) });
        await _connection.DisposeAsync();
    }

    private async Task<long> AccountAsync(string userName, string address)
    {
        var key = await _connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, IsDeleted)
            OUTPUT inserted.AccountKey
            VALUES ((SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY'), @id, @name, @userName, @address, 0)
            """, new { id = $"{_t}_{Guid.NewGuid():N}", name = $"{_t} {userName}@{address}", userName, address });
        _accounts.Add(key);
        return key;
    }

    private async Task<int> TargetAsync(string name, string? fqdn = null)
    {
        var key = await _connection.QuerySingleAsync<int>("""
            INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore)
            OUTPUT inserted.TargetKey
            VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @name, 0)
            """, new { name });
        if (fqdn is not null)
            await _connection.ExecuteAsync("INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@key, 'FQDN', @fqdn)", new { key, fqdn });
        _targets.Add(key);
        return key;
    }

    private Task HardCodedAsync(string machine, string account, string targetAddress) => _connection.ExecuteAsync("""
        INSERT INTO web.dna_hard_coded_credential (DnaImportKey, SheetCode, MachineName, AccountName, TargetSystemAddress)
        VALUES (@_importKey, 'HCC', @machine, @account, @targetAddress)
        """, new { _importKey, machine, account, targetAddress });

    private Task<IEnumerable<(string FindingType, int? TargetKey, long? AccountKey)>> FindingsAsync() =>
        _connection.QueryAsync<(string, int?, long?)>("""
            SELECT FindingType, TargetKey, AccountKey FROM web.risk_finding
            WHERE TargetKey IN @targets OR AccountKey IN @accounts
            """, new { targets = _targets, accounts = _accounts });

    [Fact]
    public async Task AHardCodedSecret_IsOnTheSystemAndTheAccount_PrivilegedWhenDnaSaysSo_AndLinkedDnaFindingsAreOnTheirAccount()
    {
        var server = await TargetAsync($"{_t}-app", fqdn: $"{_t}srv01.corp.example");
        var svc = await AccountAsync($"{_t}svc", $"{_t}db01");
        var otherHost = await AccountAsync($"{_t}svc", $"{_t}db02");
        var admin = await AccountAsync($"{_t}adm", $"{_t}db01.corp.example");

        // Found on the server by its short name; names svc on db01.
        await HardCodedAsync($"{_t}srv01", $"{_t}svc", $"{_t}db01");
        // Found on the server by its full name; names CORP\adm on DB01:1433, whom DNA lists as privileged on db01.
        await HardCodedAsync($"{_t}srv01.corp.example", $@"CORP\{_t}adm", $"{_t}DB01:1433");
        // Nothing to match: N/A.
        await HardCodedAsync("N/A", "N/A", "N/A");
        await _connection.ExecuteAsync("""
            INSERT INTO web.dna_account (DnaImportKey, SheetCode, IsDomainAccount, MachineName, AccountName, AccountCategory)
            VALUES (@_importKey, 'Windows Scan', 0, @machine, @account, 'Privileged Shared');
            INSERT INTO web.dna_account (DnaImportKey, SheetCode, IsDomainAccount, MachineName, AccountName, AccountCategory,
                                         PassTheHashVulnerable, ComplianceStatus, TrackedAccountKey)
            VALUES (@_importKey, 'Windows Scan', 0, @otherMachine, @otherAccount, 'Non-Privileged Shared', 'Yes', 'Non-compliant (Password account)', @otherHost);
            """, new { _importKey, machine = $"{_t}db01.corp.example", account = $"{_t}adm", otherMachine = $"{_t}db02", otherAccount = $"{_t}svc", otherHost });

        await _connection.ExecuteAsync("EXEC web.usp_DeriveRiskFindings");

        var findings = (await FindingsAsync()).OrderBy(f => f.FindingType).ThenBy(f => f.TargetKey).ThenBy(f => f.AccountKey).ToList();
        Assert.Equal(
            new (string, int?, long?)[]
            {
                ("DnaHardCodedPrivilegedSecret", null, admin),
                ("DnaHardCodedPrivilegedSecret", server, null),
                ("DnaHardCodedSecret", null, svc),
                ("DnaHardCodedSecret", server, null),
                ("DnaNonCompliant", null, otherHost),
                ("DnaPassTheHash", null, otherHost),
            }.OrderBy(f => f.Item1).ThenBy(f => f.Item2).ThenBy(f => f.Item3),
            findings);
        // An account with a finding of its own gets a score row, marked to recalculate.
        Assert.True(await _connection.QuerySingleAsync<bool>("SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @svc", new { svc }));

        // svc reaches the server (as if an access group or its address gave it), so it gets the server's two findings and its own.
        await _connection.ExecuteAsync("INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@svc, @server, 'ManualImport')", new { svc, server });
        var values = (await _connection.QueryAsync<(string EntityType, string EntityName, int RiskValue)>(
            "SELECT EntityType, EntityName, RiskValue FROM web.ufn_ReachableRiskValues('Account', @svc) WHERE EntityType = 'Finding'", new { svc })).ToList();
        Assert.Contains(values, v => v.EntityName == $"Hard-coded secret on {_t}-app" && v.RiskValue == 300);
        Assert.Contains(values, v => v.EntityName == $"Hard-coded privileged secret on {_t}-app" && v.RiskValue == 600);
        Assert.Contains(values, v => v.EntityName == "Hard-coded secret (this account)" && v.RiskValue == 300);
        Assert.Equal(3, values.Count);

        // A kind that doesn't count drops out.
        var before = await _connection.QuerySingleAsync<(int, bool)>("SELECT RiskValue, IsEnabled FROM web.risk_finding_type WHERE FindingType = 'DnaHardCodedSecret'");
        try
        {
            await _connection.ExecuteAsync("UPDATE web.risk_finding_type SET IsEnabled = 0 WHERE FindingType = 'DnaHardCodedSecret'");
            var left = await _connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.ufn_ReachableRiskValues('Account', @svc) WHERE EntityType = 'Finding'", new { svc });
            Assert.Equal(1, left);
        }
        finally
        {
            await _connection.ExecuteAsync("UPDATE web.risk_finding_type SET RiskValue = @Item1, IsEnabled = @Item2 WHERE FindingType = 'DnaHardCodedSecret'", new { Item1 = before.Item1, Item2 = before.Item2 });
        }
    }

    [Fact]
    public async Task ATargetMadeFromAnAddress_GetsTheDefaultScore()
    {
        var original = await _connection.QuerySingleAsync<int>("SELECT TOP (1) DefaultAddressTargetRiskScore FROM web.app_config");
        var address = $"{_t}-addr.example";
        await AccountAsync($"{_t}u", address);
        try
        {
            await _connection.ExecuteAsync("UPDATE web.app_config SET DefaultAddressTargetRiskScore = 250");
            await _connection.ExecuteAsync("EXEC dbo.usp_DeriveTargetsFromAccountAddress");
            var target = await _connection.QuerySingleAsync<(int TargetKey, int RiskScore)>(
                "SELECT t.TargetKey, t.RiskScore FROM web.dim_target t JOIN web.target_identifier ti ON ti.TargetKey = t.TargetKey WHERE ti.IdentifierValue = @address",
                new { address });
            _targets.Add(target.TargetKey);
            Assert.Equal(250, target.RiskScore);
        }
        finally
        {
            await _connection.ExecuteAsync("UPDATE web.app_config SET DefaultAddressTargetRiskScore = @original", new { original });
        }
    }
}
