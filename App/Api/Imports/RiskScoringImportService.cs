using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Imports;

/// <summary>
/// The five risk-scoring CSV imports (D-101-105, D-119 Phase B), moved out of
/// RiskScoringImportController unchanged (Data Sources phase 1,
/// Planning_Data-Sources.md) so the Bulk Actions uploads and the scheduled
/// data feeds (phase 2) run the same code. Each method takes already-parsed
/// CSV rows (CsvFileReader) and validates row by row: a bad row is reported,
/// it doesn't fail the batch.
/// </summary>
public sealed class RiskScoringImportService(
    TargetRepository targetRepository,
    RiskScoringImportRepository importRepository,
    TargetMatchingService targetMatchingService,
    ImportMappingService importMappingService)
{
    public async Task<ImportResultResponse> ImportTargetInventoryAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int? mappingProfileKey, string fileName, int userKey)
    {
        var mapping = await importMappingService.GetActiveMappingAsync("TargetInventory", mappingProfileKey);
        var identifierTypes = await targetRepository.GetIdentifierTypesAsync();
        // D-124 Phase 2: TargetType is now an FK (TargetTypeKey) -- the CSV
        // keeps using the stable TypeCode (not DisplayName, which is only the
        // prettied-up UI label), resolved here against web.dim_target_type.
        var targetTypesByCode = (await targetRepository.GetTargetTypesAsync()).ToDictionary(t => t.TypeCode, t => t.TargetTypeKey);
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            try
            {
                var row = rows[i];
                var targetTypeCode = ImportMappingService.ResolveField(row, mapping, "TargetType") ?? throw new InvalidOperationException("TargetType is required.");
                if (!targetTypesByCode.TryGetValue(targetTypeCode, out var targetTypeKey))
                {
                    throw new InvalidOperationException($"TargetType '{targetTypeCode}' is not a recognized target type code.");
                }
                var targetName = ImportMappingService.ResolveField(row, mapping, "TargetName") ?? throw new InvalidOperationException("TargetName is required.");
                var riskScoreText = ImportMappingService.ResolveField(row, mapping, "RiskScore") ?? throw new InvalidOperationException("RiskScore is required.");
                if (!int.TryParse(riskScoreText, out var riskScore))
                {
                    throw new InvalidOperationException($"RiskScore '{riskScoreText}' is not a whole number.");
                }

                var identifiers = identifierTypes
                    .Select(t => new TargetMatchIdentifier(t.IdentifierType, ImportMappingService.ResolveField(row, mapping, t.IdentifierType) ?? ""))
                    .Where(i => !string.IsNullOrWhiteSpace(i.IdentifierValue))
                    .ToList();

                var matchResult = await targetMatchingService.MatchOrCreateAsync(
                    targetTypeKey, targetName, riskScore,
                    ImportMappingService.ResolveField(row, mapping, "Description"),
                    ImportMappingService.ResolveField(row, mapping, "DiscoverySource"),
                    identifiers, importBatchId, fileName, userKey);

                result.Record(matchResult.Outcome);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Error = ex.Message });
            }
        }

        return result.ToResponse(rows.Count);
    }

    public async Task<ImportResultResponse> ImportAccessGroupInventoryAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int? mappingProfileKey, string fileName)
    {
        var mapping = await importMappingService.GetActiveMappingAsync("AccessGroupInventory", mappingProfileKey);
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var row = rows[i];
                var groupName = ImportMappingService.ResolveField(row, mapping, "GroupName") ?? throw new InvalidOperationException("GroupName is required.");
                var groupIdentifier = ImportMappingService.ResolveField(row, mapping, "GroupIdentifier") ?? throw new InvalidOperationException("GroupIdentifier is required.");
                var groupScope = ImportMappingService.ResolveField(row, mapping, "GroupScope") ?? "Domain";
                var baseRiskScoreText = ImportMappingService.ResolveField(row, mapping, "BaseRiskScore") ?? throw new InvalidOperationException("BaseRiskScore is required.");
                if (!int.TryParse(baseRiskScoreText, out var baseRiskScore))
                {
                    throw new InvalidOperationException($"BaseRiskScore '{baseRiskScoreText}' is not a whole number.");
                }

                await importRepository.UpsertAccessGroupAsync(groupName, groupIdentifier, groupScope, baseRiskScore,
                    ImportMappingService.ResolveField(row, mapping, "Description"), null, importBatchId, fileName);
                result.Record(TargetMatchOutcome.Created); // upsert -- treated as "succeeded", not distinguishing create/update here
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return result.ToResponse(rows.Count);
    }

    public async Task<ImportResultResponse> ImportAccessGroupTargetMapAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int? mappingProfileKey, string fileName)
    {
        var mapping = await importMappingService.GetActiveMappingAsync("AccessGroupTargetMap", mappingProfileKey);
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var row = rows[i];
                var groupIdentifier = ImportMappingService.ResolveField(row, mapping, "GroupIdentifier") ?? throw new InvalidOperationException("GroupIdentifier is required.");
                var identifierType = ImportMappingService.ResolveField(row, mapping, "TargetIdentifierType") ?? throw new InvalidOperationException("TargetIdentifierType is required.");
                var identifierValue = ImportMappingService.ResolveField(row, mapping, "TargetIdentifierValue") ?? throw new InvalidOperationException("TargetIdentifierValue is required.");

                var accessGroupKey = await importRepository.ResolveAccessGroupKeyAsync(groupIdentifier)
                    ?? throw new InvalidOperationException($"No Access Group found with identifier '{groupIdentifier}'.");
                var targetKey = await importRepository.ResolveTargetKeyByIdentifierAsync(identifierType, identifierValue)
                    ?? throw new InvalidOperationException($"No Target found with {identifierType} '{identifierValue}'.");

                var inserted = await importRepository.InsertAccessGroupTargetMapAsync(accessGroupKey, targetKey, importBatchId, fileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return result.ToResponse(rows.Count);
    }

    public async Task<ImportResultResponse> ImportAccountAccessGroupMembershipAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int? mappingProfileKey, string fileName)
    {
        var mapping = await importMappingService.GetActiveMappingAsync("AccountAccessGroupMembership", mappingProfileKey);
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var row = rows[i];
                var accountName = ImportMappingService.ResolveField(row, mapping, "AccountName");
                var accountKeyText = ImportMappingService.ResolveField(row, mapping, "AccountKey");
                var groupIdentifier = ImportMappingService.ResolveField(row, mapping, "GroupIdentifier") ?? throw new InvalidOperationException("GroupIdentifier is required.");

                var accountKey = await importRepository.ResolveAccountKeyAsync(accountKeyText, accountName)
                    ?? throw new InvalidOperationException($"No account found (AccountKey='{accountKeyText}', AccountName='{accountName}').");
                var accessGroupKey = await importRepository.ResolveAccessGroupKeyAsync(groupIdentifier)
                    ?? throw new InvalidOperationException($"No Access Group found with identifier '{groupIdentifier}'.");

                var inserted = await importRepository.InsertAccountAccessGroupMapAsync(accountKey, accessGroupKey, importBatchId, fileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return result.ToResponse(rows.Count);
    }

    public async Task<ImportResultResponse> ImportAccountTargetMapAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int? mappingProfileKey, string fileName)
    {
        var mapping = await importMappingService.GetActiveMappingAsync("AccountTargetMap", mappingProfileKey);
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var row = rows[i];
                var accountName = ImportMappingService.ResolveField(row, mapping, "AccountName");
                var accountKeyText = ImportMappingService.ResolveField(row, mapping, "AccountKey");
                var identifierType = ImportMappingService.ResolveField(row, mapping, "TargetIdentifierType") ?? throw new InvalidOperationException("TargetIdentifierType is required.");
                var identifierValue = ImportMappingService.ResolveField(row, mapping, "TargetIdentifierValue") ?? throw new InvalidOperationException("TargetIdentifierValue is required.");

                var accountKey = await importRepository.ResolveAccountKeyAsync(accountKeyText, accountName)
                    ?? throw new InvalidOperationException($"No account found (AccountKey='{accountKeyText}', AccountName='{accountName}').");
                var targetKey = await importRepository.ResolveTargetKeyByIdentifierAsync(identifierType, identifierValue)
                    ?? throw new InvalidOperationException($"No Target found with {identifierType} '{identifierValue}'.");

                var inserted = await importRepository.InsertAccountTargetMapAsync(accountKey, targetKey, "ManualImport", importBatchId, fileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return result.ToResponse(rows.Count);
    }

    private sealed class ImportRunResult
    {
        public int Created { get; private set; }
        public int Merged { get; private set; }
        public int PendingReview { get; private set; }
        public List<ImportRowError> Errors { get; } = [];

        public void Record(TargetMatchOutcome outcome)
        {
            switch (outcome)
            {
                case TargetMatchOutcome.Created: Created++; break;
                case TargetMatchOutcome.Merged: Merged++; break;
                case TargetMatchOutcome.PendingReview: PendingReview++; break;
            }
        }

        public ImportResultResponse ToResponse(int totalRows) => new()
        {
            TotalRows = totalRows,
            SucceededCount = Created + Merged + PendingReview,
            CreatedCount = Created,
            MergedCount = Merged,
            PendingReviewCount = PendingReview,
            Errors = Errors
        };
    }
}
