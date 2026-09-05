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

## Part 3: New Deployment admin page

### 3.1 Environment name + version/build info

- **Environment name**: `IHostEnvironment.EnvironmentName` (ASP.NET Core's own built-in concept, driven by `ASPNETCORE_ENVIRONMENT`) — already real, no new plumbing needed, and naturally lines up with D-54's Dev/Test/Staging/Prod naming as long as that variable is set to match per environment.
- **Version/build info**: this app has no existing versioning scheme (`BlueTrack.Api.csproj` has no `<Version>` today). Proposed: add an explicit `<Version>` to the csproj (even a simple `1.0.0` to start, bumped manually per real release) plus the assembly's own build timestamp (read from the compiled DLL's file metadata at runtime — no new build-pipeline step required). Not a claim of a mature release-versioning process, just enough for an admin to answer "what's actually running right now."

### 3.2 Connectivity/health checks

Built on ASP.NET Core's built-in health checks middleware (`Microsoft.Extensions.Diagnostics.HealthChecks`, part of the shared framework — no new package needed for simple custom checks), which this app doesn't use anywhere yet. Three checks, each backed by something this app can genuinely verify itself:
- **SQL Server**: a real, trivial query (`SELECT 1`) through the existing `IDbConnectionFactory` — a real connectivity check, not a stub.
- **Active Secrets Store backend**: resolve it via the existing `VaultSecretProviderResolver` and confirm a provider implementation exists for whichever backend is active — this does **not** attempt a live secret retrieval (that stays explicitly out of automated scope, same reasoning as `Design_Testing_Strategy.md`'s "Explicitly not automated" section), it only confirms the dispatch succeeds.
- **Configured identity providers**: confirm at least one provider row is enabled and, for OIDC/SAML specifically, that its required settings fields are actually populated (not a live IdP reachability check — reaching a real IdP over the network is a bigger, separate scope, consistent with this project's existing "real external IdP stays manually verified" stance).

### 3.3 SQL Server backup status

**Confirmed data source, user's explicit choice, 2026-09-04**: SQL Server's own native backup history (`msdb.dbo.backupset`), not a specific third-party tool — works regardless of which backup mechanism actually writes those rows (a maintenance plan, Ola Hallengren's scripts, a third-party tool that also updates `msdb`), since that's SQL Server's own universal backup ledger.

**A real, flagged operational risk, not silently assumed to work**: per D-30, this app's own SQL account is a deliberately least-privileged service account ("not `db_owner`, just grants scoped to what the app needs") — it almost certainly does **not** have read access to `msdb` today, since nothing in this app has ever needed it before now. This is a genuine deployment-time requirement, not something the application code can grant itself: whoever manages the SQL Server service account needs to run something equivalent to `GRANT SELECT ON msdb.dbo.backupset TO [that account];` (or add it to a suitable `msdb` role) before this feature will work in a real environment — this needs to be called out plainly in whatever setup documentation covers this feature, not discovered later as a silent failure. The endpoint itself should fail gracefully (a clear "backup history unavailable — check msdb permissions" message) rather than a raw 500 if that grant hasn't been made yet.

Query shape (illustrative): most recent `backup_finish_date` per backup `type` (`D`=full, `I`=differential, `L`=log) for `database_name = 'BlueTrack'` (or whichever database the running connection string targets, not hardcoded), from `msdb.dbo.backupset`.

## Open Questions

- **Certificate thumbprint validation for SAML.** The SAML fields above are plain text inputs for now — no lookup against the Windows Certificate Store to confirm a thumbprint is real/installed before save. Adding that would need a new endpoint (`GET /api/admin/identity-providers/certificates` or similar, enumerating `LocalMachine\My`/`TrustedPeople` thumbprints) — worth doing, but scoped separately since it's germane to Identity Providers specifically, not shared with Part 2/3.
- **Exact `<Version>` value and bump process.** Proposed as a manual, human-maintained value to start (no automated versioning/CI-stamping pipeline exists yet) — confirm that's acceptable before treating a specific number as meaningful.
- **Who is expected to run the `msdb` permission grant, and when** — this design doc flags that it's needed, but granting it is an infrastructure/DBA action outside what this app's own code or a migration script can do (a migration runs as the app's own connection, which is exactly the account that needs the *extra* grant — it can't grant itself broader permissions from inside its own restricted connection).
- **Permission gating for the new Deployment page** — proposed: a new permission (e.g. `ViewDeploymentInfo`), following this app's existing pattern of one dedicated permission per admin page, rather than folding it into an existing one like `ManageApplicationConfiguration`.

---
*New document added 2026-09-04, following the user's request to build out structured config UI for Identity Providers and Secrets Store, plus a new admin page for deployment/environment information (environment+version, health checks, SQL Server backup status) — the exact scope narrowed down through several rounds of direct clarifying questions rather than assumed.*
