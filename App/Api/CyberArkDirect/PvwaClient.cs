using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-248: a read-only client for a Self-Hosted PVWA's REST API (14.2 and later). Only GET calls, plus logon and
/// logoff. Calls and field names were checked against the lab PVWA on 2026-10-19; see
/// Claude_Docs/Planning_CyberArk-Direct-Integration.md for what each call returned.
/// </summary>
public sealed class PvwaClient(HttpClient http, Uri apiRoot)
{
    private const int PageSize = 1000;
    private const int MemberCallParallelism = 4;
    private const int UserDetailParallelism = 8;

    /// <summary>The API root for a PVWA address: https://host or https://host/PasswordVault, with or without a trailing slash.</summary>
    public static Uri ApiRootFor(string pvwaBaseUrl)
    {
        var trimmed = pvwaBaseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/PasswordVault", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^"/PasswordVault".Length];
        return new Uri(trimmed + "/PasswordVault/API/");
    }

    /// <summary>Logs on (CyberArk or LDAP authentication) and returns the session token; the password is used only in the request body.</summary>
    public async Task<string> LogonAsync(string method, string userName, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiRoot, $"auth/{method}/Logon/"))
        {
            Content = JsonContent.Create(new { username = userName, password, concurrentSession = true })
        };
        using var response = await SendAsync(request, "log on", ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        // The token is a JSON string; some versions send it bare.
        var token = body.StartsWith('"') ? JsonSerializer.Deserialize<string>(body) : body;
        if (string.IsNullOrWhiteSpace(token)) throw new PvwaException("PVWA accepted the logon but returned no session token.");
        return token;
    }

    public async Task LogoffAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiRoot, "Auth/Logoff/"));
        request.Headers.TryAddWithoutValidation("Authorization", token);
        try { using var _ = await http.SendAsync(request, ct); }
        catch (HttpRequestException) { /* the session times out on its own (20 minutes) */ }
    }

    /// <summary>
    /// Reads everything Self-Hosted Direct loads. Discovered (pending) accounts are deliberately not read. D-280:
    /// <paramref name="options"/> asks for the classic account details too (a nightly or Pull now pull; Test doesn't).
    /// </summary>
    public async Task<PvwaSnapshot> PullAsync(string token, CancellationToken ct, PvwaPullOptions? options = null)
    {
        var snapshot = new PvwaSnapshot();

        var targets = await GetTargetPlatformsAsync(token, snapshot.Warnings, ct);
        foreach (var item in await GetArrayAsync(token, "Platforms", "Platforms", ct))
        {
            var general = item.TryGetProperty("general", out var g) ? g : item;
            var id = Str(general, "id");
            if (id is null) continue;
            var credentials = Obj(item, "credentialsManagement");
            var session = Obj(item, "sessionManagement");
            var workflows = Obj(item, "privilegedAccessWorkflows");
            var propertyLists = Obj(item, "properties");
            var properties = new List<(string, string, string?)>();
            foreach (var (kind, array) in new[] { ("Required", Arr(propertyLists, "required")), ("Optional", Arr(propertyLists, "optional")), ("LinkedAccount", Arr(item, "linkedAccounts")) })
            {
                foreach (var prop in array) { if (Str(prop, "name") is { } name) properties.Add((kind, name, Str(prop, "displayName"))); }
            }
            targets.TryGetValue(id, out var target);
            snapshot.Platforms.Add(new PvwaPlatform(id, Str(general, "name"), Str(general, "description"), Bool(general, "active"), Str(general, "platformType"))
            {
                SystemType = Str(general, "systemType"), PlatformBaseId = Str(general, "platformBaseID"),
                AllowedSafes = Str(credentials, "allowedSafes"),
                PerformPeriodicVerification = Bool(credentials, "performPeriodicVerification"), VerifyEveryXDays = (int?)Long(credentials, "requirePasswordVerificationEveryXDays"),
                AllowManualVerification = Bool(credentials, "allowManualVerification"),
                PerformPeriodicChange = Bool(credentials, "performPeriodicChange"), ChangeEveryXDays = (int?)Long(credentials, "requirePasswordChangeEveryXDays"),
                AllowManualChange = Bool(credentials, "allowManualChange"),
                AutomaticReconcileWhenUnsynched = Bool(credentials, "automaticReconcileWhenUnsynched"), AllowManualReconciliation = Bool(credentials, "allowManualReconciliation"),
                RequirePsm = Bool(session, "requirePrivilegedSessionMonitoringAndIsolation"), RecordSessions = Bool(session, "recordAndSaveSessionActivity"),
                PsmServerId = Str(session, "PSMServerID"),
                RequireDualControl = Bool(workflows, "requireDualControlPasswordAccessApproval"),
                EnforceExclusiveAccess = Bool(workflows, "enforceCheckinCheckoutExclusiveAccess"), EnforceOneTimePassword = Bool(workflows, "enforceOnetimePasswordAccess"),
                RequireReasonForAccess = target?.RequireReason, VerifyOnAdd = target?.VerifyOnAdd, ChangeOnAdd = target?.ChangeOnAdd,
                ChangePasswordInResetMode = target?.ResetMode,
                Properties = properties
            });
        }

        foreach (var item in await GetPagedAsync(token, "Safes?extendedDetails=true", ct))
        {
            var number = Long(item, "safeNumber");
            var name = Str(item, "safeName");
            var urlId = Str(item, "safeUrlId") ?? name;
            if (number is null || name is null || urlId is null) { snapshot.Warnings.Add("A Safe without a number or name was skipped."); continue; }
            var creator = Obj(item, "creator");
            snapshot.Safes.Add(new PvwaSafe(number.Value, urlId, name, Str(item, "location"), Str(creator, "name"),
                Epoch(item, "creationTime"), (int?)Long(item, "numberOfDaysRetention"), (int?)Long(item, "numberOfVersionsRetention"))
            {
                Description = Str(item, "description"), CreatorId = Str(creator, "id") ?? Long(creator, "id")?.ToString(CultureInfo.InvariantCulture),
                OlacEnabled = Bool(item, "olacEnabled"), ManagingCpm = Str(item, "managingCPM"), AutoPurgeEnabled = Bool(item, "autoPurgeEnabled"),
                LastModificationTime = EpochAnyUnit(item, "lastModificationTime"), IsExpiredMember = Bool(item, "isExpiredMember")
            });
        }

        foreach (var item in await GetPagedAsync(token, "Accounts", ct))
        {
            var id = Str(item, "id");
            var parts = id?.Split('_');
            if (parts is not { Length: 2 } || !long.TryParse(parts[0], out var safeNumber) || !long.TryParse(parts[1], out var fileId))
            {
                snapshot.Warnings.Add($"An account with the id '{id}' was skipped: the id isn't SafeNumber_FileId.");
                continue;
            }
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("platformAccountProperties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            }
            // Missing objects read as null (Obj returns an undefined element, which every helper treats as absent).
            var management = Obj(item, "secretManagement");
            var remote = Obj(item, "remoteMachinesAccess");
            snapshot.Accounts.Add(new PvwaAccount(
                safeNumber, fileId, Str(item, "safeName") ?? "", Str(item, "name") ?? id!, Str(item, "address"), Str(item, "userName"),
                Str(item, "platformId"), Epoch(item, "createdTime"),
                Epoch(management, "lastModifiedTime") ?? Epoch(management, "lastModifiedDateTime"),
                Epoch(management, "lastVerifiedDateTime") ?? Epoch(management, "lastVerifiedTime"),
                Bool(management, "automaticManagementEnabled"),
                Str(management, "manualManagementReason"),
                properties)
            {
                SecretType = Str(item, "secretType"),
                CategoryModificationTime = Epoch(item, "categoryModificationTime") ?? Epoch(item, "CategoryModificationTime"),
                CpmStatus = Str(management, "status") is { Length: > 0 } status ? status : null,
                LastReconciledTime = Epoch(management, "lastReconciledTime") ?? Epoch(management, "lastReconciledDateTime"),
                RemoteMachines = Str(remote, "remoteMachines") is { Length: > 0 } machines ? machines : null,
                AccessRestrictedToRemoteMachines = Bool(remote, "accessRestrictedToRemoteMachines")
            });
        }

        // D-270: the CPM's verify, change and reconcile history, from each account's activity log. Only an account
        // with a CPM status has any (the status appears once the CPM first acts on it), so only those are asked:
        // one call each. A failed call loses only that account's history.
        await ReadCpmActivityAsync(token, snapshot, ct);

        if (options?.CachedDetails is not null) await ReadAccountDetailsAsync(token, snapshot, options, ct);

        // Members: one call per Safe. Without View Safe Members in a Safe the call is refused; that Safe's members
        // are missing, so say so rather than stop the pull.
        var members = new ConcurrentBag<PvwaMember>();
        var memberWarnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(snapshot.Safes, new ParallelOptions { MaxDegreeOfParallelism = MemberCallParallelism, CancellationToken = ct }, async (safe, innerCt) =>
        {
            try
            {
                // D-270: predefined members (Administrator, Auditors, ...) too; PVWA leaves them out unless asked.
                foreach (var item in await GetPagedAsync(token, $"Safes/{safe.SafeUrlId}/Members?filter=includePredefinedUsers%20eq%20true", innerCt))
                {
                    var memberId = Long(item, "memberId");
                    var memberName = Str(item, "memberName");
                    if (memberId is null || memberName is null) continue;
                    var permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    if (item.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in perms.EnumerateObject())
                        {
                            if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) permissions[p.Name] = p.Value.GetBoolean();
                        }
                    }
                    members.Add(new PvwaMember(safe.SafeNumber, safe.SafeName, memberId.Value, memberName,
                        string.Equals(Str(item, "memberType"), "Group", StringComparison.OrdinalIgnoreCase), Epoch(item, "membershipExpirationDate"), permissions)
                    {
                        SafeUrlId = safe.SafeUrlId, MemberType = Str(item, "memberType"), IsExpiredMembershipEnable = Bool(item, "isExpiredMembershipEnable"),
                        IsPredefinedUser = Bool(item, "isPredefinedUser"), IsReadOnly = Bool(item, "isReadOnly")
                    });
                }
            }
            catch (PvwaException ex) when (ex.Message.Contains("(403)"))
            {
                memberWarnings.Add($"Safe '{safe.SafeName}': members not read ({ex.Message}).");
            }
        });
        snapshot.Members.AddRange(members.OrderBy(m => m.SafeNumber).ThenBy(m => m.MemberId));
        snapshot.Warnings.AddRange(memberWarnings.OrderBy(w => w, StringComparer.Ordinal));

        // Users: the list has no email, so each person's details are read too (a failure there loses only that email).
        // Built-in and component users (Master, CPM, PVWA, providers) have no email, so they aren't asked about.
        var (usersJson, usersNote) = await GetUsersAsync(token, ct);
        if (usersNote is not null) snapshot.Warnings.Add(usersNote);
        var users = new ConcurrentBag<PvwaUser>();
        var userWarnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(usersJson, new ParallelOptions { MaxDegreeOfParallelism = UserDetailParallelism, CancellationToken = ct }, async (item, innerCt) =>
        {
            var id = Long(item, "id");
            var name = Str(item, "username");
            if (id is null || name is null) return;
            var personal = Obj(item, "personalDetails");
            JsonElement detail = default;
            if (Bool(item, "componentUser") != true && !string.Equals(Str(item, "userType"), "Built-InAdmins", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    detail = await GetJsonAsync(token, $"Users/{id}/", innerCt);
                }
                catch (PvwaException ex)
                {
                    userWarnings.Add($"User '{name}': details not read, so no email ({ex.Message}).");
                }
            }
            var detailPersonal = Obj(detail, "personalDetails");
            var internet = Obj(detail, "internet");
            var authorizations = new List<(string, string)>();
            foreach (var (kind, source, property) in new[]
                     {
                         ("VaultAuthorization", item, "vaultAuthorization"), ("VaultAuthorization", detail, "vaultAuthorization"),
                         ("UnauthorizedInterface", detail, "unAuthorizedInterfaces"), ("AuthenticationMethod", detail, "authenticationMethod"),
                         ("AllowedAuthenticationMethod", item, "allowedAuthenticationMethods"), ("AllowedAuthenticationMethod", detail, "allowedAuthenticationMethods")
                     })
            {
                foreach (var value in Strings(source, property)) { if (!authorizations.Contains((kind, value))) authorizations.Add((kind, value)); }
            }
            users.Add(new PvwaUser(id.Value, name, Str(item, "source"), Str(item, "userType"), Str(item, "location"),
                Bool(item, "enableUser") ?? true, Str(personal, "firstName"), Str(personal, "lastName"), Str(internet, "businessEmail"))
            {
                ComponentUser = Bool(item, "componentUser"), Suspended = Bool(item, "suspended") ?? Bool(detail, "suspended"),
                MiddleName = Str(personal, "middleName") ?? Str(detailPersonal, "middleName"),
                DetailsRead = detail.ValueKind == JsonValueKind.Object,
                Organization = Str(detailPersonal, "organization"), Department = Str(detailPersonal, "department"),
                Title = Str(detailPersonal, "title"), Profession = Str(detailPersonal, "profession"),
                HomeEmail = Str(internet, "homeEmail"), OtherEmail = Str(internet, "otherEmail"), HomePage = Str(internet, "homePage"),
                UserDn = Str(detail, "userDN") ?? Str(item, "userDN"), DistinguishedName = Str(detail, "distinguishedName"),
                Description = Str(detail, "description"),
                ExpiryDate = Epoch(detail, "expiryDate"), LastSuccessfulLoginDate = Epoch(detail, "lastSuccessfulLoginDate"),
                PasswordNeverExpires = Bool(detail, "passwordNeverExpires"),
                ChangePassOnNextLogon = Bool(detail, "changePassOnNextLogon") ?? Bool(detail, "changePasswordOnTheNextLogon"),
                Authorizations = authorizations
            });
        });
        snapshot.Users.AddRange(users.OrderBy(u => u.Id));
        snapshot.Warnings.AddRange(userWarnings.OrderBy(w => w, StringComparer.Ordinal));

        foreach (var item in await GetPagedAsync(token, "UserGroups?includeMembers=true", ct))
        {
            var id = Long(item, "id");
            var name = Str(item, "groupName");
            if (id is null || name is null) continue;
            var memberIds = new List<long>();
            var memberNames = new Dictionary<long, string>();
            foreach (var member in Arr(item, "members"))
            {
                if (Long(member, "id") is not { } mid) continue;
                memberIds.Add(mid);
                if ((Str(member, "username") ?? Str(member, "UserName")) is { } memberName) memberNames[mid] = memberName;
            }
            snapshot.Groups.Add(new PvwaGroup(id.Value, name, Str(item, "groupType"), Str(item, "location"), Str(item, "description"), memberIds)
            {
                Directory = Str(item, "directory"), Dn = Str(item, "dn"), MemberNames = memberNames
            });
        }

        if (options is not null) await ReadApplicationsAsync(token, snapshot, ct);
        return snapshot;
    }

    /// <summary>D-282: the authentication types whose value is kept (case-insensitive); a hash and any other type keep only the type.</summary>
    internal static readonly IReadOnlySet<string> ApplicationAuthValueKept =
        new HashSet<string>(["machineAddress", "osUser", "path", "certificateSerialNumber"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// D-282: CyberArk Applications and their authentication methods, from the classic Applications calls (one for the
    /// list, one per application). Both need Audit Users; a refused list is one warning and no applications (the Load
    /// then keeps the ones it has), a refused application keeps that one's methods as they were. PVWA refuses + &amp; %
    /// in a URL value, so such an application's methods aren't asked for.
    /// </summary>
    private async Task ReadApplicationsAsync(string token, PvwaSnapshot snapshot, CancellationToken ct)
    {
        List<JsonElement> list;
        try
        {
            list = ArrayOf(await GetJsonAsync(token, "../WebServices/PIMServices.svc/Applications/", ct), "application");
        }
        catch (PvwaException ex)
        {
            snapshot.Warnings.Add($"CyberArk Applications not read ({ex.Message}); the PVWA user needs the Audit Users authorization.");
            return;
        }

        var apps = list.Where(a => !string.IsNullOrWhiteSpace(Str(a, "AppID"))).ToList();
        var read = new ConcurrentDictionary<string, IReadOnlyList<PvwaApplicationAuthentication>>(StringComparer.Ordinal);
        var problems = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(apps, new ParallelOptions { MaxDegreeOfParallelism = UserDetailParallelism, CancellationToken = ct }, async (app, innerCt) =>
        {
            var appId = Str(app, "AppID")!;
            if (HasClassicReservedChars(appId)) { problems.Add($"{appId} (its name has + & or %)"); return; }
            try
            {
                var root = await GetJsonAsync(token, $"../WebServices/PIMServices.svc/Applications/{Uri.EscapeDataString(appId)}/Authentications/", innerCt);
                read[appId] = ArrayOf(root, "authentication").Select(a =>
                {
                    var type = Str(a, "AuthType") ?? "";
                    return new PvwaApplicationAuthentication(Str(a, "authID") ?? Long(a, "authID")?.ToString(CultureInfo.InvariantCulture), type,
                        ApplicationAuthValueKept.Contains(type) ? Str(a, "AuthValue") : null, Str(a, "Comment"))
                    {
                        IsFolder = Bool(a, "IsFolder"), AllowInternalScripts = Bool(a, "AllowInternalScripts"),
                        Subject = Str(a, "Subject"), Issuer = Str(a, "Issuer"), SubjectAlternativeName = Str(a, "SubjectAlternativeName")
                    };
                }).ToList();
            }
            catch (PvwaException ex)
            {
                problems.Add($"{appId} ({ex.Message})");
            }
        });

        foreach (var app in apps)
        {
            var appId = Str(app, "AppID")!;
            var authenticationsRead = read.TryGetValue(appId, out var authentications);
            snapshot.Applications.Add(new PvwaApplication(appId, Str(app, "Description"), Str(app, "Location"), Bool(app, "Disabled"),
                Str(app, "ExpirationDate") ?? Long(app, "ExpirationDate")?.ToString(CultureInfo.InvariantCulture))
            {
                AccessPermittedFrom = (int?)Long(app, "AccessPermittedFrom"), AccessPermittedTo = (int?)Long(app, "AccessPermittedTo"),
                AllowExtendedAuthenticationRestrictions = Bool(app, "AllowExtendedAuthenticationRestrictions"),
                BusinessOwnerFirstName = Blank(Str(app, "BusinessOwnerFName")), BusinessOwnerLastName = Blank(Str(app, "BusinessOwnerLName")),
                BusinessOwnerEmail = Blank(Str(app, "BusinessOwnerEmail")), BusinessOwnerPhone = Blank(Str(app, "BusinessOwnerPhone")),
                AuthenticationsRead = authenticationsRead, Authentications = authentications ?? []
            });
        }
        snapshot.ApplicationsRead = true;
        if (!problems.IsEmpty)
        {
            var sorted = problems.OrderBy(p => p, StringComparer.Ordinal).ToList();
            snapshot.Warnings.Add($"{sorted.Count} CyberArk Application(s): authentication methods not read; earlier ones kept. "
                + string.Join("; ", sorted.Take(5)) + (sorted.Count > 5 ? "; ..." : ""));
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Safe, account and member lists return <c>{value: [...]}</c>; offset and limit page through them.</summary>
    private async Task<List<JsonElement>> GetPagedAsync(string token, string path, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var sep = path.Contains('?') ? '&' : '?';
        for (var offset = 0; ; offset += PageSize)
        {
            var root = await GetJsonAsync(token, $"{path}{sep}limit={PageSize}&offset={offset}", ct);
            var page = ArrayOf(root, "value");
            all.AddRange(page);
            if (page.Count < PageSize) return all;
            if (offset > 10_000_000) throw new PvwaException($"PVWA '{path}' kept returning full pages; stopped after 10 million rows.");
        }
    }

    /// <summary>Platforms and users come wrapped as <c>{Platforms: [...], Total}</c> / <c>{Users: [...], Total}</c> and are not paged here.</summary>
    private async Task<List<JsonElement>> GetArrayAsync(string token, string path, string property, CancellationToken ct) =>
        ArrayOf(await GetJsonAsync(token, path, ct), property);

    /// <summary>
    /// The plain user list, not <c>ExtendedDetails=true</c>: on the lab PVWA the extended list left out the built-in Master
    /// user (id 0) while its Total still counted it (44 reported, 43 returned), and the extra it adds (group
    /// memberships, the LDAP DN) isn't used. If the Total still exceeds what came back, say so as a warning: users
    /// are only added or updated, so a short list loses nothing already loaded.
    /// </summary>
    private async Task<(List<JsonElement> Users, string? Note)> GetUsersAsync(string token, CancellationToken ct)
    {
        var root = await GetJsonAsync(token, "Users", ct);
        var users = ArrayOf(root, "Users");
        string? note = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Total", out var total) && total.TryGetInt32(out var count) && count > users.Count)
        {
            note = $"PVWA reported {count} users but returned {users.Count}; {count - users.Count} user(s) it counts weren't in the list (users above the service user's level in the Vault hierarchy aren't returned).";
        }
        return (users, note);
    }

    private sealed record TargetSettings(bool? RequireReason, bool? VerifyOnAdd, bool? ChangeOnAdd, bool? ResetMode);

    /// <summary>
    /// D-270: Platforms/Targets has a few settings the Platforms list lacks (reason for access, verify and change on
    /// add, reset mode), in PascalCase. Only target platforms are listed; a refused call loses only these settings.
    /// </summary>
    private async Task<Dictionary<string, TargetSettings>> GetTargetPlatformsAsync(string token, List<string> warnings, CancellationToken ct)
    {
        var map = new Dictionary<string, TargetSettings>(StringComparer.OrdinalIgnoreCase);
        List<JsonElement> items;
        try
        {
            items = await GetArrayAsync(token, "Platforms/Targets", "Platforms", ct);
        }
        catch (PvwaException ex)
        {
            warnings.Add($"Target platform settings not read ({ex.Message}).");
            return map;
        }
        foreach (var item in items)
        {
            if (Str(item, "PlatformID") is not { } id) continue;
            var policy = Obj(item, "CredentialsManagementPolicy");
            map[id] = new TargetSettings(
                Bool(Obj(Obj(item, "PrivilegedAccessWorkflows"), "RequireUsersToSpecifyReasonForAccess"), "IsActive"),
                Bool(Obj(policy, "Verification"), "AutoOnAdd"), Bool(Obj(policy, "Change"), "AutoOnAdd"),
                Bool(Obj(policy, "SecretUpdateConfiguration"), "ChangePasswordInResetMode"));
        }
        return map;
    }

    /// <summary>
    /// CPM activity codes (PAM 14.2 EVD action codes): verify, change and reconcile, their failures, and the SSH key
    /// verify and reconcile codes.
    /// </summary>
    internal static readonly IReadOnlySet<int> CpmActivityCodes = new HashSet<int> { 22, 38, 24, 57, 31, 60, 414, 415, 419 };

    /// <summary>
    /// The account activity call is the classic (1st gen) one, documented from 12.2 to 15.2; the 2nd gen
    /// API/Accounts/{id}/Activities answered on the lab's 14.6 but has no documentation, so it isn't used.
    /// Its times are text ("10/20/2026 00:24:45"); on the lab they matched the UTC epoch times of the same events.
    /// </summary>
    private async Task ReadCpmActivityAsync(string token, PvwaSnapshot snapshot, CancellationToken ct)
    {
        var indexes = Enumerable.Range(0, snapshot.Accounts.Count).Where(i => snapshot.Accounts[i].CpmStatus is not null).ToList();
        var found = new ConcurrentDictionary<int, IReadOnlyList<PvwaCpmActivity>>();
        var warnings = new ConcurrentBag<string>();
        var unreadableTimes = 0;
        await Parallel.ForEachAsync(indexes, new ParallelOptions { MaxDegreeOfParallelism = UserDetailParallelism, CancellationToken = ct }, async (i, innerCt) =>
        {
            var account = snapshot.Accounts[i];
            try
            {
                var root = await GetJsonAsync(token, $"../WebServices/PIMServices.svc/Accounts/{account.Id}/Activities/", innerCt);
                var activities = new List<PvwaCpmActivity>();
                foreach (var a in ArrayOf(root, "GetAccountActivitiesSlashResult"))
                {
                    if (Long(a, "ActivityCode") is not { } code || !CpmActivityCodes.Contains((int)code)) continue;
                    var time = ClassicTime(Str(a, "Time"));
                    if (time is null && Str(a, "Time") is not null) Interlocked.Increment(ref unreadableTimes);
                    activities.Add(new PvwaCpmActivity((int)code, Str(a, "Activity"), time, Str(a, "UserName"), Str(a, "Reason"), Str(a, "MoreInfo")));
                }
                // The latest of each code: what the stage rules need, and what "last verified" means.
                found[i] = activities.GroupBy(a => a.Code).Select(g => g.OrderByDescending(a => a.Time ?? DateTime.MinValue).First()).OrderBy(a => a.Code).ToList();
            }
            catch (PvwaException ex)
            {
                warnings.Add($"Account '{account.Id}': CPM activity not read ({ex.Message}).");
            }
        });
        foreach (var (i, activity) in found) snapshot.Accounts[i] = snapshot.Accounts[i] with { CpmActivity = activity };
        snapshot.Warnings.AddRange(warnings.OrderBy(w => w, StringComparer.Ordinal));
        if (unreadableTimes > 0) snapshot.Warnings.Add($"{unreadableTimes} CPM activit{(unreadableTimes == 1 ? "y has" : "ies have")} a time that couldn't be read; kept without it.");
    }

    /// <summary>
    /// D-280: DeviceType, CreationMethod and the linked accounts, which REST doesn't return ("Internal properties are
    /// not returned"), from the classic account search: one call per account, so only for an account not read before
    /// or whose CategoryModificationTime changed (on the lab that time moved with property edits that left the
    /// password's lastModifiedTime alone). An account that isn't found or whose call fails keeps what an earlier pull
    /// read, and is tried again next time; failures are counted in one warning, not one each.
    /// </summary>
    private async Task ReadAccountDetailsAsync(string token, PvwaSnapshot snapshot, PvwaPullOptions options, CancellationToken ct)
    {
        var cached = options.CachedDetails!;
        var indexes = Enumerable.Range(0, snapshot.Accounts.Count).Where(i =>
        {
            var a = snapshot.Accounts[i];
            return a.CategoryModificationTime is null || !cached.TryGetValue(a.Id, out var c) || c.CategoryModificationTime != a.CategoryModificationTime;
        }).ToList();
        var found = new ConcurrentDictionary<int, PvwaAccountDetail>();
        var problems = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(indexes, new ParallelOptions { MaxDegreeOfParallelism = UserDetailParallelism, CancellationToken = ct }, async (i, innerCt) =>
        {
            var account = snapshot.Accounts[i];
            try
            {
                var detail = await ReadAccountDetailAsync(token, account, innerCt);
                if (detail is null) { problems.Add($"{account.Id} (not found by user name and address)"); return; }
                found[i] = detail;
                options.FreshDetails.Add(detail);
            }
            catch (PvwaException ex)
            {
                problems.Add($"{account.Id} ({ex.Message})");
            }
        });
        for (var i = 0; i < snapshot.Accounts.Count; i++)
        {
            var a = snapshot.Accounts[i];
            if (found.TryGetValue(i, out var fresh)) snapshot.Accounts[i] = a with { Detail = fresh, DetailReadThisPull = true };
            else if (cached.TryGetValue(a.Id, out var old)) snapshot.Accounts[i] = a with { Detail = old };
        }
        if (!problems.IsEmpty)
        {
            var list = problems.OrderBy(p => p, StringComparer.Ordinal).ToList();
            snapshot.Warnings.Add($"{list.Count} account(s): device type, creation method and linked accounts not read; earlier values kept, tried again next pull. "
                + string.Join("; ", list.Take(5)) + (list.Count > 5 ? "; ..." : ""));
        }
    }

    /// <summary>
    /// The classic search takes keywords and a Safe and returns up to 10 accounts; the one whose AccountID is the REST
    /// id is used. On the lab the user name and address found it every time and the account's name never did, so
    /// those are tried, then each alone. PVWA refuses + &amp; % in these values even encoded, so such a value isn't sent.
    /// </summary>
    private async Task<PvwaAccountDetail?> ReadAccountDetailAsync(string token, PvwaAccount account, CancellationToken ct)
    {
        if (HasClassicReservedChars(account.SafeName)) return null;
        var attempts = new[] { $"{account.UserName} {account.Address}".Trim(), account.UserName?.Trim(), account.Address?.Trim() }
            .Where(k => !string.IsNullOrEmpty(k) && !HasClassicReservedChars(k)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var keywords in attempts)
        {
            var root = await GetJsonAsync(token,
                $"../WebServices/PIMServices.svc/Accounts?Keywords={Uri.EscapeDataString(keywords!)}&Safe={Uri.EscapeDataString(account.SafeName)}", ct);
            foreach (var item in ArrayOf(root, "accounts"))
            {
                if (!string.Equals(Str(item, "AccountID"), account.Id, StringComparison.Ordinal)) continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // The documentation's example spells it "Internal Properties"; the lab sends InternalProperties.
                foreach (var list in new[] { "Properties", "InternalProperties", "Internal Properties" })
                {
                    foreach (var pair in Arr(item, list))
                    {
                        if (Str(pair, "Key") is { } key && Str(pair, "Value") is { Length: > 0 } value) values.TryAdd(key, value);
                    }
                }
                string? V(string key) => values.GetValueOrDefault(key);
                return new PvwaAccountDetail(account.Id, account.CategoryModificationTime, V("DeviceType"), V("CreationMethod"),
                    V("ExtraPass1Safe"), V("ExtraPass1Folder"), V("ExtraPass1Name"), V("ExtraPass3Safe"), V("ExtraPass3Folder"), V("ExtraPass3Name"));
            }
        }
        return null;
    }

    private static bool HasClassicReservedChars(string value) => value.IndexOfAny(['+', '&', '%']) >= 0;

    private static readonly string[] ClassicTimeFormats = ["MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt"];

    private static DateTime? ClassicTime(string? text) =>
        text is not null && DateTime.TryParseExact(text.Trim(), ClassicTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

    private static List<JsonElement> ArrayOf(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Array) return [.. root.EnumerateArray()];
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { property, "value", "Value" })
            {
                if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array) return [.. array.EnumerateArray()];
            }
        }
        return [];
    }

    private async Task<JsonElement> GetJsonAsync(string token, string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiRoot, relative));
        request.Headers.TryAddWithoutValidation("Authorization", token);
        using var response = await SendAsync(request, $"read '{relative.Split('?')[0]}'", ct);
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PvwaException($"PVWA returned something that isn't JSON for '{relative.Split('?')[0]}'.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PvwaException($"Couldn't reach PVWA to {what}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PvwaException($"PVWA didn't answer in time to {what}.", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = (int)response.StatusCode;
        string detail = "";
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            detail = Str(doc.RootElement, "ErrorMessage") ?? Str(doc.RootElement, "errorMessage") ?? Str(doc.RootElement, "Details") ?? "";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* not JSON: leave the detail out */ }
        response.Dispose();
        var reason = response.StatusCode == HttpStatusCode.Unauthorized ? "the credential was refused" : detail;
        throw new PvwaException($"PVWA refused to {what} ({status}){(reason.Length > 0 ? ": " + reason : "")}.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>Epoch seconds as UTC; null for a missing value, zero or less, or a date PVWA uses as "never" (year 1).</summary>
    private static DateTime? Epoch(JsonElement e, string name)
    {
        var seconds = Long(e, name);
        if (seconds is null or <= 0 || seconds > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
    }

    /// <summary>An epoch time in seconds, milliseconds or microseconds (a Safe's lastModificationTime is microseconds), told apart by size.</summary>
    private static DateTime? EpochAnyUnit(JsonElement e, string name)
    {
        var value = Long(e, name);
        if (value is null or <= 0) return null;
        var seconds = value.Value switch { > 100_000_000_000_000 => value.Value / 1_000_000, > 100_000_000_000 => value.Value / 1_000, _ => value.Value };
        return seconds > 253402300799 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    /// <summary>A nested object, or an undefined element (which every helper here reads as absent).</summary>
    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>The non-blank strings of an array property.</summary>
    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        Arr(e, name).Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())).Select(v => v.GetString()!.Trim());
}
