# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
Nothing at the moment. The 2026-10-13 requests shipped as D-220 to D-229.

## Requested, deferred
- **API access for scripts and other systems** (requested 2026-10-13): sign-in today is interactive only (Windows, SAML, OIDC). Needs a design: per-user API keys or service tokens, their permissions (a role, like a user), expiry and rotation, storage (hashed), and audit logging of their use.
- **Localize server-side text** (requested 2026-10-14): API error messages (ProblemDetails), audit details, feed run summaries and emails are English only; D-227 covers the SPA. Needs the request's language on the API (the Language preference or `Accept-Language`) and .NET resource files.
- **"Use browser default" in the Language picker** (requested 2026-10-14): once a language is saved, there's no way to go back to following the browser's language.

## Follow-ups found while building D-229 (2026-10-14)
- **Link two existing targets by hand**: only Target Match Review creates links, and an unlinked pair can't be linked again from the UI. Needs a picker on Edit Target (the D-225 typeahead) and a rule for a target that has its own linked targets.
- **A resolved Target Match Review row blocks deleting its candidate target** (409, its `CandidateTargetKey` FK): the E2E tests leave such targets behind. Decide whether a delete should clear or remove the target's review history.

## From the audit findings (moved here 2026-10-13, at the user's request)
- **Validate LDAPS against a remote DC** (Low): BlueTrack's `UseSsl` bind can't be proven on the dev host, which is the DC itself (binds from a DC to its own LDAPS fail; Authentik's LDAPS sync from another machine works). Run the check from DCACYBSQL01 (another domain): resolve and reach the DC on 636, trust its LDAPS certificate, bind with an account in the DC's domain (no trust between the domains, so not a trusted connection), then let AD Account Discovery run.
- **`ApplicationPoolIdentity` can't reach a SQL Server on another machine** (found 2026-09-24): `Login failed for user 'DOMAIN\HOSTNAME$'` although the login exists with the right SID. A known limitation of `ApplicationPoolIdentity` for outbound Windows authentication. Needs an environment decision: a dedicated domain account or gMSA for the App Pool (the Installation guide covers the gMSA).
- **CI runs approved pull-request code on this host** (Medium, accepted for now): the repo is public with a self-hosted runner. Mitigated: approval required for outside contributors, `permissions: contents: read`, `actions/checkout` pinned. Remaining: review a PR's changes (`ci.yml`, `package.json`, tests, scripts) before approving its run; making the repository private was declined.
- **Admin-only troubleshooting endpoints return exception text** (Info, kept on purpose): backup, credential test, SMTP test, feed test; that text is how an admin diagnoses the failure. Revisit only if those pages are opened to non-admins.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live. The 2026-10-12 review's hardening is done (D-215: User-scope DPAPI for their stored credentials, explicit Azure/AWS timeouts, Conjur's Base64 token header as CyberArk's own .NET client sends it); each still needs a real round trip before use.
- **SAML and OIDC**: proven end to end on the dev host against the lab Authentik on 2026-10-13: `/login/saml` and `/login/oidc` -> Authentik sign-in -> ACS / `signin-oidc` -> a BlueTrack session (account `AI_Admin_User`, which has no BlueTrack group, so no roles). Still for the user to check: a sign-in with a mapped user (`adm.jesse`) and Authentik's MFA prompt. SAML Single Logout built and proven against the same Authentik (D-217).
- **Installer on a fresh server**: `Db.AppPoolAccess` (D-171) and `Iis.Reset` (D-175) have only run by hand or with `-WhatIf`; the new `Iis.SiteRestart` (D-188) ran on the dev host only. DCACYBSQL01's app pool SQL login was granted by hand. Check all three on the next real install.
- **AD Account Discovery multi-domain**: tested with a fake two-domain directory (D-214, `AdAccountDiscoveryMultiDomainTests`); still unproven against a real second domain (DCACYBSQL01's domain could be one, once LDAPS works).
- **Account classification reads** (D-233): `FindUsers` (a `DirectorySearcher` from `PrincipalSearcher.GetUnderlyingSearcher()`, 50 values per filter) is tested only with the fake directory. Check against the real domain: it binds with trusted connection, explicit credentials and LDAPS; employeeID/employeeNumber and extensionAttributeN come back; the run time on the real group set; and owners outside the Search Base (not found by design).
- **SIEM forwarding** (D-234): tested only against an in-test TCP/TLS collector on 127.0.0.1. With the real SIEM, check octet-counting vs LF framing, CEF parsing of `rt`, `externalId`, `suser`, `cs1`-`cs3`, `c6a1` and severity, the collector certificate under the chosen trust mode (and revocation reachability), the firewall port, the 8 KB message size, and handling of a duplicate `externalId` after a retry (delivery is at-least-once).
- **OAuth2 sign-in for SMTP** (D-223): Microsoft 365, Google Workspace and Generic OAuth2 are built from each provider's documentation and tested against fakes only. Google is the least certain (its docs never show the service-account flow and SMTP together).
- **Release script against GitHub** (D-226): `Tools/New-BlueTrackRelease.ps1` was tested on a throwaway clone with a stub `gh`; the real PR, signed tag and GitHub release are first exercised at the next release.
- **Nightly job's single step** (D-224): installed on the dev host 2026-10-14 and `usp_RunNightlyImportLoad` run by hand (Import, Load, Snapshot succeeded); its first scheduled 02:00 run is 2026-10-15.

## Found in the real CyberArk data (checked 2026-10-13)
Self-Hosted: EVD `CyberArkSH`, loaded 2026-10-13. Privilege Cloud: the export CSVs in the configured folder (loaded since D-228, 2026-10-14).
- **Self-Hosted pivot property names confirmed**: `UserName`, `Address`, `PolicyID` exist with that spelling on every password object (`02_BlueTrack_Baseline_EtlLoads.sql:404`). Remove the PLACEHOLDER comment in the next script that touches `usp_Load_FactAccount`.
- **Self-Hosted `PlatformKey` is always NULL**: there's no Self-Hosted platform list, so `PolicyID` matches nothing in `dim_platform`. Decide on a source (e.g. seed `dim_platform` from the distinct `PolicyID` values).
- **Self-Hosted properties the pivot ignores**: `LogonDomain` (could fill `PlatformLogonDomain`), `CPMDisabled`, `DeviceType`, `CreationMethod`, and the linked logon/reconcile account properties (`ExtraPass1*`, `ExtraPass3*`). The EVD has no CPM status or last-change properties, so those stay empty for Self-Hosted.
- **Q-27 columns never filled**: the `CAFLock*` and `CAFAccessed`/`CAFNew`/`CAFRetrieved`/`CAFModified`/`CAFIsRequestNeeded` columns on `stg_sh_files` exist, but the EVD import doesn't select them.
- **Privilege Cloud columns always empty** in the current export: `LastReconciled`, `RemoteMachines`, `Deleted`, and the new Users `Enabled`/`Suspended` and Groups `Directory` (stored since D-228); `LastVerified`, `Platform_LogonDomain` and `CPMStatus` are mostly empty.
- **Privilege Cloud exports with no importer**: `Export LDAP Group Members.csv` and `Export_ApplicationsList.csv`. Decide whether either is useful.
- **Direct Account→Target links**: neither CyberArk source lists remote machines (Privilege Cloud `RemoteMachines` is empty; Self-Hosted has no such property), so the Address-derived links (D-193) are the CyberArk side. The `AccountTargetMap` data feed exists (`App/Api/DataFeeds/FeedColumns.cs:21`); what's missing is a non-CyberArk system to supply it.
- **Drop `dbo.stg_discovered_accounts`** in a new script: empty, and nothing references it (superseded by `web.discovered_account`).

## Process and tooling
- **Scheduled feed for Account → Application assignments** (Low, D-239): the CSV import exists on Application ↔ Safe Mapping > Bulk Actions; a data feed type for it (like `SafeAssignments`) needs the `web.data_feed.FeedType` CHECK constraint extended, the runner case, `FeedColumns` and the feed type list.
- **Installer doesn't update an old prerequisite** (Low): the prerequisite check tests presence only (the Hosting Bundle by `aspnetcorev2.dll`), so `-Step Prereq.HostingBundle` reports an installed 10.0.11 as done. A minimum-version check would let an upgrade install the .NET 10.0.13 security update itself; until then the release notes say to install it by hand.
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
