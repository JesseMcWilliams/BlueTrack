using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-232: Resolve and Undo on Reports > Unresolved Entitlement Members, and the
/// Resolve form's user/group search, all under ConfirmReconciliation (as the
/// Possible Account Matches actions, D-212). The report itself stays in
/// ReportsController under ViewDashboard. Resolve and Undo are audit-logged
/// as EntitlementMemberResolution events. D-279: each call names the member's
/// source (sourceSystemCode PRIVCLOUD, the default, or SELFHOSTED).
/// </summary>
[ApiController]
[Route("api/reports/unresolved-entitlement-members")]
[Authorize(Policy = Permissions.ConfirmReconciliation)]
public sealed class EntitlementMemberResolutionController(
    EntitlementMemberResolutionRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEventType = "EntitlementMemberResolution";
    private const int MaxNoteLength = 500;
    private const int MaxMemberIdLength = 300;

    /// <summary>The source's users (memberType=User) or groups (Group) whose name contains q (2 to 300 characters), at most 20.</summary>
    [HttpGet("principals")]
    public async Task<IActionResult> SearchPrincipals([FromQuery] string? memberType, [FromQuery] string? q, [FromQuery] string? sourceSystemCode)
    {
        if (SourceCode(sourceSystemCode) is not { } source) return BadSourceCode();
        if (!IsMemberType(memberType)) return BadMemberType();
        var text = q?.Trim() ?? "";
        if (text.Length < 2)
        {
            return Problem(title: "Search text too short", detail: "Type at least 2 characters to search.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (text.Length > 300)
        {
            return Problem(title: "Search text too long", detail: "Search text can be at most 300 characters.", statusCode: StatusCodes.Status400BadRequest);
        }
        return Ok(await repository.SearchAsync(source, memberType!, text));
    }

    /// <summary>Links the member to a user or group of its type and source (Linked), or labels it External (External, with an optional note).</summary>
    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromBody] ResolveEntitlementMemberRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        if (SourceCode(request.SourceSystemCode) is not { } source) return BadSourceCode();
        if (!IsMemberType(request.MemberType)) return BadMemberType();
        if (BadMemberId(request.SourceMemberId) is { } badId) return badId;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            return Problem(title: "Note too long", detail: $"The note can be at most {MaxNoteLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        int? userKey = null, groupKey = null;
        string? linkedName = null;
        switch (request.Resolution)
        {
            case "Linked":
                var key = request.MemberType == "User" ? request.UserKey : request.GroupKey;
                if (key is null)
                {
                    return Problem(title: "Nothing to link to",
                        detail: request.MemberType == "User" ? "Choose the user to link this member to." : "Choose the group to link this member to.",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                linkedName = await repository.GetPrincipalNameAsync(source, request.MemberType!, key.Value);
                if (linkedName is null)
                {
                    return Problem(title: "Unknown user or group",
                        detail: $"There's no {SourceName(source)} {(request.MemberType == "User" ? "user" : "group")} with key {key}.",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                if (request.MemberType == "User") userKey = key; else groupKey = key;
                break;
            case "External":
                break;
            default:
                return Problem(title: "Unknown resolution", detail: "Resolution must be Linked or External.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await repository.ResolveAsync(source, request.MemberType!, request.SourceMemberId!, request.Resolution, userKey, groupKey, note, user.UserKey);
        if (result is null) return NotFoundMember();

        var (before, after) = result.Value;
        var what = request.Resolution == "Linked"
            ? $"linked to {(request.MemberType == "User" ? "user" : "group")} '{linkedName}' (key {userKey ?? groupKey})"
            : "labelled External" + (note is null ? "" : $" ({note})");
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "entitlement_member_resolution", EntityKey(request.SourceMemberId!),
            detail: AuditLogger.ClipDetail($"{SourceName(source)} {request.MemberType} '{before.MemberName ?? request.SourceMemberId}' (ID {request.SourceMemberId}) {what}; was {Describe(before)}"),
            fieldChanges: [new FieldChange("ResolutionMethod", before.ResolutionMethod, after.ResolutionMethod)]);
        return NoContent();
    }

    /// <summary>Undo: removes the member's manual resolution; it then resolves by ID or name again, or is unresolved. 404 when it has none.</summary>
    [HttpPost("unresolve")]
    public async Task<IActionResult> Unresolve([FromBody] UnresolveEntitlementMemberRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        if (SourceCode(request.SourceSystemCode) is not { } source) return BadSourceCode();
        if (!IsMemberType(request.MemberType)) return BadMemberType();
        if (BadMemberId(request.SourceMemberId) is { } badId) return badId;

        var result = await repository.UnresolveAsync(source, request.MemberType!, request.SourceMemberId!);
        if (result is null)
        {
            return Problem(title: "Not resolved by hand",
                detail: "This member has no manual resolution to undo. Reload the report.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var (before, after) = result.Value;
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "entitlement_member_resolution", EntityKey(request.SourceMemberId!),
            detail: AuditLogger.ClipDetail($"{SourceName(source)} {request.MemberType} '{before.MemberName ?? request.SourceMemberId}' (ID {request.SourceMemberId}): manual resolution undone; was {Describe(before)}, now {Describe(after)}"),
            fieldChanges: [new FieldChange("ResolutionMethod", before.ResolutionMethod, after.ResolutionMethod)]);
        return NoContent();
    }

    private static bool IsMemberType(string? memberType) => memberType is "User" or "Group";

    // D-279: no source given means Privilege Cloud, as before Self-Hosted members could be resolved.
    private static string? SourceCode(string? sourceSystemCode) => sourceSystemCode switch
    {
        null or "" or EntitlementMemberResolutionRepository.PrivilegeCloud => EntitlementMemberResolutionRepository.PrivilegeCloud,
        EntitlementMemberResolutionRepository.SelfHosted => EntitlementMemberResolutionRepository.SelfHosted,
        _ => null
    };

    private static string SourceName(string source) =>
        source == EntitlementMemberResolutionRepository.SelfHosted ? "Self-Hosted" : "Privilege Cloud";

    private ObjectResult BadSourceCode() =>
        Problem(title: "Unknown source", detail: "Source must be PRIVCLOUD or SELFHOSTED.", statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult BadMemberType() =>
        Problem(title: "Unknown member type", detail: "Member type must be User or Group.", statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult? BadMemberId(string? sourceMemberId) =>
        string.IsNullOrWhiteSpace(sourceMemberId) || sourceMemberId.Length > MaxMemberIdLength
            ? Problem(title: "Member ID missing", detail: $"Give the member's ID from the report (at most {MaxMemberIdLength} characters).", statusCode: StatusCodes.Status400BadRequest)
            : null;

    private ObjectResult NotFoundMember() => Problem(
        title: "Member not found",
        detail: "No current entitlement has this member ID. It may have resolved by ID, or left CyberArk, since the report was loaded. Reload the report.",
        statusCode: StatusCodes.Status404NotFound);

    // audit_event.EntityKey is NVARCHAR(100).
    private static string EntityKey(string sourceMemberId) => sourceMemberId.Length > 100 ? sourceMemberId[..100] : sourceMemberId;

    private static string Describe(EntitlementMemberResolutionRepository.MemberState state) => state.ResolutionMethod switch
    {
        null => "unresolved",
        EntitlementResolutionMethods.ById => $"resolved by ID to '{state.ResolvedToName}'",
        EntitlementResolutionMethods.ByName => $"matched by name to '{state.ResolvedToName}'",
        EntitlementResolutionMethods.Manual => $"linked to '{state.ResolvedToName}'",
        EntitlementResolutionMethods.External => "External",
        var other => other
    };
}
