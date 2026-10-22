# BlueTrack: Backlog

> **Stage: Planning.** Work that isn't built or proven yet. Carried forward from the 2026-09-16 survey (`Archive_Planning_Outstanding-Work-Survey-2026-09-16.md`); items that survey listed but the audit tracker records as resolved were left in the archive. Items tagged *(verify)* may already be done; confirm before starting. When an item ships, delete it here and record it in the relevant `Design_` doc.

## Requested, not started
- **Privilege Cloud Direct** (requested 2026-10-19, with Self-Hosted Direct, D-248): read Privilege Cloud through its API into `stg_pc_*` instead of the export folder, as a fourth option on the CyberArk sources page (already listed there, unavailable). Needs the Privilege Cloud API's logon (an Identity OAuth client, `Platform/Developer` and `PAM/Privilege-Cloud-Standard` in the Idira reference), a lab tenant, and the same change guard.

## From the requirements review (2026-10-21)
Gaps against the original design (commit f0561ce) and the Blueprint theme. Built from that review: Progress Rules (D-283), editable stages (D-284), rejected sign-in logging (D-285), the SAML certificate reminder (D-287); step-up MFA is left to the SSO provider (D-286).
- **Reload Rights re-maps the session's groups only** (D-13): a group change at the IdP or in AD shows after the next sign-in, not on Reload Rights. A live lookup (Graph, LDAP) would close it.
- **No SMTP on the dev site**: no host or recipients, so the overdue-exception alerts (D-19) and the other notifications reach nobody On hold until a test mail environment exists (D-289). The break-glass email is unproven for the same reason.
- **Stale docs**: `Planning_API-Access-For-Scripts.md` still lists phases 2 and 3 as to build (D-243 built them); rename it `Design_API-Access.md`.
- **PSM for Privilege Cloud** (D-288): the RequiresPsm rule condition reads the platform policy of a Self-Hosted Direct pull; Privilege Cloud's platform export has no PSM column, so it's empty there until Privilege Cloud Direct reads the platform policy.
- **Suggestion: email the application owner** (D-46 `OwnerEmail`) when an application-scoped exception comes up for review, not only the configured recipients.
- **Suggestion: least-privilege view**: who holds Manage Safe, Manage Safe Members or full rights, across Safes (the per-account "Who can use this account" and the Power BI entitlement view exist).
- **Suggestion: sponsor view of progress against target dates over time**, from Trends and `TargetRemediationDate`.

## Requested, deferred

## From Self-Hosted Direct (D-248, 2026-10-19)
- **Direct is unproven on a large Vault and over time** (Medium): measured on 40 Safes, 2,452 accounts and 43 users (about 8 seconds), through the REST API and through a fake PVWA, and compared once with the EVD. Not yet run as a scheduled night, against thousands of Safes (one call each) or users (one call each), or through the Agent job's Import gate on a real run.
- **REST doesn't return some EVD properties** (Low): `LastVerified` and `LastReconciled` (the EVD's `CAFLastUsedDate` stood in for the first), `RemoteMachines`, and `LogonDomain` on two PSM accounts (`DeviceType`, `CreationMethod` and the linked accounts come from the classic API since D-280). Checked 2026-10-20 against CPM-processed accounts on the lab's 14.6: `lastReconciledTime` and `status` are returned, `lastVerifiedTime` is not (verify history comes from the account activity call instead); see `Archive_Planning_Self-Hosted-Direct-And-EVD-Retirement.md`.
- **Windows and SAML logon to PVWA** (Low): Direct supports CyberArk and LDAP authentication only (both take the credential's user name and password).

## From the audit findings (moved here 2026-10-13, at the user's request)
- **Validate LDAPS against a remote DC** (Low): BlueTrack's `UseSsl` bind can't be proven on the dev host, which is the DC itself (binds from a DC to its own LDAPS fail; Authentik's LDAPS sync from another machine works). Run the check from DCACYBSQL01 (another domain): resolve and reach the DC on 636, trust its LDAPS certificate, bind with an account in the DC's domain (no trust between the domains, so not a trusted connection), then let AD Account Discovery run.
- **`ApplicationPoolIdentity` can't reach a SQL Server on another machine** (found 2026-09-24): `Login failed for user 'DOMAIN\HOSTNAME$'` although the login exists with the right SID. A known limitation of `ApplicationPoolIdentity` for outbound Windows authentication. Needs an environment decision: a dedicated domain account or gMSA for the App Pool (the Installation guide covers the gMSA).
- **CI runs approved pull-request code on this host** (Medium, accepted for now): the repo is public with a self-hosted runner. Mitigated: approval required for outside contributors, `permissions: contents: read`, `actions/checkout` pinned. Remaining: review a PR's changes (`ci.yml`, `package.json`, tests, scripts) before approving its run; making the repository private was declined.
- **Admin-only troubleshooting endpoints return exception text** (Info, kept on purpose): backup, credential test, SMTP test, feed test; that text is how an admin diagnoses the failure. Revisit only if those pages are opened to non-admins.

## Built, but never proven against a real live service
- **Azure Key Vault, AWS Secrets Manager, CyberArk Conjur** secrets backends: tested only against unreachable placeholder endpoints (error handling, not a real secret round-trip). CyberArk CP, CCP and Windows DPAPI are verified live. The 2026-10-12 review's hardening is done (D-215: User-scope DPAPI for their stored credentials, explicit Azure/AWS timeouts, Conjur's Base64 token header as CyberArk's own .NET client sends it); each still needs a real round trip before use.
- **Installer on a fresh server**: `Db.AppPoolAccess` (D-171) and `Iis.Reset` (D-175) have only run by hand or with `-WhatIf`; the new `Iis.SiteRestart` (D-188) ran on the dev host only. DCACYBSQL01's app pool SQL login was granted by hand. Check all three on the next real install.
- **AD Account Discovery multi-domain**: tested with a fake two-domain directory (D-214, `AdAccountDiscoveryMultiDomainTests`); still unproven against a real second domain (DCACYBSQL01's domain could be one, once LDAPS works).
- **Account classification reads** (D-233): proven against the lab domain on 2026-10-21 with a trusted connection (Use SSL on): a discovery run read the allow-listed attributes (`adminCount`, `employeeType`, `description`) and `employeeID` back for all 10 discovered accounts and the 4 owners, in seconds; a schema without Exchange's `extensionAttribute1`-`15` doesn't break the read; the Attribute, Name and OU rules and the owner from a stripped `adm.` prefix all worked. Owner by employee ID proven the same day (svc.ces, matched by no name rule, got jesse through a shared `employeeID`). The explicit-credential bind (a CP credential) failed because the Vault object's user name wasn't the account's 20-character sAMAccountName or its UPN (`Reference_Lessons-Learned.md`, 2026-10-21); the password itself was checked outside BlueTrack. With D-274 (the bind name built as a UPN from the account's LogonDomain) the same CP credential bound and discovery ran, 2026-10-21. Still unproven: LDAPS from another machine, the run time on production's group set, and owners outside the Search Base.
- **SIEM forwarding** (D-234): tested only against an in-test TCP/TLS collector on 127.0.0.1. With the real SIEM, check octet-counting vs LF framing, CEF parsing of `rt`, `externalId`, `suser`, `cs1`-`cs3`, `c6a1` and severity, the collector certificate under the chosen trust mode (and revocation reachability), the firewall port, the 8 KB message size, and handling of a duplicate `externalId` after a retry (delivery is at-least-once).
- **OAuth2 sign-in for SMTP** (D-223): Microsoft 365, Google Workspace and Generic OAuth2 are built from each provider's documentation and tested against fakes only. Google is the least certain (its docs never show the service-account flow and SMTP together).

## Found in the real CyberArk data (checked 2026-10-13)
Self-Hosted: EVD `CyberArkSH`, loaded 2026-10-13. Privilege Cloud: the export CSVs in the configured folder (loaded since D-228, 2026-10-14).
- **Nested AD group path** (D-281): "Via AD group" names the Safe's group; a person in a nested group isn't shown the group in between.
- **Direct Account→Target links**: neither CyberArk source lists remote machines (Privilege Cloud `RemoteMachines` is empty; Self-Hosted has no such property), so the Address-derived links (D-193) are the CyberArk side. The `AccountTargetMap` data feed exists (`App/Api/DataFeeds/FeedColumns.cs:21`); what's missing is a non-CyberArk system to supply it.

## Process and tooling
- Playwright E2E has a known recurring flaky-proxy issue, accepted as a limitation (see `Reference_Lessons-Learned.md`).
