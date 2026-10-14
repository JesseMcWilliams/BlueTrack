# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
Requested 2026-10-13. Each ships on its own branch with a `D-n`; delete it here when it ships.
- **Report export**: CSV and Excel (`.xlsx`, server-side) on every report, with all rows that match the current filters and sort, up to a configurable cap. Reports and Admin hubs get their sub-pages in a vertical list on the left.
- **Trends page** (from `fact_account_progress_history`): accounts by stage, % complete, and risk level over time, each filterable by application or Safe.
- **More email notifications**: nightly import failed (right after the job), overdue Risk Exceptions (daily digest), and a weekly summary (day and time set on the Notifications page). An "approvals waiting" digest was dropped: exceptions are created Active (D-70), so nothing waits for approval.
- **Daily snapshot even when the import fails**: `usp_Load_FactAccountProgressHistory` runs only inside `usp_RunFullLoad`, so a failed load leaves a gap in Trends (the dev site has only 2026-09-05). Run it as its own step that always runs.
- **Site URL setting**: on Global Application Configuration, set by the installer from `-Hostname`/`-HttpsPort`, so emails link straight to the page instead of naming it.
- **OAuth2 for SMTP**: Microsoft 365, Google Workspace, and a generic client-credentials provider; the client secret or key stays in the Secrets Store. Confirm each provider's supported app-only sending path from its own documentation before building.
- **Target Match Review and CSV import mapping**: typeahead for the "Merged" target, and auto-detection of CSV headers on the mapping profile.
- **Localization framework**: every UI string in an English resource file (vue-i18n), a language picker in My Profile; other languages later, each reviewed by a native speaker.
- **Versioning**: a release script (`<Version>`, Unreleased to a version section, `Published_Docs/`, tag) plus a build stamp from the latest tag and commit count between releases.

## Requested, deferred
- **API access for scripts and other systems** (requested 2026-10-13): sign-in today is interactive only (Windows, SAML, OIDC). Needs a design: per-user API keys or service tokens, their permissions (a role, like a user), expiry and rotation, storage (hashed), and audit logging of their use.

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

## Found in the real CyberArk data (checked 2026-10-13)
Self-Hosted: EVD `CyberArkSH`, loaded 2026-10-13. Privilege Cloud: the export CSVs in the configured folder, because staging still held September data (first item).
- **Privilege Cloud import failing** since 2026-10-10. First, `Export_PlatformsList.csv` was not found, and the configured `Export_UsersList.csv` and `Export_SafesList.csv` weren't in the folder either; the files are there now. Then (found 2026-10-14) the import failed at Users with Msg 7301. The Users export had changed from 8 columns to 10 (Email gone; Enabled, Suspended and Location added), and Groups from 6 to 7 (Directory added). The import loaded into fixed-column tables, so any change in the column count failed it. **Fixed by D-228 (script 22, ships with this PR):** the import reads columns by header name, and Test checks each file's header. After deploying, re-run the import by hand and check that `stg_pc_*` holds today's data; then delete this item.
- **No `AddressDerived` Account→Target links** although about 2,450 active accounts' Address equals a Target identifier (D-193). Probably `usp_RunFullLoad` hasn't completed since D-193 (the Agent job has no step history); confirm after the import runs.
- **Self-Hosted pivot property names confirmed**: `UserName`, `Address`, `PolicyID` exist with that spelling on every password object (`02_BlueTrack_Baseline_EtlLoads.sql:404`). Remove the PLACEHOLDER comment in the next script that touches `usp_Load_FactAccount`.
- **Self-Hosted `PlatformKey` is always NULL**: there's no Self-Hosted platform list, so `PolicyID` matches nothing in `dim_platform`. Decide on a source (e.g. seed `dim_platform` from the distinct `PolicyID` values).
- **Self-Hosted properties the pivot ignores**: `LogonDomain` (could fill `PlatformLogonDomain`), `CPMDisabled`, `DeviceType`, `CreationMethod`, and the linked logon/reconcile account properties (`ExtraPass1*`, `ExtraPass3*`). The EVD has no CPM status or last-change properties, so those stay empty for Self-Hosted.
- **Q-27 columns never filled**: the `CAFLock*` and `CAFAccessed`/`CAFNew`/`CAFRetrieved`/`CAFModified`/`CAFIsRequestNeeded` columns on `stg_sh_files` exist, but the EVD import doesn't select them.
- **Privilege Cloud columns always empty** in the current export: `LastReconciled`, `RemoteMachines`, `Deleted`; `LastVerified`, `Platform_LogonDomain` and `CPMStatus` are mostly empty.
- **Privilege Cloud exports with no importer**: `Export LDAP Group Members.csv` and `Export_ApplicationsList.csv`. Decide whether either is useful.
- **Direct Account→Target links**: neither CyberArk source lists remote machines (Privilege Cloud `RemoteMachines` is empty; Self-Hosted has no such property), so the Address-derived links (D-193) are the CyberArk side. The `AccountTargetMap` data feed exists (`App/Api/DataFeeds/FeedColumns.cs:21`); what's missing is a non-CyberArk system to supply it.
- **Drop `dbo.stg_discovered_accounts`** in a new script: empty, and nothing references it (superseded by `web.discovered_account`).

## Process and tooling
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
