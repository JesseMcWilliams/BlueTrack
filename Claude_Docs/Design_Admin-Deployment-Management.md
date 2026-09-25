# Web Interface Design Document — Admin Deployment Management

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Three related gaps in the Admin section, all raised together (2026-09-04):

1. **Identity Providers admin page** (`admin/IdentityProviders.vue`) manages OIDC/SAML settings as a single raw JSON textarea (`ConfigurationValues`) — an admin has to hand-write JSON matching `OidcProviderSettings`/`SamlProviderSettings`'s exact property names with no validation until save fails. Replace with structured, per-provider-type fields.
2. **Secrets Store Configuration admin page** (`admin/SecretsStoreConfiguration.vue`) has the identical problem for its five backends' `BackendSettings`.
3. **A new admin page for deployment/environment information** that doesn't exist today at all: environment name + version/build info, connectivity/health checks, and SQL Server backup status.

## Part 1: Identity Providers — structured config fields

Confirmed exact shapes from the actual settings classes (`App/Api/Models/OidcProviderSettings.cs`, `SamlProviderSettings.cs`) — not guessed:

**OIDC** (`OidcProviderSettings`): `Authority`, `ClientId`, `CallbackPath` (defaults `/signin-oidc`), `GroupsClaimType` (defaults `groups`). Client secret is separate — the existing write-only `PlaintextSecret` field (D-84) already covers it and needs no change.

**SAML** (`SamlProviderSettings`): `SpEntityId`, `SpCertificateThumbprint`, `IdpEntityId`, `IdpSingleSignOnDestination`, `IdpSingleLogoutDestination` (optional), `IdpCertificateThumbprint`, `GroupClaimType` (defaults the SOAP claim URI). Both certificate fields are Windows Certificate Store *thumbprints* (D-25/D-34) — the UI should say so directly (e.g. placeholder/help text), since a thumbprint isn't self-explanatory and there's no dynamic lookup to validate it against without adding a new endpoint (see Open Questions).

**UI change**: `IdentityProviders.vue`'s edit form switches its "Configuration Values (JSON)" textarea for a field set chosen by the selected `ProviderType` (OIDC fields when `ProviderType === 'OIDC'`, SAML fields when `'SAML'`) — the form still serializes to the same `ConfigurationValues` JSON string on save and deserializes it back on edit, so `IdentityProviderRepository`/the database column shape is completely unchanged; this is a frontend-only change plus, if useful, light validation of required fields per type before submit.

**Implemented 2026-09-04.** `IdentityProviders.vue` now renders OIDC's four fields and SAML's seven fields (both certificate fields labeled as Windows Certificate Store thumbprints, per the note above) in place of the old textarea, keyed off `editing.providerType`; a `configFields` object holds the structured values, serialized to/parsed from `ConfigurationValues` JSON on save/edit using the same camelCase-vs-PascalCase case-insensitive match every other reader in this app already relies on (`ProviderSettingsReader`'s `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`, confirmed directly, not assumed). No backend change. Verified with a new Playwright test (`admin-pages.spec.js`) asserting the structured OIDC fields actually round-trip through save → reload → re-edit, not just that the form submits. See PR #14.

## Part 1b: Completing the trust — what to give the IdP, and known gaps

**Added 2026-09-23**, after a direct documentation-completeness check found that nothing in this project's docs told an admin what BlueTrack itself exposes back to an IdP. Part 1 above only covers BlueTrack's own side of the form; setting up a real IdP also needs these values, confirmed directly against `Saml2Controller.cs`, `AuthenticationExtensions.cs`, and `IdentityProvidersHealthCheck.cs` (not guessed).

### OIDC

**What BlueTrack needs from the IdP's own app/client registration**: the **Authority** (issuer URL, e.g. `https://login.microsoftonline.com/{tenant}/v2.0` for Entra ID — already the form's own placeholder text) and a **Client ID**/**Client Secret** pair.

**What to give the IdP**: a **redirect URI**, `https://<your-BlueTrack-hostname>/BlueTrack<Callback Path>` — with the default Callback Path (`/signin-oidc`), that's `https://<your-BlueTrack-hostname>/BlueTrack/signin-oidc` (the `/BlueTrack` segment is the nested IIS Application's own mount point, D-163 — not part of the Callback Path setting itself). Register this exact URL in the IdP's app registration *before* enabling OIDC here, or the IdP will reject the callback. This isn't a BlueTrack endpoint you call directly — `Microsoft.AspNetCore.Authentication.OpenIdConnect`'s own middleware intercepts requests to this path automatically once the scheme is registered.

**A real gap, not just a documentation one**: if the Secret field is left blank (or was never actually set) while Authority/Client ID are filled in and the provider is enabled, OIDC silently fails to register at app startup — `AuthenticationExtensions.cs` logs one console line (`"...has no stored client secret...not registering the OIDC scheme"`) and nothing else. The Deployment page's health check does **not** catch this — `IdentityProvidersHealthCheck.cs` only confirms Authority/Client ID are non-blank, never that a secret was actually stored. Confirm OIDC is genuinely working by attempting a real sign-in after the required restart, not by trusting a healthy status alone.

### SAML

**What BlueTrack needs from the IdP**: the **IdP Entity ID** (Issuer) and **IdP Single Sign-On Destination** (the SSO URL), plus the **IdP Certificate Thumbprint** — the IdP's own signing certificate, which must already be installed into this server's Windows Certificate Store (`LocalMachine\My`) *before* its thumbprint is entered in the form; this is a lookup by thumbprint, never a file upload (`Saml2ConfigurationFactory.cs`'s `FindCertificate`).

**What to give the IdP**:
- **SP Entity ID / Audience URI**: whatever you enter in the **SP Entity ID** field on this page — sent as-is as the SAML `Issuer`, so it must match exactly what's registered on the IdP side.
- **ACS URL** (also called Reply URL, Single Sign-On URL, or Assertion Consumer Service URL depending on the vendor): `https://<your-BlueTrack-hostname>/BlueTrack/api/auth/saml/acs` — a fixed route (`Saml2Controller.cs`'s `Acs` action), not configurable. The `/BlueTrack` segment is the nested IIS Application's own mount point (D-163); the `api/auth/saml/acs` part is the controller's own route.
- **SP metadata**, if the IdP can import it directly instead of each field being entered by hand: `GET https://<your-BlueTrack-hostname>/BlueTrack/api/auth/saml/metadata` — returns real, signed SP metadata (including the ACS URL above, and BlueTrack's own SP signing certificate if **SP Certificate Thumbprint** is set) once a SAML provider row is enabled with all required fields populated; a 503 otherwise.

**Known limitation, stated plainly**: this integration has been verified structurally — genuine signed SP metadata generated, a correctly-formed and signed `AuthnRequest` produced, using a real self-signed certificate installed for the test (see `Design_Authentication-Architecture.md`'s own Implementation Status note) — but has never been round-tripped end to end against a real vendor IdP. Treat a first real setup as needing careful, hands-on verification of an actual sign-in, not an assume-it-works integration.

### Provider-specific quick reference

The concepts above map onto four commonly-requested IdPs' own terminology as follows. **Exact current menu labels and navigation are not verified against any of these vendors' live admin consoles in this pass — vendor UIs change over time, so confirm current steps against each vendor's own documentation rather than treating the table below as a verified click-path.**

| BlueTrack concept | Okta | Microsoft Entra ID (Azure AD) | Ping (PingOne / PingFederate) | Authentik |
|---|---|---|---|---|
| OIDC: registering BlueTrack | An OIDC "App Integration" (Web Application) | An "App registration" | An OIDC "Application" | An OAuth2/OIDC "Provider" plus an "Application" |
| OIDC: Authority/issuer value | The org or custom authorization server's issuer URL | `https://login.microsoftonline.com/{tenant-id}/v2.0` | The PingOne/PingFederate environment's own OIDC issuer URL | `https://{authentik-host}/application/o/{slug}/` |
| OIDC: where the redirect URI goes | "Sign-in redirect URIs" | "Redirect URI" (under Authentication) | "Redirect URIs" | "Redirect URIs" |
| OIDC: Client ID/Secret | Shown on the app integration's own overview page after creation | "Application (client) ID", with a generated secret under "Certificates & secrets" | Shown after app creation | Shown after provider creation |
| SAML: registering BlueTrack | A SAML 2.0 "App Integration" | A non-gallery "Enterprise Application" (SAML-based single sign-on) | A SAML "Application" | A SAML "Provider" |
| SAML: the ACS URL field | "Single sign-on URL" | "Reply URL (Assertion Consumer Service URL)" | "Assertion Consumer Service (ACS) URL" | "ACS URL" |
| SAML: the SP Entity ID field | "Audience URI (SP Entity ID)" | "Identifier (Entity ID)" | "SP Entity ID" / "Partner Entity ID" | "Issuer" |
| SAML: importing SP metadata | Supports pointing at/uploading SP metadata directly | Supports uploading an SP metadata file | Supports importing SP metadata by URL or file | Supports importing SP metadata by URL or file |

## Part 2: Secrets Store — structured config fields per backend

Confirmed exact shapes from the actual provider classes (each backend's private `*Settings` class in `App/Api/Secrets/`) — not guessed:

| Backend | Fields |
|---|---|
| CyberArkCP | `AppId` |
| CyberArkCCP | `BaseUrl`, `AppId` |
| CyberArkConjur | `ApplianceUrl`, `Account`, `Login`, plus a credential (API key) |
| AzureKeyVault | `VaultUri`, `AuthMethod` (`ManagedIdentity` \| `ServicePrincipal`), and only when `ServicePrincipal`: `TenantId`, `ClientId`, plus a credential (client secret) |
| AwsSecretsManager | `Region`, `AuthMethod` (`IamRole` \| `AccessKey`), and only when `AccessKey`: `AccessKeyId`, plus a credential (secret access key) |

The write-only `PlaintextCredential` field (D-84, already exists on `SetActiveSecretsStoreRequest`) covers every backend's credential — no change needed there, same "leave blank to keep the existing one" semantics.

**UI change**: `SecretsStoreConfiguration.vue`'s per-row "Settings (JSON)" textarea becomes a field set chosen by that row's own `BackendType` (five known types, a fixed `v-if`/`v-else-if` chain — there's no sixth backend to generalize for). Same non-change to the backend: still serializes to `BackendSettings` JSON on save, `SecretsStoreRepository`/the database column shape untouched.

**Implemented 2026-09-04.** Structured fields keyed by the exact PascalCase property names on each backend's own settings class (`CyberArkCpSettings`/`CyberArkCcpSettings`/the private `*Settings` classes inside `CyberArkConjurSecretsProvider.cs`/`AzureKeyVaultSecretsProvider.cs`/`AwsSecretsManagerSecretsProvider.cs`), including the `AuthMethod`-conditional fields for Azure Key Vault (`TenantId`/`ClientId` shown only for `ServicePrincipal`) and AWS Secrets Manager (`AccessKeyId` shown only for `AccessKey`). A parsed settings *object* is kept per backend rather than a raw string, and only its known fields are edited — this was a deliberate choice, not incidental: `SecretsStoreRepository.SetActiveAsync` replaces `BackendSettings` wholesale with whatever the client sends whenever no new `PlaintextCredential` is supplied, so anything not already present in that object (in practice, the redacted `"ProtectedCredential":"***"` key returned by `Redact()`) would otherwise be silently dropped from storage on the next save — parsing into an object and only touching known keys keeps existing behavior intact by construction rather than by accident. No backend change. Verified with a new Playwright test asserting the CyberArkCP App ID field round-trips through save → reload. See PR #15.

## Part 3: New Deployment admin page

### 3.1 Environment name + version/build info

- **Environment name**: `IHostEnvironment.EnvironmentName` (ASP.NET Core's own built-in concept, driven by `ASPNETCORE_ENVIRONMENT`) — already real, no new plumbing needed, and naturally lines up with D-54's Dev/Test/Staging/Prod naming as long as that variable is set to match per environment.
- **Version/build info**: this app has no existing versioning scheme (`BlueTrack.Api.csproj` has no `<Version>` today). Proposed: add an explicit `<Version>` to the csproj (even a simple `1.0.0` to start, bumped manually per real release) plus the assembly's own build timestamp (read from the compiled DLL's file metadata at runtime — no new build-pipeline step required). Not a claim of a mature release-versioning process, just enough for an admin to answer "what's actually running right now."

### 3.2 Connectivity/health checks

Built on ASP.NET Core's built-in health checks middleware (`Microsoft.Extensions.Diagnostics.HealthChecks`, part of the shared framework — no new package needed for simple custom checks), which this app doesn't use anywhere yet. Three checks, each backed by something this app can genuinely verify itself:
- **SQL Server**: a real, trivial query (`SELECT 1`) through the existing `IDbConnectionFactory` — a real connectivity check, not a stub.
- **Active Secrets Store backend**: resolve it via the existing `VaultSecretProviderResolver` and confirm a provider implementation exists for whichever backend is active — this does **not** attempt a live secret retrieval (that stays explicitly out of automated scope, same reasoning as `Design_Testing-Strategy.md`'s "Explicitly not automated" section), it only confirms the dispatch succeeds.
- **Configured identity providers**: confirm at least one provider row is enabled and, for OIDC/SAML specifically, that its required settings fields are actually populated (not a live IdP reachability check — reaching a real IdP over the network is a bigger, separate scope, consistent with this project's existing "real external IdP stays manually verified" stance).

### 3.3 SQL Server backup status

**Confirmed data source, user's explicit choice, 2026-09-04**: SQL Server's own native backup history (`msdb.dbo.backupset`), not a specific third-party tool — works regardless of which backup mechanism actually writes those rows (a maintenance plan, Ola Hallengren's scripts, a third-party tool that also updates `msdb`), since that's SQL Server's own universal backup ledger.

**A real, flagged operational risk, not silently assumed to work**: per D-30, this app's own SQL account is a deliberately least-privileged service account ("not `db_owner`, just grants scoped to what the app needs") — it almost certainly does **not** have read access to `msdb` today, since nothing in this app has ever needed it before now. This is a genuine deployment-time requirement, not something the application code can grant itself: whoever manages the SQL Server service account needs to run something equivalent to `GRANT SELECT ON msdb.dbo.backupset TO [that account];` (or add it to a suitable `msdb` role) before this feature will work in a real environment — this needs to be called out plainly in whatever setup documentation covers this feature, not discovered later as a silent failure. The endpoint itself should fail gracefully (a clear "backup history unavailable — check msdb permissions" message) rather than a raw 500 if that grant hasn't been made yet.

**Resolved 2026-09-05 (D-107): a dedicated `msdb` role, not a one-off grant, so the permission is assignable to more than just the app's own service account:**

```sql
-- Proposed -- a DBA runs this once against msdb on the real SQL Server
-- instance. Not tied to any application-owned migration, since granting
-- msdb permissions is deliberately outside what this app's own
-- (still-restricted) connection can do to itself.
USE msdb;
GO

CREATE ROLE db_backupstatus_reader;
GO

GRANT SELECT ON msdb.dbo.backupset TO db_backupstatus_reader;
GRANT SELECT ON msdb.dbo.backupmediafamily TO db_backupstatus_reader;
GRANT SELECT ON msdb.dbo.backupfile TO db_backupstatus_reader;
GO

-- Then, whichever account should be able to read backup status:
-- ALTER ROLE db_backupstatus_reader ADD MEMBER [DOMAIN\BlueTrackAppPoolAccount];
-- -- or, for a human admin checking status via SSMS instead of widening
-- -- the app's own account:
-- ALTER ROLE db_backupstatus_reader ADD MEMBER [DOMAIN\SomeAdminLogin];
```

A role decouples "what permission is needed" from "who holds it" — confirmed directly: it should be assignable either to the application pool's own account (the feature as already built assumes this) or to a separate admin account (a human checking status by hand, without widening the app's own least-privileged connection). `backupmediafamily`/`backupfile` are included ahead of need in case a future refinement of the backup-status query wants media-set or physical-file detail beyond what `backupset` alone carries — trivial to drop if that never happens.

**Turned into a runnable file 2026-09-16**: `Database/38_BlueTrack_GrantBackupStatusReaderRole.sql` (Option B from `Archive_Planning_Outstanding-Work-Survey-2026-09-16.md` — a separate, DBA-run script, never through `App/Migrator`, mirroring `14_BlueTrack_ScheduleImportLoadJob.sql`'s own established precedent for a script that must `USE msdb`). The Group / Role Mapping admin page's existing group-resolver "Lookup / Test Tool" gained a "Generate db_backupstatus_reader Script" button (`GroupRoleMappingsController`'s new `generate-backupstatus-reader-script` endpoint) that substitutes the resolved account name into the tracked script's `__TARGET_ACCOUNT__` placeholder and returns it as a download — a DBA still has to actually run it, but no longer has to hand-write the `ALTER ROLE ADD MEMBER` line themselves. This does not resolve who is expected to run it or when (see Open Questions) — it only removes the "the script doesn't exist as a file yet" half of that gap.

Query shape (illustrative): most recent `backup_finish_date` per backup `type` (`D`=full, `I`=differential, `L`=log) for `database_name = 'BlueTrack'` (or whichever database the running connection string targets, not hardcoded), from `msdb.dbo.backupset`.

**Implemented 2026-09-04.** A new `DeploymentController`/`DeploymentRepository` back a new `admin/DeploymentInfo.vue` page, gated by a new `ViewDeploymentInfo` permission (originally granted via its own incremental script, `Database/25_BlueTrack_DeploymentInfoPermissionSeed.sql`; that script was retired in the 2026-09-05 restructure as redundant for a fresh install — the permission is already in `08_BlueTrack_WebSchema.sql`'s seed and `09_BlueTrack_WebSeed.sql`'s bootstrap Admin grant already covers every permission that exists at seed time).

- **3.1**: `EnvironmentName` from `IHostEnvironment`; `Version` from a new `<Version>1.0.0</Version>` in `BlueTrack.Api.csproj`; build timestamp from the running assembly's own DLL file metadata (`File.GetLastWriteTimeUtc`) -- no build-pipeline change needed, exactly as scoped.
- **3.2**: three real `IHealthCheck` implementations (`App/Api/HealthChecks/`) registered via `AddHealthChecks()` -- `SqlServerHealthCheck` (`SELECT 1` through `IDbConnectionFactory`), `SecretsStoreHealthCheck` (`VaultSecretProviderResolver.ResolveActiveAsync()`, dispatch-only, no live secret retrieval), `IdentityProvidersHealthCheck` (at least one enabled provider; OIDC/SAML additionally checked for populated required fields via the existing `ProviderSettingsReader`). Deliberately **not** mapped as an anonymous `/health` route -- `DeploymentController` calls `HealthCheckService.CheckHealthAsync()` directly and returns the results only through the same `ViewDeploymentInfo`-gated endpoint, consistent with this app never exposing anything unauthenticated elsewhere; this still runs through ASP.NET Core's own health-checks middleware/`HealthCheckService`, just consumed differently than a typical infra-probe setup.
- **3.3**: `DeploymentRepository.GetBackupStatusAsync()` queries `msdb.dbo.backupset` filtered by `database_name = DB_NAME()` (so it always targets whatever database the connection string points at) and catches `SqlException` to return a plain `{ Available: false, Error: "..." }` result rather than a raw 500 -- verified this actually works end-to-end against `BlueTrackTest` this session (the account used for that verification is this session's own elevated SQL access, not the app's real least-privileged service account, so D-97's flagged risk about the real service account's `msdb` permissions is unchanged and still needs a manual DBA grant in any real environment).

Verified: `dotnet test` 247/247 (including new gate/functional coverage in `AdminControllersPermissionTests.cs`), Vitest 16/16, and a new Playwright test loading the page and asserting all three sections render plus a 403 for a user without the permission. One non-obvious pitfall hit and documented in `Reference_Lessons-Learned.md`: granting a brand-new permission to an already-cached test identity's role doesn't take effect until that identity's rights are reloaded (Reload Rights) or its cache entry's sliding expiration lapses. See PR #16.

### 3.4 On-demand backup ("Backup App" button)

**Requested directly, 2026-09-08 (D-117)**, alongside the new Credentials/LDAP work (D-116, `Design_Credentials-Management.md`) and role-targeted notification email (`Design_Notifications.md`'s updated Proposed Workflow) — a button on this same Deployment page to trigger a real backup on demand, not just view history.

Two questions resolved directly rather than assumed:
- **Backup destination**: a new `web.app_config.BackupFolder` setting (admin-editable on Global Application Configuration, alongside the other app-wide settings) — not SQL Server's own default backup directory, so this app's own admin UI decides where its backups land, consistent with every other admin-editable setting in this app.
- **Config file scope**: `appsettings.json`/`appsettings.Production.json` only, bundled into a downloadable zip returned alongside triggering the database backup. "Exclude the DPAPI secret" (the user's own phrasing) maps cleanly onto this scope: `WindowsDpapiProtector` creates no key file of its own (Windows manages DPAPI's machine/user master keys transparently, confirmed by reading that class directly) — the only DPAPI material that exists at all is ciphertext inside `web.credential`/`web.notification_config` rows, which a zip of two JSON files never touches. That ciphertext IS still included in the separate `BACKUP DATABASE` step (T-SQL's `BACKUP DATABASE` has no column-level exclusion), which is fine and expected — D-65 already documents that DPAPI ciphertext only ever decrypts on this one machine anyway, so a database backup carrying it around is no more of a portability concern than everything else D-65 already flags.

**Implemented 2026-09-08.** `DeploymentRepository.TriggerBackupAsync` runs a real `BACKUP DATABASE ... TO DISK` (database name read via `DB_NAME()` and bracket-quoted into the command text since `BACKUP DATABASE` doesn't accept a parameterized database name; the destination path is a real parameter) with no `WITH COMPRESSION` (not every SQL Server edition supports it, and this app doesn't know which edition it's running against). `DeploymentController.Backup()` (`POST /api/admin/deployment/backup`) requires a new `TriggerBackup` permission **on top of** the class's existing `ViewDeploymentInfo` (ASP.NET Core combines multiple `[Authorize]` policies with AND) — a deliberate split from the page's own read-only permission, since triggering a backup is a mutating, potentially expensive action very different from just viewing status. Returns a zip (`appsettings*.json` plus a small `BACKUP_NOTE.txt` recording the `.bak` file's real path) as the HTTP response body; the frontend turns that into a real browser download via an object URL.

**Verified against the real environment**: pointed `BackupFolder` at SQL Server's own default backup directory (guaranteed write access for the SQL Server service account, sidestepping a permissions question this pass didn't need to answer) and triggered a real backup — a genuine ~20MB `.bak` file was created, the returned zip contained `appsettings.json` (no `appsettings.Production.json` exists in this dev environment, correctly skipped) and the note file, and the existing 3.3 backup-status query immediately reflected the new backup (`type: D`, today's finish date) with no code change needed on that side. `dotnet test` 253/253, Vitest 18/18 (unaffected -- no existing test exercises this new endpoint).

## Open Questions

- **Certificate thumbprint validation for SAML.** The SAML fields above are plain text inputs for now — no lookup against the Windows Certificate Store to confirm a thumbprint is real/installed before save. Adding that would need a new endpoint (`GET /api/admin/identity-providers/certificates` or similar, enumerating `LocalMachine\My`/`TrustedPeople` thumbprints) — worth doing, but scoped separately since it's germane to Identity Providers specifically, not shared with Part 2/3.
- **Exact `<Version>` value and bump process.** Proposed as a manual, human-maintained value to start (no automated versioning/CI-stamping pipeline exists yet) — confirm that's acceptable before treating a specific number as meaningful.
- **Who is expected to run the `msdb` permission grant, and when** — this design doc flags that it's needed, but granting it is an infrastructure/DBA action outside what this app's own code or a migration script can do (a migration runs as the app's own connection, which is exactly the account that needs the *extra* grant — it can't grant itself broader permissions from inside its own restricted connection).
- ~~**Permission gating for the new Deployment page**~~ **Resolved (D-98):** a new `ViewDeploymentInfo` permission, granted to the bootstrap Admin role only (originally `Database/25_BlueTrack_DeploymentInfoPermissionSeed.sql`; folded into `08_BlueTrack_WebSchema.sql`/`09_BlueTrack_WebSeed.sql` in the 2026-09-05 restructure).
- **Who is expected to *run* the `msdb` permission grant, and when** — the mechanism itself is now designed (D-107's `db_backupstatus_reader` role, above), but granting it is still an infrastructure/DBA action outside what this app's own code or a migration script can do (a migration runs as the app's own connection, which is exactly the account that needs the *extra* grant — it can't grant itself broader permissions from inside its own restricted connection), and no specific person/process owns actually executing that script yet.
- ~~**Permission gating for the new Deployment page**~~ **Resolved (D-98):** a new `ViewDeploymentInfo` permission, granted to the bootstrap Admin role only (`Database/25_BlueTrack_DeploymentInfoPermissionSeed.sql`).

---
*New document added 2026-09-04, following the user's request to build out structured config UI for Identity Providers and Secrets Store, plus a new admin page for deployment/environment information (environment+version, health checks, SQL Server backup status) — the exact scope narrowed down through several rounds of direct clarifying questions rather than assumed.*
