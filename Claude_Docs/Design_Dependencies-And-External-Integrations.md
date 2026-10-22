# Dependencies & External Integrations

**Blueprint Progress Tracking**

## Purpose & Scope

A single, maintained inventory of every external system, third-party API, and library this project depends on — what it's for, whether it's currently active or a built-but-unverified alternative, and where in the codebase it's wired up. Package versions come straight from the `.csproj`/`package.json` files; nothing here is estimated. **Keep this in sync**: whenever a package reference or an external integration changes, update the relevant table/section here in the same commit — the same discipline `Design_Decision-Register.md` expects for design decisions.

## 1. Runtime & build prerequisites

| Requirement | Needed for |
|---|---|
| Windows (Server or desktop) | `App/Api` targets `net10.0` with `[assembly: SupportedOSPlatform("windows")]` — Negotiate auth, DPAPI, and `System.DirectoryServices.AccountManagement` are all Windows-only |
| .NET 10 SDK | `App/Api`, `App/Api.Tests`, `App/Migrator` |
| Node.js / npm | `App/Web` (Vite/Vue), `App/E2E` (Playwright) |
| SQL Server (reachable, able to create databases) | The only datastore this app uses — see 6 below |
| CyberArk Application Password SDK (`NetStandardPasswordSDK.dll`, local install) | Only if building with CyberArk Credential Provider (CP) as the active/available secrets backend — see 2 below. Every other secrets backend builds without it. |
| IIS (production/shared environments only) | Hosting the built SPA + the API as a nested Application (`Design_Deployment-Methodology.md`) — not needed for `dotnet run`/`npm run dev` local development |

## 2. External systems & APIs actually integrated

### 2.1 CyberArk — data warehouse feed (CSV, not a live API)

The `dbo` schema's operational data (accounts, safes, platforms, entitlements) comes from **flat-file exports** for Privilege Cloud: CSV exports (`Reference/PrivilegedCloud/`) loaded via `usp_Import_All`/`usp_Import_PC_*` (BULK INSERT). Self-Hosted comes from PVWA's REST API (below); the same-instance EVD database read by `usp_Import_SelfHosted_EVD` was removed by D-278. Access-group/target inventory used for risk scoring comes from a similarly file-based bulk-import mechanism (`App/Api/RiskScoring/CsvFileReader.cs`, `CsvHelper`), not a CyberArk API either — see `Design_Risk-Scoring.md`.

### 2.1a CyberArk — Self-Hosted PVWA REST API (live, optional; D-248)

When Admin > Data Sources > CyberArk sources sets Self-Hosted to **Direct**, the API reads a Self-Hosted PVWA's 2nd gen REST API (`/PasswordVault/API/`, PAM Self-Hosted 14.2 or later) each night: logon (`auth/CyberArk` or `auth/LDAP`), Safes, Accounts, Safe members, Users, UserGroups and Platforms, `GET` only. `HttpClient` named `PvwaDirect` (`App/Api/Program.cs`; redirects off, 5-minute timeout). The credential is a named `web.credential` fetched at each pull (a CP or CCP one reads the Vault; see 2.2). Verified against the lab PVWA on 2026-10-19; see `Design_CyberArk-Direct-Integration.md` for what it returns and the gaps against the EVD. Nothing is written to PVWA. Since script 48 a nightly or Pull now pull also makes classic (1st gen) `GET` calls under `/PasswordVault/WebServices/PIMServices.svc/`: `Accounts?Keywords=&Safe=` for each new or changed account's device type, creation method and linked accounts (D-280), and `Applications/` with `Applications/{AppID}/Authentications/` for the CyberArk Applications (D-282, needs Audit Users). The account activity call (D-270) was already classic.

### 2.2 CyberArk — secrets retrieval (live API/local call, distinct from 2.1)

Used only for BlueTrack's own operational credentials (e.g. an SMTP or LDAP bind account) via the pluggable `IVaultSecretProvider` abstraction (`App/Api/Secrets/`, `Design_Secrets-Storage.md`) — never for the warehouse data above:

- **CyberArk Central Credential Provider (CCP)** — a real REST call (`AddHttpClient(nameof(CyberArkCcpSecretsProvider))`, `App/Api/Program.cs`) to a configured PVWA host's `AIMWebService/api/Accounts` endpoint. Confirmed working against a real PVWA host.
- **CyberArk Credential Provider (CP)** — an in-process call via CyberArk's own Application Password SDK (`NetStandardPasswordSDK.dll`, referenced by local `HintPath` in `App/Api/BlueTrack.Api.csproj` since CyberArk doesn't publish it to NuGet). Requires the CP agent installed and running on the same box. Optional at build time (D-169): without the DLL, `App/CyberArkSdk.targets` leaves `CyberArkCpSecretsProvider` out, and the `CyberArkCP` backend reports "no provider implementation" if activated.
- **CyberArk Conjur** — a hand-rolled REST client against Conjur's Authn API (`AddHttpClient(nameof(CyberArkConjurSecretsProvider))`). Built as scaffolding; not verified against a live Conjur instance.

### 2.3 Other secrets backends (same `IVaultSecretProvider` abstraction)

- **Windows DPAPI** (`System.Security.Cryptography.ProtectedData`) — the default, active-out-of-the-box backend; encrypts in place, no external dependency. `DataProtectionScope` defaults to `LocalMachine`, admin-configurable to `CurrentUser`.
- **Azure Key Vault** (`Azure.Security.KeyVault.Secrets` + `Azure.Identity`) — built as a placeholder framework, not verified against a live vault.
- **AWS Secrets Manager** (`AWSSDK.SecretsManager`) — same status as Azure Key Vault: present, unverified against a live service.

Exactly one backend is active at a time (admin-selectable via the Secrets Store Configuration page); switching backends never requires a code change, only configuration.

### 2.3a DNS (the app server's resolver)

D-212 (Data Sources phase 4c): after each account inventory feed import, `DnsHostResolver` (`App/Api/DataFeeds/PossibleMatchFinder.cs`) looks up host names and IP addresses with .NET's `System.Net.Dns`, as the App Pool's account, to suggest possible account matches (forward and reverse lookups, 3 seconds each, cached for the run). Only addresses of accounts whose user name matches a CyberArk account are looked up. A failed lookup just means no suggestion.

### 2.4 Authentication providers

| Provider | Status | Mechanism |
|---|---|---|
| Windows Integrated (Negotiate/Kerberos) | **Default, fully wired** | `Microsoft.AspNetCore.Authentication.Negotiate` |
| SAML2 | Real code path, not enabled by default | `ITfoxtec.Identity.Saml2.MvcCore`, needs a real IdP's metadata/config entered before use |
| OpenID Connect | Real code path, not enabled by default | `Microsoft.AspNetCore.Authentication.OpenIdConnect`, same caveat as SAML2 |
| DevFakeAuth | Development environment only | Reuses the Negotiate handler with substituted group-membership resolution — lets a developer exercise every permission path without a real domain |

See `Claude_Docs/Design_Authentication-Architecture.md` for the full design, including the break-glass local-account mechanism (credential held in CyberArk itself, not this app's own secret store).

### 2.5 Directory services (LDAP/Active Directory)

`System.DirectoryServices.AccountManagement`, used by two components under `App/Api/Ldap/` and `App/Api/AdDiscovery/`:
- `LdapGroupMemberResolver` resolves a notification role's mapped AD group(s) to real recipient email addresses — **notification recipient resolution only**, not authentication.
- `AdAccountDiscoveryService` (AD Account Discovery feature, 2026-09-16, D-137) enumerates real members of every already-inventoried Access Group's AD group, to find accounts not yet onboarded into CyberArk and risk-score them.
- D-233: with account classification on, `AdAccountDiscoveryService` also runs `DirectorySearcher` searches (System.DirectoryServices, through the context's `PrincipalSearcher.GetUnderlyingSearcher()`) for UPN, employeeID/employeeNumber and an allow-list of attributes, 50 accounts per search, plus capped owner lookups (`Design_Risk-Scoring.md`, "Account classification").

- D-281: `SafeGroupAdExpansionService` (`App/Api/CyberArkDirect/`), inside the nightly Self-Hosted Direct pull, reads through `AdAccountDiscoveryService`'s `IAdDirectory` the users of each group that is a Safe member (a Self-Hosted directory group by DN, a Privilege Cloud group by name), for "who can use this account".

Both share `LdapContextFactory` for binding and read `web.ldap_config` — now a real per-domain table (one row per AD domain/forest, each independently enabled with its own bind credential or trusted connection), not the singleton it originally shipped as. Opt-in per row via `IsEnabled`.

### 2.6 Email (SMTP)

`MailKit`, wired through `App/Api/Notifications/SmtpNotificationSender.cs`. Sends via an admin-configured SMTP relay (host/port/STARTTLS/auth method, credential via `web.credential`); two admin-editable TLS overrides exist (ignore CRL/OCSP revocation checks; ignore all SSL errors), both off by default. Currently triggered by one wired check (`DevFakeAuthEnabledCheck` — warns when DevFakeAuth has been left enabled too long), via the general-purpose `INotificationCheck`/`NotificationCheckBackgroundService` framework designed to take more triggers later without further schema change. See `Claude_Docs/Design_Notifications.md`.

**OAuth2 token endpoints (D-223)**, called only when an OAuth2 Auth Method is chosen, by `App/Api/Notifications/OAuth/SmtpOAuthTokenService.cs` with plain `HttpClient` (no new package; JWTs are built and signed with `System.Security.Cryptography`): 10 seconds a try, 2 retries, no redirects. None is proven against the live service yet.

| System | Endpoint | Used for |
|---|---|---|
| Microsoft Entra ID | `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token` (scope `https://outlook.office365.com/.default`) | Microsoft 365 app-only token; then XOAUTH2 to `smtp.office365.com:587` |
| Google OAuth 2.0 | `https://oauth2.googleapis.com/token` (JWT bearer, scope `https://mail.google.com/`) | Google Workspace service account token; then XOAUTH2 to `smtp.gmail.com:587` |
| Any OAuth2 provider | The admin's https token URL (RFC 6749 client credentials) | Generic OAuth2 |

### 2.6a SIEM (syslog collector, D-234)

The audit log can be forwarded to the organisation's SIEM: an outbound connection from the API to the syslog collector the admin names on **Admin > SIEM Forwarding** (off by default). CEF inside RFC 5424 syslog, over TCP (RFC 6587, default port 514) or TLS (RFC 5425, default port 6514), octet-counting framing by default or LF. Built on .NET's own `TcpClient` and `SslStream` (`App/Api/Siem/SyslogTransport.cs`); **no new package**. TLS trusts the Windows store, a pinned certificate fingerprint or a given CA; no "ignore errors" option. One connection per batch, from `SiemForwardingBackgroundService`; nothing is received from the collector. The firewall must allow the API server out to the collector's port. Not yet tested against a real SIEM. Details: `Design_Audit-Logging.md`, "SIEM forwarding".

### 2.7 Database

**SQL Server is the only datastore.** `Microsoft.Data.SqlClient` + `Dapper` (no ORM) back every `App/Api/Data/*Repository.cs`; `dbup-sqlserver` (`App/Migrator`) applies `Database/*.sql` in filename order. `Microsoft.Extensions.Caching.SqlServer` backs a SQL-Server-based distributed cache (`web.distributed_cache`) used for per-user permission caching (`UserRightsCache`) — not an ASP.NET Core cookie session.

## 3. NuGet packages — `App/Api`

| Package | Version | Purpose |
|---|---|---|
| AWSSDK.SecretsManager | 4.0.100.11 | AWS Secrets Manager backend (2.3) |
| Azure.Identity | 1.21.0 | Azure AD credential flow for Key Vault access |
| Azure.Security.KeyVault.Secrets | 4.11.1 | Azure Key Vault backend (2.3) |
| ClosedXML | 0.105.1 | Report export as Excel `.xlsx` (D-220, `App/Api/Reports/XlsxReportWriter.cs`). MIT licence. CSV export is written by hand (`CsvReportWriter`), not CsvHelper |
| CsvHelper | 33.1.0 | Risk Scoring bulk CSV import/parsing (Targets, Access Groups, mapping tables) |
| Dapper | 2.1.79 | All SQL data access — no ORM beyond this |
| ITfoxtec.Identity.Saml2.MvcCore | 4.20.1 | SAML2 authentication (2.4) |
| MailKit | 4.17.0 | SMTP notification sending (2.6) |
| Microsoft.AspNetCore.Authentication.Negotiate | 10.0.13 | Windows Integrated authentication (2.4) |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.13 | OIDC authentication (2.4) |
| Microsoft.AspNetCore.OpenApi | 10.0.13 | OpenAPI/Swagger document generation |
| Microsoft.Data.SqlClient | 7.0.2 | SQL Server connectivity |
| Microsoft.Extensions.Caching.SqlServer | 10.0.13 | SQL-Server-backed distributed cache (2.7) |
| System.DirectoryServices.AccountManagement | 10.0.13 | LDAP/AD group-membership resolution (2.5) |
| System.Security.Cryptography.ProtectedData | 10.0.13 | Windows DPAPI secrets backend (2.3) |
| System.Security.Cryptography.Xml | 10.0.13 | Not called directly -- pins a transitive dependency of `ITfoxtec.Identity.Saml2` past a vulnerable floor (see notes below; on .NET 10 the runtime's own copy is what runs) |
| System.Security.Cryptography.Pkcs | 10.0.13 | Not called directly -- pins a transitive dependency (MailKit/MimeKit, Microsoft.Data.SqlClient, Caching.SqlServer) past GHSA-846f-f764-76mx and GHSA-2fmv-rv2c-59x7; also pinned in `App/Migrator` (via SqlClient) |
| *NetStandardPasswordSDK* (local reference, not NuGet) | n/a | CyberArk Credential Provider (2.2) |

**Security note (2026-09-23, D-153):** `ITfoxtec.Identity.Saml2` (the SAML2 library, 2.4) transitively pulls `System.Security.Cryptography.Xml` at a 10.0.7 floor, which carries 5 high-severity advisories (GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-mmjf-rqrv-855v) -- all the same class of .NET 10 XML-encryption denial-of-service issue, CVSS 7.5, network-exploitable with no user interaction. Relevant here specifically because SAML processes externally-supplied XML (assertions/responses) over the network whenever that provider is enabled -- not just a theoretical transitive warning. Fixed by adding the explicit `System.Security.Cryptography.Xml` 10.0.10 reference in the table above, overriding the vulnerable floor. **`dotnet build` will show a `NU1510` warning suggesting this reference be removed as "automatically available and not needed"** -- do not remove it: the "automatically available" version is exactly the vulnerable 10.0.7 this override exists to replace. Re-check `dotnet list package --vulnerable --include-transitive` after any future `ITfoxtec.Identity.Saml2` upgrade to see whether its own floor has finally moved past 10.0.10, at which point this explicit override (and this note) can be removed.

**Security update (2026-10-14):** `dotnet list package --vulnerable --include-transitive` flagged Negotiate 10.0.11 (GHSA-vrrp-6v32-2p8g), Cryptography.Xml 10.0.10 (GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj, GHSA-r955-8q5j-3h83) and Cryptography.Pkcs 10.0.10 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7); all fixed in 10.0.13 (versions confirmed from the advisories and NuGet). The Microsoft 10.0.x packages above moved to 10.0.13 together. **On .NET 10, `System.Security.Cryptography.Xml` resolves to the shared runtime (`lib/net10.0/_._`: no DLL in bin), so the package version only quietens the audit; the fix that actually runs is the .NET runtime 10.0.13 (ASP.NET Core Hosting Bundle 10.0.13 on the server).** The reference is kept so the audit still flags it when it goes stale. Pkcs 10.0.13 does ship in bin and wins over the runtime copy. The web app's `npm audit --omit=dev` fix for `source-map-js` (GHSA-68fv-2mgg-jv7q) was a lockfile patch bump.

**ClosedXML (D-220, 2026-10-13):** version and licence confirmed from nuget.org (`dotnet package search ClosedXML --exact-match`, newest stable 0.105.1; its nuspec's licence expression is `MIT`). It brings, as resolved: `DocumentFormat.OpenXml` 3.1.1, `ClosedXML.Parser` 2.0.0, `ExcelNumberFormat` 1.1.0 and `RBush.Signed` 4.0.0 (all MIT), and `SixLabors.Fonts` 1.0.0 (Apache-2.0; ClosedXML allows up to 3.0, and later SixLabors.Fonts versions carry a different licence, so check it before letting it float up). The export never measures fonts (no `AdjustToContents`), so the server needs none installed. The API tests open the exported files with the same package; the E2E spec reads an `.xlsx` header row with a small zip/XML reader (`App/E2E/tests/xlsx.js`) instead of adding an npm package.

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
| Web | chart.js | ^4.5.1 | Canvas charts on Reports > Trends (D-221). MIT. Brings `@kurkle/color` (MIT) |
| Web | vue-chartjs | ^5.3.4 | Vue components for Chart.js (D-221). MIT. Peer: chart.js ^4.1.1, vue ^3 |
| Web | vue-i18n | ^11.4.14 | UI text localization (D-227, `Design_Localization.md`). MIT. Brings `@intlify/core-base`, `@intlify/message-compiler`, `@intlify/shared`, `@intlify/devtools-types` (11.4.14, MIT) and `@vue/devtools-api` 6.6.4 (MIT) |
| Web (dev) | vite | ^5.4.0 | Dev server / build |
| Web (dev) | @vitejs/plugin-vue | ^5.1.0 | Vue SFC compilation under Vite |
| Web (dev) | vitest | ^3.2.7 | Unit/component test runner |
| Web (dev) | @vue/test-utils | ^2.5.0 | Component mounting for Vitest |
| Web (dev) | happy-dom | ^20.14.0 | DOM environment for Vitest |
| E2E (dev) | @playwright/test | ^1.48.0 | Browser-driven end-to-end tests |

**chart.js and vue-chartjs (D-221, 2026-10-13):** versions and licences confirmed from the npm registry (`npm view chart.js version license`: 4.5.1, MIT, published 2025-10-13; `npm view vue-chartjs version license peerDependencies`: 5.3.4, MIT, published 2026-07-07). Both load only in the Trends page's own chunk (route-level `import()`), not on other pages. The Dashboard's pies stay hand-rolled SVG (D-143). Vitest stubs `vue-chartjs` (the test DOM has no canvas).

**vue-i18n (D-227, 2026-10-13):** version and licence confirmed from the npm registry (`npm view vue-i18n version license`: 11.4.14, MIT, published 2026-10-10; `latest` dist-tag). Runs under the site's CSP without `'unsafe-eval'`: since v10 messages are compiled JIT to an AST that is interpreted (vue-i18n.intlify.dev, "Optimization"), and neither the package's dist files nor our build contain `eval(` or `new Function`. `@intlify/unplugin-vue-i18n` (build-time precompile) was not added: its current 11.x needs Vite 6+, and the app is on Vite 5.

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
| `Ldap/` | `LdapGroupMemberResolver`, `LdapContextFactory` (2.5) |
| `AdDiscovery/` | `AdAccountDiscoveryService`/`AdAccountDiscoveryBackgroundService` (2.5) — AD Account Discovery feature |
| `Notifications/` | Notification framework — checks, senders, background service (2.6) |
| `Reports/` | Report export (D-220): `ReportExportService`, the CSV/.xlsx writers, the per-report columns |
| `RiskScoring/` | Target/Access Group matching, CSV import, staleness propagation, band overlap validation |
| `Secrets/` | `IVaultSecretProvider`/`ILocalSecretProtector` implementations (2.2/2.3) |
| `Siem/` | SIEM forwarding (2.6a): CEF and syslog formatting, the TCP/TLS transport, the forwarder and its background service |

## SBOMs (D-297)

`Sbom/` holds CycloneDX 1.6 JSON SBOMs of what is installed on a server: `BlueTrack.Api.cdx.json` (the NuGet packages of `App/Api`, with transitive ones), `BlueTrack.Migrator.cdx.json` (`App/Migrator`) and `BlueTrack.Web.cdx.json` (the npm packages in the built site, from `package-lock.json`; build-only packages left out). Each component has its version, package URL, licence and hashes. `Tools/New-BlueTrackSbom.ps1` writes them; the release tool runs it with the release version.

| Tool | Version | Licence | How it runs |
|---|---|---|---|
| CycloneDX (CycloneDX for .NET) | 6.2.0 (2026-04-27) | Apache-2.0 | .NET local tool, pinned in `dotnet-tools.json` (`dotnet tool restore`) |
| @cyclonedx/cyclonedx-npm | 6.0.1 (2026-08-11) | Apache-2.0 | `npx --yes @cyclonedx/cyclonedx-npm@6.0.1`, pinned in the script; not a project dependency |

Versions and licences from nuget.org's registration index and `npm view` on 2026-10-21. The newer CycloneDX for .NET releases (6.2.1 on 2026-10-11, 6.3.0 and 6.3.1 on 2026-10-12) were under two weeks old and not taken. The SBOMs don't cover the server prerequisites in section 1.

## Maintenance

This document is a snapshot, not a live report — it goes stale the moment a package is added/upgraded or a new external integration is wired up without a matching edit here. When you touch a `.csproj`/`package.json`, or add a new `IVaultSecretProvider`/authentication provider/outbound `HttpClient`, update the relevant section above in the same change.
