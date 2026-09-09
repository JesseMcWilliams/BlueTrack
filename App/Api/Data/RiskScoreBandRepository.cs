using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the new Risk Score Bands admin page (D-120): an admin-configurable
/// set of named bands over the computed EffectiveRiskScore. Simpler than
/// TargetRepository/AccessGroupRepository -- no nested child collection --
/// but adds one thing they don't need: server-side overlap validation,
/// since two bands covering the same score would make "the" band name for
/// an account ambiguous.
/// </summary>
public sealed class RiskScoreBandRepository(IDbConnectionFactory connectionFactory)
{
    private const string SelectSql = """
        SELECT RiskScoreBandKey, BandName, MinScore, MaxScore, RiskOrder, ModifiedDate
        FROM web.dim_risk_score_band
        """;

    public async Task<IReadOnlyList<RiskScoreBandSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<RiskScoreBandSummary>($"{SelectSql} ORDER BY RiskOrder");
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveRiskScoreBandRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        await EnsureNoOverlapAsync(connection, request, excludingBandKey: null);

        const string sql = """
            INSERT INTO web.dim_risk_score_band (BandName, MinScore, MaxScore, RiskOrder, ModifiedBy, ModifiedDate)
            OUTPUT inserted.RiskScoreBandKey
            VALUES (@BandName, @MinScore, @MaxScore, @RiskOrder, @ModifiedBy, SYSUTCDATETIME())
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            request.BandName,
            request.MinScore,
            request.MaxScore,
            request.RiskOrder,
            ModifiedBy = modifiedByUserKey
        });
    }

    public async Task UpdateAsync(int riskScoreBandKey, SaveRiskScoreBandRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        await EnsureNoOverlapAsync(connection, request, excludingBandKey: riskScoreBandKey);

        const string sql = """
            UPDATE web.dim_risk_score_band
            SET BandName = @BandName, MinScore = @MinScore, MaxScore = @MaxScore, RiskOrder = @RiskOrder,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE RiskScoreBandKey = @RiskScoreBandKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            RiskScoreBandKey = riskScoreBandKey,
            request.BandName,
            request.MinScore,
            request.MaxScore,
            request.RiskOrder,
            ModifiedBy = modifiedByUserKey
        });
    }

    public async Task DeleteAsync(int riskScoreBandKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.dim_risk_score_band WHERE RiskScoreBandKey = @RiskScoreBandKey", new { RiskScoreBandKey = riskScoreBandKey });
    }

    /// <summary>
    /// D-120: a new/edited band's [MinScore, MaxScore] range must not
    /// overlap any OTHER existing band -- two standard-interval ranges
    /// overlap exactly when each one's Min is <= the other's Max.
    /// </summary>
    private static async Task EnsureNoOverlapAsync(System.Data.IDbConnection connection, SaveRiskScoreBandRequest request, int? excludingBandKey)
    {
        const string sql = """
            SELECT BandName, MinScore, MaxScore FROM web.dim_risk_score_band
            WHERE (@ExcludingBandKey IS NULL OR RiskScoreBandKey <> @ExcludingBandKey)
              AND MinScore <= @MaxScore AND MaxScore >= @MinScore
            """;
        var overlaps = await connection.QueryAsync<(string BandName, int MinScore, int MaxScore)>(sql, new
        {
            request.MinScore,
            request.MaxScore,
            ExcludingBandKey = excludingBandKey
        });

        var overlap = overlaps.FirstOrDefault();
        if (overlap != default)
        {
            throw new RiskScoreBandOverlapException(
                $"Range {request.MinScore}-{request.MaxScore} overlaps existing band '{overlap.BandName}' ({overlap.MinScore}-{overlap.MaxScore}).");
        }
    }
}
