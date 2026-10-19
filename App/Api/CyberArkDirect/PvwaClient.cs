using System.Collections.Concurrent;
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

    /// <summary>Reads everything Self-Hosted Direct loads. Discovered (pending) accounts are deliberately not read.</summary>
    public async Task<PvwaSnapshot> PullAsync(string token, CancellationToken ct)
    {
        var snapshot = new PvwaSnapshot();

        foreach (var item in await GetArrayAsync(token, "Platforms", "Platforms", ct))
        {
            var general = item.TryGetProperty("general", out var g) ? g : item;
            var id = Str(general, "id");
            if (id is null) continue;
            snapshot.Platforms.Add(new PvwaPlatform(id, Str(general, "name"), Str(general, "description"), Bool(general, "active"), Str(general, "platformType")));
        }

        foreach (var item in await GetPagedAsync(token, "Safes?extendedDetails=true", ct))
        {
            var number = Long(item, "safeNumber");
            var name = Str(item, "safeName");
            var urlId = Str(item, "safeUrlId") ?? name;
            if (number is null || name is null || urlId is null) { snapshot.Warnings.Add("A Safe without a number or name was skipped."); continue; }
            snapshot.Safes.Add(new PvwaSafe(number.Value, urlId, name, Str(item, "location"),
                item.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object ? Str(creator, "name") : null,
                Epoch(item, "creationTime"), (int?)Long(item, "numberOfDaysRetention"), (int?)Long(item, "numberOfVersionsRetention")));
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
            JsonElement management = default;
            var hasManagement = item.TryGetProperty("secretManagement", out management) && management.ValueKind == JsonValueKind.Object;
            snapshot.Accounts.Add(new PvwaAccount(
                safeNumber, fileId, Str(item, "safeName") ?? "", Str(item, "name") ?? id!, Str(item, "address"), Str(item, "userName"),
                Str(item, "platformId"), Epoch(item, "createdTime"),
                hasManagement ? Epoch(management, "lastModifiedTime") ?? Epoch(management, "lastModifiedDateTime") : null,
                hasManagement ? Epoch(management, "lastVerifiedDateTime") ?? Epoch(management, "lastVerifiedTime") : null,
                hasManagement ? Bool(management, "automaticManagementEnabled") : null,
                hasManagement ? Str(management, "manualManagementReason") : null,
                properties));
        }

        // Members: one call per Safe. Without View Safe Members in a Safe the call is refused; that Safe's members
        // are missing, so say so rather than stop the pull.
        var members = new ConcurrentBag<PvwaMember>();
        var memberWarnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(snapshot.Safes, new ParallelOptions { MaxDegreeOfParallelism = MemberCallParallelism, CancellationToken = ct }, async (safe, innerCt) =>
        {
            try
            {
                foreach (var item in await GetPagedAsync(token, $"Safes/{safe.SafeUrlId}/Members", innerCt))
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
                        string.Equals(Str(item, "memberType"), "Group", StringComparison.OrdinalIgnoreCase), Epoch(item, "membershipExpirationDate"), permissions));
                }
            }
            catch (PvwaException ex) when (ex.Message.Contains("(403)"))
            {
                memberWarnings.Add($"Safe '{safe.SafeName}': members not read ({ex.Message}).");
            }
        });
        snapshot.Members.AddRange(members.OrderBy(m => m.SafeNumber).ThenBy(m => m.MemberId));
        snapshot.Warnings.AddRange(memberWarnings.OrderBy(w => w, StringComparer.Ordinal));

        // Users: the list has no email, so each user's details are read too (a failure there loses only that email).
        var usersJson = await GetUsersAsync(token, ct);
        var users = new ConcurrentBag<PvwaUser>();
        var userWarnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(usersJson, new ParallelOptions { MaxDegreeOfParallelism = UserDetailParallelism, CancellationToken = ct }, async (item, innerCt) =>
        {
            var id = Long(item, "id");
            var name = Str(item, "username");
            if (id is null || name is null) return;
            var details = item.TryGetProperty("personalDetails", out var pd) && pd.ValueKind == JsonValueKind.Object ? pd : default;
            string? first = details.ValueKind == JsonValueKind.Object ? Str(details, "firstName") : null;
            string? last = details.ValueKind == JsonValueKind.Object ? Str(details, "lastName") : null;
            string? email = null;
            try
            {
                var detail = await GetJsonAsync(token, $"Users/{id}/", innerCt);
                if (detail.TryGetProperty("internet", out var net) && net.ValueKind == JsonValueKind.Object) email = Str(net, "businessEmail");
            }
            catch (PvwaException ex)
            {
                userWarnings.Add($"User '{name}': details not read, so no email ({ex.Message}).");
            }
            users.Add(new PvwaUser(id.Value, name, Str(item, "source"), Str(item, "userType"), Str(item, "location"),
                Bool(item, "enableUser") ?? true, first, last, email));
        });
        snapshot.Users.AddRange(users.OrderBy(u => u.Id));
        snapshot.Warnings.AddRange(userWarnings.OrderBy(w => w, StringComparer.Ordinal));

        foreach (var item in await GetPagedAsync(token, "UserGroups?includeMembers=true", ct))
        {
            var id = Long(item, "id");
            var name = Str(item, "groupName");
            if (id is null || name is null) continue;
            var memberIds = new List<long>();
            if (item.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                foreach (var member in m.EnumerateArray()) { if (Long(member, "id") is { } mid) memberIds.Add(mid); }
            }
            snapshot.Groups.Add(new PvwaGroup(id.Value, name, Str(item, "groupType"), Str(item, "location"), Str(item, "description"), memberIds));
        }
        return snapshot;
    }

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

    private async Task<List<JsonElement>> GetUsersAsync(string token, CancellationToken ct)
    {
        var root = await GetJsonAsync(token, "Users?ExtendedDetails=true", ct);
        var users = ArrayOf(root, "Users");
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Total", out var total) && total.TryGetInt32(out var count) && count > users.Count)
        {
            throw new PvwaException($"PVWA reported {count} users but returned {users.Count}; the list is incomplete.");
        }
        return users;
    }

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
}
