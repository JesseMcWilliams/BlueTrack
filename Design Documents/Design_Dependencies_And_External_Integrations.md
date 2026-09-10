# Dependencies & External Integrations

**Blueprint Progress Tracking**

## Purpose & Scope

A single, maintained inventory of every external system, third-party API, and library this project depends on — what it's for, whether it's currently active or a built-but-unverified alternative, and where in the codebase it's wired up. Package versions come straight from the `.csproj`/`package.json` files; nothing here is estimated. **Keep this in sync**: whenever a package reference or an external integration changes, update the relevant table/section here in the same commit — the same discipline `Design_Decision_Register.md` expects for design decisions.

## 1. Runtime & build prerequisites

| Requirement | Needed for |
|---|---|
| Windows (Server or desktop) | `App/Api` targets `net10.0` with `[assembly: SupportedOSPlatform("windows")]` — Negotiate auth, DPAPI, and `System.DirectoryServices.AccountManagement` are all Windows-only |
| .NET 10 SDK | `App/Api`, `App/Api.Tests`, `App/Migrator` |
| Node.js / npm | `App/Web` (Vite/Vue), `App/E2E` (Playwright) |
| SQL Server (reachable, able to create databases) | The only datastore this app uses — see 6 below |
| CyberArk Application Password SDK (`NetStandardPasswordSDK.dll`, local install) | Only if building with CyberArk Credential Provider (CP) as the active/available secrets backend — see 2 below. Every other secrets backend builds without it. |
| IIS (production/shared environments only) | Hosting the built SPA + the API as a nested Application (`Design_Deployment_Methodology.md`) — not needed for `dotnet run`/`npm run dev` local development |

## 2. External systems & APIs actually integrated

### 2.1 CyberArk — data warehouse feed (CSV, not a live API)

The `dbo` schema's operational data (accounts, safes, platforms, entitlements) comes from **flat-file exports**, not a live CyberArk REST call: Privileged Cloud CSV exports (`Reference/PrivilegedCloud/`) loaded via `usp_Import_All`/`usp_Import_PC_*` (BULK INSERT), plus a same-instance cross-database query against a separate Self-Hosted/EVD database (`usp_Import_SelfHosted_EVD`). Access-group/target inventory used for risk scoring comes from a similarly file-based bulk-import mechanism (`App/Api/RiskScoring/CsvFileReader.cs`, `CsvHelper`), not a CyberArk API either — see `Design_Risk_Scoring.md`.

### 2.2 CyberArk — secrets retrieval (live API/local call, distinct from 2.1)

Used only for BlueTrack's own operational credentials (e.g. an SMTP or LDAP bind account) via the pluggable `IVaultSecretProvider` abstraction (`App/Api/Secrets/`, `Design_Secrets_Storage.md`) — never for the warehouse data above:

- **CyberArk Central Credential Provider (CCP)** — a real REST call (`AddHttpClient(nameof(CyberArkCcpSecretsProvider))`, `App/Api/Program.cs`) to a configured PVWA host's `AIMWebService/api/Accounts` endpoint. Confirmed working against a real PVWA host.
- **CyberArk Credential Provider (CP)** — an in-process call via CyberArk's own Application Password SDK (`NetStandardPasswordSDK.dll`, referenced by local `HintPath` in `App/Api/BlueTrack.Api.csproj` since CyberArk doesn't publish it to NuGet). Requires the CP agent installed and running on the same box.
- **CyberArk Conjur** — a hand-rolled REST client against Conjur's Authn API (`AddHttpClient(nameof(CyberArkConjurSecretsProvider))`). Built as scaffolding; not verified against a live Conjur instance.

### 2.3 Other secrets backends (same `IVaultSecretProvider` abstraction)

- **Windows DPAPI** (`System.Security.Cryptography.ProtectedData`) — the default, active-out-of-the-box backend; encrypts in place, no external dependency. `DataProtectionScope` defaults to `LocalMachine`, admin-configurable to `CurrentUser`.
- **Azure Key Vault** (`Azure.Security.KeyVault.Secrets` + `Azure.Identity`) — built as a placeholder framework, not verified against a live vault.
- **AWS Secrets Manager** (`AWSSDK.SecretsManager`) — same status as Azure Key Vault: present, unverified against a live service.

Exactly one backend is active at a time (admin-selectable via the Secrets Store Configuration page); switching backends never requires a code change, only configuration.

### 2.4 Authentication providers

| Provider | Status | Mechanism |
|---|---|---|
| Windows Integrated (Negotiate/Kerberos) | **Default, fully wired** | `Microsoft.AspNetCore.Authentication.Negotiate` |
| SAML2 | Real code path, not enabled by default | `ITfoxtec.Identity.Saml2.MvcCore`, needs a real IdP's metadata/config entered before use |
| OpenID Connect | Real code path, not enabled by default | `Microsoft.AspNetCore.Authentication.OpenIdConnect`, same caveat as SAML2 |
| DevFakeAuth | Development environment only | Reuses the Negotiate handler with substituted group-membership resolution — lets a developer exercise every permission path without a real domain |

See `Design Documents/Design_Authentication_Architecture.md` for the full design, including the break-glass local-account mechanism (credential held in CyberArk itself, not this app's own secret store).

### 2.5 Directory services (LDAP/Active Directory)

`System.DirectoryServices.AccountManagement`, used by exactly one component: `App/Api/Ldap/LdapGroupMemberResolver.cs`. It resolves a notification role's mapped AD group(s) to real recipient email addresses (binding via a stored `web.credential` LDAP bind account, expanding group membership recursively) — **notification recipient resolution only**, not authentication. Opt-in via `web.ldap_config.IsEnabled`.

### 2.6 Email (SMTP)

`MailKit`, wired through `App/Api/Notifications/SmtpNotificationSender.cs`. Sends via an admin-configured SMTP relay (host/port/STARTTLS/auth method, credential via `web.credential`); two admin-editable TLS overrides exist (ignore CRL/OCSP revocation checks; ignore all SSL errors), both off by default. Currently triggered by one wired check (`DevFakeAuthEnabledCheck` — warns when DevFakeAuth has been left enabled too long), via the general-purpose `INotificationCheck`/`NotificationCheckBackgroundService` framework designed to take more triggers later without further schema change. See `Design Documents/Design_Notifications.md`.

### 2.7 Database

**SQL Server is the only datastore.** `Microsoft.Data.SqlClient` + `Dapper` (no ORM) back every `App/Api/Data/*Repository.cs`; `dbup-sqlserver` (`App/Migrator`) applies `Database/*.sql` in filename order. `Microsoft.Extensions.Caching.SqlServer` backs a SQL-Server-based distributed cache (`web.distributed_cache`) used for per-user permission caching (`UserRightsCache`) — not an ASP.NET Core cookie session.

## 3. NuGet packages — `App/Api`

| Package | Version | Purpose |
|---|---|---|
| AWSSDK.SecretsManager | 4.0.100.11 | AWS Secrets Manager backend (2.3) |
| Azure.Identity | 1.21.0 | Azure AD credential flow for Key Vault access |
| Azure.Security.KeyVault.Secrets | 4.11.1 | Azure Key Vault backend (2.3) |
| CsvHelper | 33.1.0 | Risk Scoring bulk CSV import/parsing (Targets, Access Groups, mapping tables) |
| Dapper | 2.1.79 | All SQL data access — no ORM beyond this |
| ITfoxtec.Identity.Saml2.MvcCore | 4.20.1 | SAML2 authentication (2.4) |
| MailKit | 4.17.0 | SMTP notification sending (2.6) |
| Microsoft.AspNetCore.Authentication.Negotiate | 10.0.11 | Windows Integrated authentication (2.4) |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.11 | OIDC authentication (2.4) |
| Microsoft.AspNetCore.OpenApi | 10.0.11 | OpenAPI/Swagger document generation |
| Microsoft.Data.SqlClient | 7.0.2 | SQL Server connectivity |
| Microsoft.Extensions.Caching.SqlServer | 10.0.11 | SQL-Server-backed distributed cache (2.7) |
| System.DirectoryServices.AccountManagement | 10.0.11 | LDAP/AD group-membership resolution (2.5) |
| System.Security.Cryptography.ProtectedData | 10.0.11 | Windows DPAPI secrets backend (2.3) |
| *NetStandardPasswordSDK* (local reference, not NuGet) | n/a | CyberArk Credential Provider (2.2) |

## 4. NuGet packages — `App/Migrator` and `App/Api.Tests`

| Project | Package | Version | Purpose |
|---|---|---|---|
| Migrator | dbup-sqlserver | 7.2.0 | Applies `Database/*.sql` in order, tracks what's already run |
| Migrator | Microsoft.Data.SqlClient | 7.0.2 | SQL Server connectivity |
| Api.Tests | Microsoft.NET.Test.Sdk | 17.12.0 | Test host |
| Api.Tests | xunit | 2.9.2 | Test framework |
| Api.Tests | xunit.runner.visualstudio | 2.8.2 | Test discovery/runner |
| Api.Tests | Microsoft.AspNetCore.Mvc.Testing | 10.0.0 | In-memory `WebApplicationFactory` for contract tests |

## 5. npm packages — `App/Web` and `App/E2E`

| Project | Package | Version | Purpose |
|---|---|---|---|
| Web | vue | ^3.5.0 | UI framework (Composition API) |
| Web | vue-router | ^4.4.0 | Client-side routing |
| Web | pinia | ^2.2.0 | State stores (rights, theme, page size) |
| Web (dev) | vite | ^5.4.0 | Dev server / build |
| Web (dev) | @vitejs/plugin-vue | ^5.1.0 | Vue SFC compilation under Vite |
| Web (dev) | vitest | ^3.2.7 | Unit/component test runner |
| Web (dev) | @vue/test-utils | ^2.5.0 | Component mounting for Vitest |
| Web (dev) | happy-dom | ^20.14.0 | DOM environment for Vitest |
| E2E (dev) | @playwright/test | ^1.48.0 | Browser-driven end-to-end tests |

## 6. Internal module map (`App/Api`)

Not external dependencies, but the top-level internal modules a change is likely to touch:

| Folder | Covers |
|---|---|
| `Controllers/` | HTTP endpoints |
| `Data/` | Dapper repositories |
| `Models/` | Request/response/domain shapes |
| `Auth/` | Authentication provider wiring, permission policies, `UserRightsCache` |
| `Audit/` | Audit log writer |
| `HealthChecks/` | Deployment admin page's health-check implementations |
| `Ldap/` | `LdapGroupMemberResolver` (2.5) |
| `Notifications/` | Notification framework — checks, senders, background service (2.6) |
| `RiskScoring/` | Target/Access Group matching, CSV import, staleness propagation, band overlap validation |
| `Secrets/` | `IVaultSecretProvider`/`ILocalSecretProtector` implementations (2.2/2.3) |

## Maintenance

This document is a snapshot, not a live report — it goes stale the moment a package is added/upgraded or a new external integration is wired up without a matching edit here. When you touch a `.csproj`/`package.json`, or add a new `IVaultSecretProvider`/authentication provider/outbound `HttpClient`, update the relevant section above in the same change.
