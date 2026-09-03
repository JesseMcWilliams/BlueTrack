# Web Interface Design Document — Secrets Storage Architecture

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how sensitive values — OIDC client secrets, SAML signing certificates, and anything else a provider configuration needs — are stored and retrieved, kept modular the same way authentication providers are, so the backend can be swapped or run in parallel without changing how the rest of the application asks for a secret.

## Design Principles

- The database (`identity_provider_config.SecretReference`) never holds a raw secret value — only a reference/pointer that a pluggable secret-store component resolves at runtime.
- A single internal abstraction (conceptually, an `ISecretStore` interface: given a reference, return the secret) is implemented once per backend, so the rest of the app never needs to know which backend is active.
- Which backend is active is itself configuration, following the same enable/configure pattern as identity providers.

## Terminology Note

"OS Keyring" is typically a cross-platform/Linux-macOS term (GNOME Keyring, macOS Keychain). Since this application is IIS/Windows-only, the direct Windows equivalent is Windows Credential Manager — used by name throughout the rest of this document instead of the more generic "keyring" term.

## Confirmed Secret Store Backends

The modular backend list has been finalized to the following six — the `ISecretStore` abstraction described above is what makes supporting all six without the rest of the app needing to know which one is active.

| Backend | What It Is | Notes |
|---|---|---|
| Azure Key Vault | Cloud-hosted secrets manager | Centralized, auditable, works across a farm without extra design; requires network/identity access to Azure from IIS |
| AWS Secrets Manager | AWS-hosted secret storage/rotation service | Confirmed 2026-08-27 to specifically mean AWS Secrets Manager, not AWS KMS (see resolution below) |
| Windows DPAPI | Windows Data Protection API encrypts a value to a machine or user context | Simple, no external dependency; does not work across an IIS farm without the DPAPI-NG variant (see below) |
| CyberArk CCP (Central Credential Provider) | Centralized, API-style credential retrieval | Unified with CP and Conjur behind one modular provider interface (see below) |
| CyberArk CP (Credential Provider) | Agent-based local credential cache, typically used for application-to-application retrieval | Unified with CCP and Conjur behind one modular provider interface (see below) |
| CyberArk Conjur | CyberArk's API/SDK-driven secrets management platform | Unified with CCP and CP behind one modular provider interface (see below) |

### AWS Naming — Resolved

**Resolved 2026-08-27:** "AWS Key Store" specifically means **AWS Secrets Manager** (secret storage/rotation), not AWS KMS (encryption key management). (D-15)

### CyberArk CCP / CP / Conjur — Modular Provider Architecture

**Resolved 2026-08-27:** CCP, CP, and Conjur are genuinely different retrieval mechanisms (API call, local agent cache, and SDK-driven respectively), but they will be unified behind **one modular provider interface**: the application makes a standard request to an external module, and that module returns a standardized object the app can consume — regardless of which of the three backends is actually behind it. (D-16)

This keeps the `ISecretStore` abstraction from needing to know CyberArk-specific details; the CyberArk-specific logic (which of CCP/CP/Conjur to call, and how) lives entirely inside the external module.

**Resolved 2026-08-27:** CyberArk **CP (Credential Provider)** is the backend that gets built and enabled first (D-32) — resolves Q-05 for the CyberArk options specifically (Azure/AWS/DPAPI sequencing relative to CP is still open, see Recommendation below). **Still deferred:** the exact request/response contract for CP itself (API/agent-cache call shape, authentication to CyberArk) still needs a conversation with your CyberArk platform team — narrowed from all three backends down to just CP, but not resolved by today's session (D-33, narrows former Q-08).

## Certificate Handling Alongside DPAPI

Windows DPAPI is well suited to encrypting a generic secret value (a client secret, an API key). A SAML signing certificate is a different kind of artifact — a private key plus a certificate chain. **Resolved 2026-08-27:** certificates are stored in the Windows Certificate Store where possible, rather than as a DPAPI-encrypted blob — the Certificate Store sits alongside DPAPI as the certificate-specific mechanism, not folded into it. (D-34) Fully resolves former Q-07.

## Web Farm Consideration for DPAPI-Family Options

Plain DPAPI encrypts to a single machine (or a single user profile on that machine). A secret encrypted via DPAPI on one IIS server will not decrypt on a second IIS server — a real problem the moment this application runs on more than one node for redundancy or load balancing.

Two ways to address this if a multi-server deployment is in scope: DPAPI-NG with an Active Directory protection descriptor (group-scoped rather than machine-scoped encryption), or ASP.NET Core's Data Protection API pointed at shared key storage (a UNC path or a database-backed key ring) so every node in the farm can decrypt what any other node encrypted.

**Resolved:** the development environment is a single server (SQL Server and IIS combined) — plain Windows DPAPI is viable without the AD-scoped DPAPI-NG variant. Revisit if a production topology later introduces a farm. (D-09)

**Disaster recovery gap — noted 2026-08-27 (D-65), not just a farm concern:** D-09 only evaluated DPAPI against "single server vs. farm." A backup/DR review surfaced a related but distinct risk: DPAPI ciphertext is bound to the *originating machine*, not just to "more than one node at a time." Even a single-server deployment can't survive disaster recovery to *different hardware* — a full database backup restored onto a new box would still have `SecretReference` pointers, but the DPAPI-encrypted values behind them would be permanently undecryptable. **Decision:** keep DPAPI as the first backend built (D-36) regardless — it still unblocks development now at zero setup cost — but this is now a **documented, accepted risk** rather than an unexamined one. Revisit before Prod if disaster-recovery-to-new-hardware is an actual requirement for this environment (at which point DPAPI-NG, or sequencing a portable backend like Azure Key Vault/AWS Secrets Manager/CyberArk CP ahead of DPAPI, would need reconsidering).

## Data Model

| Field | Type | Purpose |
|---|---|---|
| SecretStoreKey | int, PK | Surrogate key |
| BackendType | text, controlled list | AzureKeyVault / AwsSecretsManager / WindowsDpapi / CyberArkCCP / CyberArkCP / CyberArkConjur |
| IsActive | bit | Which backend is currently in use — designed to allow exactly one active backend at a time, though the abstraction doesn't prevent supporting more than one simultaneously if that's ever needed |
| BackendSettings | structured | Backend-specific connection info (e.g., Key Vault URI, Vault namespace) — itself non-secret configuration |

**Implementation note (D-72, 2026-09-01):** this table was fully specified above but never actually added to the schema until the Secrets Store Configuration admin page was built — found as an implementation-time gap, not a design-phase review. Added via `14_BlueTrack_SecretsStoreSchema.sql`, matching this Data Model exactly, seeded with all six backends and `WindowsDpapi` active by default (D-36). `SecretsStoreController`/`SecretsStoreRepository` manage this record only — "exactly one active" is enforced at the application layer, not a database constraint. **No actual backend (Windows DPAPI, CyberArk CP, or any other) is implemented anywhere in the app** — this is configuration data only, same as how the Identity Providers admin page can store an OIDC/SAML row without those providers actually being wired to authenticate anyone.

## Recommendation

**Resolved 2026-08-27:** **Windows DPAPI is the first backend built overall** (D-36) — no external dependency, works immediately in the confirmed single-server dev environment (D-09). **CyberArk CP is the designated first CyberArk backend** (D-32), built after DPAPI. Where Azure Key Vault and AWS Secrets Manager fall in the order after that is not yet decided, but is no longer blocking — DPAPI unblocks development now. This fully resolves Q-05.

## CP Integration Details

Answers gathered 2026-08-27 from the platform team, including a working PowerShell example and CyberArk's official Application Provider Messages reference. This fully resolves Q-08.

- **Topology:** the Credential Provider is installed **locally on the app server itself** (not a remote/network call) — consistent with the earlier design principle that DPAPI-style local mechanisms are viable in the confirmed single-server environment (D-09). **One or more CPs can be used** (a multi-CP setup is supported, presumably for redundancy) — multi-CP routing/failover is treated as handled beneath the SDK/CP layer itself, not something BlueTrack's application code manages directly.
- **Availability expectations — resolved 2026-08-27 (D-49):** CP is a locally-installed service expected to be up **99.99% of the time**.
- **App-level resilience strategy — resolved 2026-08-27 (D-49):** in addition to CP's own internal caching (below), BlueTrack **also caches the last successfully-fetched secret at the application level**. The pattern is fetch-first, cache-as-fallback: always attempt a live `GetPassword` call first (favoring freshness), and only fall back to the cached value if that call times out or fails (per the connectivity/transient categories in the error-handling table below). Exact cache storage mechanics (in-memory only vs. persisted, expiry policy for the fallback copy) are an implementation detail to settle during build, not decided here — but it should hold a live secret in application memory, so it needs the same care as any in-memory credential handling.
- **Authentication to CP — resolved, and simpler than expected:** this is **entirely configured on the CyberArk side**, not something BlueTrack's application code implements or chooses. CP supports five methods (None/host-trusted, Path, Hash, Host, OS User) but which one(s) apply to this app is set up by the platform team when they register BlueTrack's identity in CyberArk — there is nothing here for this design document to decide. (D-38)
- **Call shape — resolved, then corrected 2026-09-01 (D-76) once the actual SDK was integrated:** CP is called via a local **.NET SDK**, not a REST/network call. The real, installed assembly is **`NetStandardPasswordSDK.dll`** (targets .NET Standard 2.0, confirmed by reflecting the DLL directly — compatible with this app's `net10.0`), namespace **`CyberArk.AAM.NetStandardPasswordSDK`** — not the originally-assumed `NetPasswordSDK.dll`/`CyberArk.AIM.NetPasswordSDK` (the older AIM-branded assembly, also present on this box but not what's referenced). The real pattern, confirmed against the actual SDK's reflected API rather than the platform team's PowerShell example alone:
  1. Build a `PSDKPasswordRequest` object.
  2. Set its typed properties directly — `AppID`, `Safe`, `Folder`, `Object` — **not** a combined `Query` string via `SetAttribute` as originally assumed (that mechanism may still exist for the older SDK/other call shapes, but isn't what this app uses).
  3. Call the static method `PasswordSDK.GetPassword(request)`, which returns a `PSDKPassword`.

  **Resolved 2026-08-27:** `Folder` is always included, defaulting to `root` when the object isn't in a subfolder. (D-40)
- **Response shape — resolved, then corrected 2026-09-01 (D-76):** `PSDKPassword` exposes the retrieved secret as **`Content`** (a `char[]`) — not `SecureContent` as originally assumed — plus direct properties `UserName`, `Address`, `Database`, `PolicyID`, a `GetAttribute(string)` method for anything else, and a `PasswordProperties` hashtable (confirmed to hold only non-secret metadata keys — `PolicyID`, `Folder`, `Safe`, `Object`, `UserName`, `Address`, `DeviceType`, `CreationMethod`, `RetriesCount`, `ResetImmediately` — no separate content key). No distinct `PasswordChangeInProgress` boolean property was found on the real type; that condition surfaces as a `PSDKRequestFailedOnPasswordChange` exception instead (a subclass of `PSDKException`). (D-39)
- **Error handling — resolved 2026-08-27 (D-48):** confirmed from CyberArk's official Application Provider Messages reference (`Reference/Application Provider Messages _ Idira Docs.md`). The SDK surfaces failures as CyberArk error codes (e.g. `APPAP004E`); the app should categorize the codes it needs to react to distinctly rather than treat every failure the same:
  | Category | Codes (examples) | App behavior |
  |---|---|---|
  | Not found | `APPAP004E`, `APPAP249E`, `APPAP324E` | Treat as a configuration problem (wrong Safe/Object/Folder) — log and alert an admin, don't retry |
  | Access denied / auth failure | `APPAP008E`, `APPAP087E`, `APPAP132E`, `APPAP133E` | This app's registration in CyberArk (App ID, Path/Hash/Host/OS User restriction) doesn't match how it's actually running — log and alert an admin, don't retry |
  | Ambiguous query | `APPAP227E`–`APPAP230E`, `APPAP251E` | More than one password object matches — since `Folder` is always specified (D-40) this should be rare; log and alert if it happens |
  | Password change in progress | `APPAP282E`, `APPAP286E` | Transient — retry with backoff rather than fail immediately |
  | Vault/CP connectivity | `APPAP007E`/`APPBC007E`, `APPAP096W`, `APPAP289E`, `APPAP291E`, `APPAP292E`, `APPAP297E` | Transient — CP itself already implements its own retry/backoff/circuit-breaker for Vault connectivity (per the reference doc), so the app mainly needs to catch these, log, alert, and surface a clear "secret temporarily unavailable" state rather than crash |
  | Anything else | e.g. `APPAP001E`, `APPAP009E` | Generic catch-all — log full detail and alert |

  **Confirmed 2026-09-01 (D-76):** the code surfaces via `PSDKException.Reason` (falling back to `.Message` if `Reason` is empty) — `CyberArkErrorClassifier` (`App/Api/Secrets/CyberArkErrorCategory.cs`) matches against this table's code lists directly.
- **Caching behavior — resolved 2026-08-27 (D-48):** the same reference doc confirms CP has its **own local caching**, configurable CP-side (`CacheLevel` = Memory or Persistent, `CacheRefreshInterval`, `VaultAccessInterval`) — this is Credential Provider configuration, not something BlueTrack's application code manages. The app can simply call `GetPassword` whenever it needs a secret and rely on CP to decide whether to serve from its cache or go to the Vault, rather than building its own caching/refresh layer on top. (BlueTrack's own fallback cache, above, is a separate, additional layer for resilience — not a replacement for this.)
- **AppID / Safe / Query naming — resolved 2026-08-27 (D-49):** these are **populated by the user/admin** (part of the per-provider `BackendSettings` configuration, consistent with how other Secrets Storage backends are configured), not hardcoded. Placeholder/default example values for this design document:
  - `AppID`: `CP_WN-DOM-000602-SQL`
  - `Safename`: `WN-DOM-000602-SQL`
  - `Query`: `Object=BlueTrack_SQL_Bind_Account`
  
  The `Query` string isn't limited to referencing `Object` — it can reference other CP query attributes instead, depending on what's being retrieved. The exact set of alternate attributes in use isn't enumerated here; `BackendSettings`/`SecretReference` should treat `Query` as a flexible, admin-configurable string rather than assuming a fixed `Safe=...;Folder=...;Object=...` shape.
- **Onboarding process / naming convention:** the example uses placeholder names (`Test_App`, `Test_Safe`, `Test_Acct`) — BlueTrack's actual `AppDescs.AppID` and Safe/Object naming are assigned by the platform team as part of onboarding, per an admin-configured value (D-49) rather than a design decision this document needs to pin down further.

## Implementation Status (added 2026-09-01, D-76)

CyberArk CP is now genuinely built and verified — the first (and so far only) real secrets-backend implementation, ahead of Windows DPAPI despite D-36 calling for DPAPI first (DPAPI still has no code at all; CP got built first because real Safe/AppID/Object values became available to test against).

- `App/Api/Secrets/ISecretsProvider.cs` — the modular interface D-16 calls for (`SecretQuery` in, `SecretResult` out), so a second backend can be added later without changing callers.
- `App/Api/Secrets/CyberArkCpSecretsProvider.cs` — the real implementation, referencing `NetStandardPasswordSDK.dll` directly from its local install path (`App/Api/BlueTrack.Api.csproj`) rather than a NuGet package or a copy committed to this repo.
- `AppID` lives in `web.secrets_store.BackendSettings` as JSON (`{"appId": "..."}`), matching D-49's "populated by the user/admin" resolution exactly. `Safe`/`Folder`/`Object` are per-call parameters, not backend config — a Vault holds many secrets, so these can't be a single fixed backend-level setting the way `AppID` is.
- D-49's fetch-first/cache-as-fallback resilience pattern is implemented: always attempt a live `GetPassword` first, fall back to the last successfully-fetched in-memory value only on a transient failure (`VaultConnectivity` or `PasswordChangeInProgress`, per the error table above).
- A "Test Connection" action exists on the Secrets Store Configuration admin page (`POST /api/admin/secrets-store/test`) — attempts a real retrieval and reports success/failure plus non-secret metadata (`UserName`/`Address`/content length), **never** the retrieved secret itself.

**Verified end to end** against a real, locally-installed Credential Provider on the dev host, with real values (`AppID: APP_BlueTrack`, `Safe: P-App-User-01`, `Folder: Root`, `Object: Operating System-WinServerLocal-WIN-K5POLANERI5.company.com-adm.jesse`) — confirmed independently via the CP's own `APPAudit.log` (`APPAU001I ... has successfully fetched password ... for application [APP_BlueTrack]`), not just BlueTrack's own success response. One real-world wrinkle worth recording: the very first two test calls returned a successful response with an *empty* password (`Content.Length == 0`) even though the CP's audit log confirmed a genuine fetch each time — not a code defect (the same code later returned real 19-character content once the account's state in the Vault actually had a password to return). Worth remembering if this ever recurs: check the account's actual state in PVWA before assuming the integration broke.

**Still not implemented:** Windows DPAPI (the design's actual "build first" backend, D-36), Azure Key Vault, AWS Secrets Manager, CyberArk CCP, and CyberArk Conjur. Nothing in the app yet actually *consumes* a secret from this provider for a real feature (e.g., binding to an external system's credential) — so far it's been built and proven end to end, but has no caller besides the admin test action.

## Open Questions

None remaining as of 2026-08-27. Q-08 is fully resolved: architecture (D-16), backend/build order (D-32, D-36), authentication (D-38), call/response shape (D-39), the `Folder` qualifier (D-40), error handling/caching (D-48), and availability/naming/app-level resilience (D-49).
