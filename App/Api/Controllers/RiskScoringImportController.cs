using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Imports;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Design_Risk-Scoring.md, D-101-105, D-119 Phase B: bulk CSV upload for
/// all four ETL-fed relationships plus Target/Access Group inventory,
/// unified into one mechanic -- the design doc's own "uploading an existing
/// external export as-is just needs a one-time mapping profile" already
/// meant a scheduled network-path puller was never required. Every
/// endpoint validates row-by-row (a bad row doesn't fail the whole batch)
/// and returns a summary, matching Design_Risk-Scoring.md's own "reports
/// errors per row rather than failing the whole batch on one bad row".
/// The import logic itself lives in RiskScoringImportService (Data Sources
/// phase 1), shared with the scheduled data feeds; this controller keeps
/// the HTTP side: routes, permissions, templates, reading the upload.
/// </summary>
[ApiController]
[Route("api/admin/risk-scoring/import")]
public sealed class RiskScoringImportController(
    TargetRepository targetRepository,
    RiskScoringImportRepository importRepository,
    RiskScoringImportService importService,
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
        return Ok(await importService.ImportTargetInventoryAsync(rows, mappingProfileKey, file.FileName, user.UserKey));
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
        return Ok(await importService.ImportAccessGroupInventoryAsync(rows, mappingProfileKey, file.FileName));
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
        return Ok(await importService.ImportAccessGroupTargetMapAsync(rows, mappingProfileKey, file.FileName));
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
        return Ok(await importService.ImportAccountAccessGroupMembershipAsync(rows, mappingProfileKey, file.FileName));
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
        return Ok(await importService.ImportAccountTargetMapAsync(rows, mappingProfileKey, file.FileName));
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
}

public sealed class CreateAccountTargetLinkRequest
{
    public long? AccountKey { get; init; }
    public string? AccountName { get; init; }
    public required int TargetKey { get; init; }
}
