# Web Interface Design Document — Credentials Management

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

D-116 (2026-09-08): a generic, admin-managed store for named credentials this app itself authenticates *as* -- the SMTP account, an LDAP bind account, and whatever else needs one later -- requested directly alongside role-targeted notification email and LDAP lookup (`Design_Notifications.md`'s updated Proposed Workflow) and a real local SMTP server the user provisioned to test against.

This is a deliberately different concept from `web.secrets_store` (Design_Secrets_Storage.md): that table picks the **one active backend** the whole app uses to resolve **privileged-account** secrets a vault (CyberArk, Azure Key Vault, AWS Secrets Manager) manages. `web.credential` instead lets an admin pick a backend **per named credential**, for **this app's own** service-account-style credentials -- there can be several at once (SMTP, LDAP, more later), each independently backed.

## Data Model

### web.credential

| Field | Type | Purpose |
|---|---|---|
| CredentialKey | int, PK | |
| CredentialName | nvarchar(100), unique | e.g. "SMTP Relay", "LDAP Bind Account" |
| BackendType | nvarchar(50) | `WindowsDpapi` \| `CyberArkCP` \| `CyberArkCCP` \| `CyberArkConjur` \| `AzureKeyVault` \| `AwsSecretsManager` |
| Username | nvarchar(255), nullable | DPAPI only |
| ProtectedPassword | nvarchar(2000), nullable | DPAPI only -- ciphertext |
| ScopePreference | nvarchar(10), nullable | DPAPI only -- `Machine` \| `User`, the admin's choice |
| CurrentScope | nvarchar(10), nullable | DPAPI only -- what `ProtectedPassword` is actually protected with *right now* (see below) |
| VaultSafe / VaultFolder / VaultObject | nvarchar, nullable | Vault backends only -- reuses `IVaultSecretProvider.SecretQuery`'s own Safe/Folder/Object shape, already how CyberArk CP/CCP/Conjur and (with different field meanings) Azure Key Vault/AWS Secrets Manager all interpret a reference |

Resolution (`CredentialRepository.ResolveForUseAsync`) dispatches on `BackendType`: DPAPI decrypts locally; anything else calls `VaultSecretProviderResolver.ResolveByBackendType(row.BackendType)` (a new method, independent of which backend is "active" in `web.secrets_store`) and reads `SecretResult.UserName`/`Content`.

## DPAPI Machine/User Scope

**The request, verbatim**: "Allow the user to select machine or user. If user is selected, initially store it as machine and when the application pool user uses it, the app pool will change it to user so only the app pool will have access."

**Why this matters**: `WindowsDpapiProtector` had hardcoded `DataProtectionScope.LocalMachine` for every existing caller (OIDC's ClientSecret, the old inline SMTP password, vault providers' own protected settings) -- its own comment explains why: this app runs under one dedicated app-pool service account (D-30), and Machine scope decrypts correctly regardless of whether that account's Windows profile is loaded, which `CurrentUser` scope depends on. `User` scope is *tighter* (only that one Windows identity can decrypt it, not "any process on this machine") but *fragile* if the encrypting identity's profile isn't fully loaded yet -- a real, known DPAPI/IIS gotcha.

**The mechanism, confirmed directly with the user rather than assumed**: a DPAPI credential's `ScopePreference` (the admin's choice) and `CurrentScope` (what's actually stored right now) are tracked separately. Saving a credential with `ScopePreference = User` always writes `CurrentScope = Machine` first. Every request in this app already runs under the *same one* app-pool identity (D-30) -- there's no separate "admin-interactive" identity a save-time encrypt could get wrong the way a desktop app's own interactive user session might -- so the very first successful decrypt (`CredentialRepository.ResolveForUseAsync`) re-protects the plaintext under `CurrentUser` scope and persists the upgrade, converging automatically. The re-protect attempt is wrapped in `try/catch`: a transient failure (the profile genuinely not loaded on that particular call) just leaves it at `Machine` scope, retried on the next resolve, rather than breaking the actual credential retrieval.

`ILocalSecretProtector` gained scope-aware `Protect(plaintext, CredentialScope)`/`Unprotect(protectedValue, CredentialScope)` overloads; every existing caller keeps using the original parameterless overloads (always `Machine`, unchanged behavior).

**Verified live, 2026-09-08**: created a real `WindowsDpapi` credential (the real `BlueTrack_SMTP` account) with `ScopePreference = User`. Confirmed via the API: created at `CurrentScope = Machine`; the first `POST /{key}/test` (a real decrypt) flipped it to `CurrentScope = User`, confirmed both via the redacted API response and by reading the raw ciphertext length directly in SQL (216 Base64 characters both before and after -- genuinely re-encrypted, not just relabeled); a second `test` call afterward still decrypted correctly under the new scope.

## Real SMTP Server (context for the credential test above)

The user provisioned a real local relay for this work: `192.222.222.149:25`, Windows-authenticated (`BlueTrack_SMTP` account). Two things learned from testing against it, both documented in `SmtpNotificationSender.cs`'s own comments:

- MailKit's `AuthenticateAsync(username, password)` auto-negotiates whichever SASL mechanism the server advertises (PLAIN/LOGIN/**NTLM**/CRAM-MD5/...) -- a Windows-authenticated relay needed no special-casing beyond a plain username/password credential; `AuthMethod = 'Basic'` on `notification_config` already covers it.
- The relay's real TLS certificate passed hostname/chain validation once addressed by its actual hostname (not the raw IP, which the cert isn't issued for), but had no CRL/OCSP endpoint reachable from this network. Initially fixed as a hardcoded `SmtpClient.CheckCertificateRevocation = false`, then immediately turned into two admin-editable checkboxes on the Notifications page instead (`IgnoreCrlErrors`, `IgnoreSslErrors` -- both off by default) once asked to make it a real per-environment setting rather than a permanent behavior -- see `Design_Notifications.md`'s own D-116 follow-up section for the full detail and live verification of both checkboxes independently.

A real test email was sent successfully through this relay using the DPAPI-stored, scope-upgraded credential above.

## LDAP Configuration

`web.ldap_config` (singleton, disabled by default -- same pattern as OIDC/SAML's placeholder rows): `IsEnabled`, `DomainController` (blank = default domain), `SearchBase`, `UseSsl`, and `CredentialKey` pointing at the bind-account `web.credential` row. LDAP lookups (`LdapGroupMemberResolver`, see `Design_Notifications.md`'s D-116 section for the full mechanism) are skipped entirely -- not attempted, not an error -- unless both `IsEnabled` and `CredentialKey` are set.

Managed on this same Credentials & LDAP admin page (one permission, `ManageCredentials`, covers both) -- LDAP configuration is small enough (three fields plus a picker for which Credential is the bind account) not to warrant a fully separate permission from the credential store it directly depends on.

**Verified live, 2026-09-08**: a throwaway probe (not the production code path) confirmed `System.DirectoryServices.AccountManagement` can bind to the real domain (`WIN-K5POLANERI5.Company.com`) and expand a real AD group's membership with real `mail` attributes, using only the ambient process identity -- confirming the underlying LDAP capability genuinely works in this environment. The actual production code path (requiring an explicit bind-account `Credential`) was then exercised for real too, reusing the already-verified `BlueTrack_SMTP` credential as the LDAP bind account (a real, working domain account) -- see `Design_Notifications.md`'s D-116 verification for the full end-to-end result (a real notification fired with LDAP-resolved recipients included).

## New Permissions

- `ManageCredentials` -- the Credentials & LDAP admin page (CRUD + LDAP config).
- `TriggerBackup` -- unrelated to credentials directly, but added in the same schema script (`17_BlueTrack_CredentialsLdapBackupSchema.sql`) for the Deployment page's new Backup App button; see `Design_Admin_Deployment_Management.md` §3.4.

Both explicitly granted to the bootstrap Admin role, matching the established D-98/D-115 pattern for any permission added after `09_BlueTrack_WebSeed.sql`'s own one-time bootstrap grant already ran.

## D-118 (2026-09-08 follow-up): WindowsDpapi as a real Secrets Store backend, and LDAP trusted connections

Two more direct requests once PR #21 merged: (1) "WindowsDpapi should be a secrets backend. May need a wrapper for this" -- prompted by the Secrets Store health check (Design_Admin_Deployment_Management.md §3.2) reporting Unhealthy whenever WindowsDpapi was the active `web.secrets_store` backend, since no `IVaultSecretProvider` had ever been registered for it; (2) "LDAP should be able to use a trusted connection using the application pool or the local computer account" -- an alternative to always requiring an explicit bind-account credential.

### WindowsDpapi as `IVaultSecretProvider`

A genuinely different code path from `ILocalSecretProtector`/`WindowsDpapiProtector` (D-79's original split deliberately kept those separate -- see that interface's own comment, unchanged), not a replacement for it. `WindowsDpapiVaultSecretsProvider` is a thin adapter: `SecretQuery.Object` names a `web.credential` row (`BackendType = 'WindowsDpapi'`) directly by `CredentialName`; `Safe`/`Folder` are accepted (every backend still takes the same three-field query) but ignored, since a local DPAPI store is a flat namespace, not a hierarchical vault.

**A circular DI dependency had to be designed around**: `CredentialRepository` depends on `VaultSecretProviderResolver` (to resolve non-DPAPI credentials), which enumerates every registered `IVaultSecretProvider` -- now including the new DPAPI one. If that new provider depended on `CredentialRepository` itself (the obvious first instinct, since it needs the same decrypt+scope-upgrade logic), the container would never resolve: `CredentialRepository` → `VaultSecretProviderResolver` → `IVaultSecretProvider` → `WindowsDpapiVaultSecretsProvider` → `CredentialRepository`. Fixed by extracting the actual DPAPI decrypt/Machine-to-User-upgrade mechanism (previously inline in `CredentialRepository.ResolveForUseAsync`) into a new standalone `DpapiCredentialResolver`, depending only on `IDbConnectionFactory`/`ILocalSecretProtector` -- both `CredentialRepository` (resolving by key) and `WindowsDpapiVaultSecretsProvider` (resolving by name) delegate to it now, with no cycle.

**Verified live**: created a real DPAPI credential, then exercised it through `web.secrets_store`'s own "Test Connection" (Object = the credential's name) -- succeeded, returning the correct username and password length; an unknown name returned a clean `NotFound` failure, not an error. The Secrets Store health check flipped from Unhealthy ("no provider implementation exists for it yet") to Healthy ("Active backend 'WindowsDpapi' has a provider implementation") with zero other changes. One existing xUnit test (`SecretsStore_TestConnection_NoProviderForActiveBackend_ReturnsFailureNotError`) asserted the old, now-fixed gap as its premise -- updated to assert the new, correct `NotFound` behavior instead, renamed accordingly.

### LDAP trusted connections

`web.ldap_config` gained `UseTrustedConnection` (bit, default 0 -- unchanged behavior for existing configuration). When set, `LdapGroupMemberResolver` binds via `PrincipalContext`'s no-credentials constructor overload instead of resolving a bind-account `Credential` -- confirmed via a live probe against this environment's real domain, empirically, before relying on it: passing explicit `null` username/password into the 5-argument overload was never verified to behave like the dedicated 3-argument (container/name only) overload, so the code selects between two distinct constructor calls rather than assuming null-handling equivalence.

**Verified live, end to end, isolating the exact mechanism**: enabled LDAP with `UseTrustedConnection = true` and `CredentialKey = null` (no bind account configured at all), with zero flat recipients and no role email set, then triggered a real role-targeted notification -- it sent successfully, which is only possible if the trusted-connection bind itself resolved real AD group members. Confirmed by checking the database directly that no other recipient source existed for that send.

### Automated test coverage added the same day

Every new/changed capability from this session (D-115 through D-118) previously had zero automated coverage, verified entirely by hand. Added: `Contract/CredentialsControllerTests.cs`, `Contract/NotificationsControllerTests.cs`, `Contract/DeploymentBackupTests.cs` (the last one triggers a real `BACKUP DATABASE` against `BlueTrackTest` on every run -- confirmed a genuine `.bak` file is produced, not a stubbed response), plus `GatedGetEndpoints`/`AdminControllersPermissionTests.cs` rows for the new admin routes; `Integration/CredentialRepositoryTests.cs` (including the Machine-to-User scope upgrade, ciphertext genuinely changing, and the WindowsDpapi vault-lookup-by-name path), `Integration/NotificationRepositoryTests.cs`, `Integration/LdapConfigRepositoryTests.cs`. New Playwright coverage for the Credentials & LDAP and Notifications admin pages was also written (`admin-pages.spec.js`) and is structurally correct (matches every established convention in that file), but could not be confirmed passing in this session -- a pre-existing, untouched test in the same file (`Group → Role Mapping`) failed identically and repeatably across multiple clean-process retries, pointing to an environmental issue on this shared dev box (consistent with this session's own previously-documented Vite-proxy/DevFakeAuth flakiness pattern) rather than a defect in the new specs. `dotnet test` 292/292 (up from 253 -- 39 new tests), Vitest 18/18.

## Implementation Status

**Implemented and verified live, D-116 (2026-09-08).** `Database/17_BlueTrack_CredentialsLdapBackupSchema.sql`; `CredentialRepository`/`LdapConfigRepository`/`CredentialsController`; `App/Api/Ldap/LdapGroupMemberResolver.cs` (this app's first use of `System.DirectoryServices`); a new `Credentials.vue` admin page.

**D-118 (same day, follow-up).** `Database/19_BlueTrack_LdapTrustedConnectionSchema.sql` (`UseTrustedConnection`); `App/Api/Secrets/DpapiCredentialResolver.cs` (extracted to avoid a circular DI dependency) and `WindowsDpapiVaultSecretsProvider.cs`, registered as a real `IVaultSecretProvider`; `LdapGroupMemberResolver` updated to bind without credentials when trusted-connection mode is on. Automated coverage added the same day across all of D-115 through D-118 (Contract, Integration, and Playwright layers) -- see that section above for the full rundown. `dotnet test` 292/292, Vitest 18/18.

Not built: a UI affordance for choosing/entering vault-backend fields (Safe/Folder/Object) has basic form support in `Credentials.vue`, but only the `WindowsDpapi` path was exercised live (the SMTP/LDAP credentials, and now the Secrets Store "Test Connection" path too, all used it) -- a vault-backed credential (CyberArk CP/CCP/Conjur/Azure/AWS) is implemented via the same `VaultSecretProviderResolver.ResolveByBackendType` used elsewhere but not separately re-verified here, since those backends' own live/placeholder status is already tracked in `Design_Secrets_Storage.md`. The new Playwright specs for Credentials/Notifications are written but unconfirmed passing in this environment -- see D-118's own note on the (pre-existing, unrelated) flakiness that blocked confirming them.
