using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Design_Risk_Scoring.md, D-101-105, D-119 Phase B: bulk CSV upload for
/// all four ETL-fed relationships plus Target/Access Group inventory,
/// unified into one mechanic -- the design doc's own "uploading an existing
/// external export as-is just needs a one-time mapping profile" already
/// meant a scheduled network-path puller was never required. Every
/// endpoint validates row-by-row (a bad row doesn't fail the whole batch)
/// and returns a summary, matching Design_Risk_Scoring.md's own "reports
/// errors per row rather than failing the whole batch on one bad row".
/// </summary>
[ApiController]
[Route("api/admin/risk-scoring/import")]
public sealed class RiskScoringImportController(
    TargetRepository targetRepository,
    RiskScoringImportRepository importRepository,
    TargetMatchingService targetMatchingService,
    ImportMappingService importMappingService,
    CurrentUserResolver currentUserResolver) : ControllerBase
{
    private static readonly string[] FixedTargetColumns = ["TargetType", "TargetName", "RiskScore", "Description", "DiscoverySource"];

    [HttpGet("target-inventory/template")]
    [Authorize(Policy = Permissions.ManageTargets)]
    public async Task<IActionResult> GetTargetInventoryTemplate()
    {
        var identifierTypes = await targetRepository.GetIdentifierTypesAsync();
        var headers = FixedTargetColumns.Concat(identifierTypes.Select(t => t.IdentifierType));
        return File(Encoding.UTF8.GetBytes(string.Join(',', headers) + "\r\n"), "text/csv", "target-inventory-template.csv");
    }

    [HttpPost("target-inventory")]
    [Authorize(Policy = Permissions.ManageTargets)]
    public async Task<IActionResult> ImportTargetInventory(IFormFile file, [FromForm] int? mappingProfileKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
        var mapping = await importMappingService.GetActiveMappingAsync("TargetInventory", mappingProfileKey);
        var identifierTypes = await targetRepository.GetIdentifierTypesAsync();
        var importBatchId = Guid.NewGuid();

        var result = new ImportRunResult();
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            try
            {
                var row = rows[i];
                var targetType = ImportMappingService.ResolveField(row, mapping, "TargetType") ?? throw new InvalidOperationException("TargetType is required.");
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
                    targetType, targetName, riskScore,
                    ImportMappingService.ResolveField(row, mapping, "Description"),
                    ImportMappingService.ResolveField(row, mapping, "DiscoverySource"),
                    identifiers, importBatchId, file.FileName, user.UserKey);

                result.Record(matchResult.Outcome);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = rowNumber, Error = ex.Message });
            }
        }

        return Ok(result.ToResponse(rows.Count));
    }

    [HttpGet("access-group-inventory/template")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public IActionResult GetAccessGroupInventoryTemplate() =>
        File(Encoding.UTF8.GetBytes("GroupName,GroupIdentifier,GroupScope,BaseRiskScore,Description\r\n"), "text/csv", "access-group-inventory-template.csv");

    [HttpPost("access-group-inventory")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public async Task<IActionResult> ImportAccessGroupInventory(IFormFile file, [FromForm] int? mappingProfileKey)
    {
        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
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
                    ImportMappingService.ResolveField(row, mapping, "Description"), null, importBatchId, file.FileName);
                result.Record(TargetMatchOutcome.Created); // upsert -- treated as "succeeded", not distinguishing create/update here
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return Ok(result.ToResponse(rows.Count));
    }

    [HttpGet("access-group-target-map/template")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public IActionResult GetAccessGroupTargetMapTemplate() =>
        File(Encoding.UTF8.GetBytes("GroupIdentifier,TargetIdentifierType,TargetIdentifierValue\r\n"), "text/csv", "access-group-target-map-template.csv");

    [HttpPost("access-group-target-map")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public async Task<IActionResult> ImportAccessGroupTargetMap(IFormFile file, [FromForm] int? mappingProfileKey)
    {
        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
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

                var inserted = await importRepository.InsertAccessGroupTargetMapAsync(accessGroupKey, targetKey, importBatchId, file.FileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return Ok(result.ToResponse(rows.Count));
    }

    [HttpGet("account-access-group-membership/template")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public IActionResult GetAccountAccessGroupMembershipTemplate() =>
        File(Encoding.UTF8.GetBytes("AccountName,GroupIdentifier\r\n"), "text/csv", "account-access-group-membership-template.csv");

    [HttpPost("account-access-group-membership")]
    [Authorize(Policy = Permissions.ManageAccessGroups)]
    public async Task<IActionResult> ImportAccountAccessGroupMembership(IFormFile file, [FromForm] int? mappingProfileKey)
    {
        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
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

                var inserted = await importRepository.InsertAccountAccessGroupMapAsync(accountKey, accessGroupKey, importBatchId, file.FileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return Ok(result.ToResponse(rows.Count));
    }

    [HttpGet("account-target-map/template")]
    [Authorize(Policy = Permissions.ManageTargets)]
    public IActionResult GetAccountTargetMapTemplate() =>
        File(Encoding.UTF8.GetBytes("AccountName,TargetIdentifierType,TargetIdentifierValue\r\n"), "text/csv", "account-target-map-template.csv");

    [HttpPost("account-target-map")]
    [Authorize(Policy = Permissions.ManageTargets)]
    public async Task<IActionResult> ImportAccountTargetMap(IFormFile file, [FromForm] int? mappingProfileKey)
    {
        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
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

                var inserted = await importRepository.InsertAccountTargetMapAsync(accountKey, targetKey, "ManualImport", importBatchId, file.FileName);
                result.Record(inserted ? TargetMatchOutcome.Created : TargetMatchOutcome.Merged);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ImportRowError { RowNumber = i + 2, Error = ex.Message });
            }
        }

        return Ok(result.ToResponse(rows.Count));
    }

    /// <summary>Single-add analog of account-target-map -- an analyst picks one Account and one Target directly.</summary>
    [HttpPost("~/api/admin/risk-scoring/account-target-links")]
    [Authorize(Policy = Permissions.ManageTargets)]
    public async Task<IActionResult> CreateAccountTargetLink([FromBody] CreateAccountTargetLinkRequest request)
    {
        var accountKey = await importRepository.ResolveAccountKeyAsync(request.AccountKey?.ToString(), request.AccountName);
        if (accountKey is null) return Problem(title: "Account not found", statusCode: StatusCodes.Status400BadRequest);

        var inserted = await importRepository.InsertAccountTargetMapAsync(accountKey.Value, request.TargetKey, "ManualImport", null, null);
        return inserted ? NoContent() : Conflict(new { message = "This account is already directly linked to this target." });
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

public sealed class CreateAccountTargetLinkRequest
{
    public long? AccountKey { get; init; }
    public string? AccountName { get; init; }
    public required int TargetKey { get; init; }
}
