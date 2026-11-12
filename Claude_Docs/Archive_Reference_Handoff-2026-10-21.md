# BlueTrack: Handoff, 2026-10-21

> **Stage: Archive (superseded by `Archive_Reference_Handoff-2026-11-05.md`, then `Archive_Reference_Handoff-2026-11-06.md`, then `Reference_Handoff-2026-11-10.md`).** Kept for the history up to 1.5.1 and the 2026-11-03/04 rounds.
>
> **Stage: Reference (point-in-time snapshot).** Written at the end of the 2026-10-21 session for whoever picks the project up next (a person or another Claude session), brought up to date after the session's second batch (D-289 to D-297) and again on 2026-10-23 (release 1.5.1, the dev-site upgrade, SQL Server Audit live, and the customer upgrade questions in section 2a), and on 2026-11-03 (Archived status, Safe Access Policies and compliance review; section 2b, then section 2c), and on 2026-11-04 (Home and the user's notes; section 2d), and again later on 2026-11-04 (the layout review, the dev-site install of `main` and the installer fix; section 2e). Supersedes `Archive_Reference_Handoff-2026-10-19.md` and `Archive_Planning_Handoff-2026-10-18.md`. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them.

## 1. Where things stand

- **Released: v1.5.1** (2026-10-22; signed tag on `477fd01`, GitHub verified the signature; GitHub release; rebuilt `Published_Docs` and `Sbom/`). It holds everything in section 2 marked 1.5.1. Before it: v1.5.0, v1.4.0 (EVD removal, D-278) and v1.3.4.
- **Release numbers go up by 0.0.1 only**, whatever a release holds (the user, 2026-10-22). A **v1.6.0** was published first by mistake and withdrawn within minutes: its GitHub release and tag were deleted and PR #232 put `<Version>`, the notes, `Published_Docs` and `Sbom/` back before 1.5.1 was made. No v1.6.0 exists.
- **`main`** holds PRs #235 to #274 (2026-11-03 and 04; #241 to #245 are section 2c, #246 to #271 section 2d, #272 to #274 section 2e). **Unreleased** lists all of it; the next release is **1.5.2**, not released yet. Its Upgrade Guide section 5 row is written and covers scripts 58 to 65, Home at `/` (the Dashboard moved to `/dashboard`), AD discovery's new run time and domain ticks, and the optional `Db.PowerBiAccess` step. #273 and #274 need no upgrade step.
- **Dev site** (`https://bluetrack.company.com`) runs **`main` at #273** (`1.5.1-dev.93+14a8332`), installed 2026-11-04 with the saved answers, without `-Force`: pre-deployment backup taken and verified, scripts 56 to 65 applied, `Db.AppPoolAccess` re-run (Manual/02 and 08), `Db.PowerBiAccess` run for a lab account (Manual/09; the account is now in the saved answers), `Db.SqlServerAudit` audit `STARTED`, smoke test healthy. `Test-BlueTrackHealth.ps1` 13 pass, 0 fail (the Import and Load job had not run yet since the install); `npm run test:live` 6/6. `Test-BlueTrackSso.ps1` was **not** run. #274 (installer only, and a no-op C# change) is not on the site and doesn't need to be.
- **SQL Server Audit is live on the dev host** (`Db.SqlServerAudit`, D-295): Windows audit policy set, the SQL Server service account granted *Generate security audits* and the Security-log registry key, SQL Server restarted by the user, audit `STARTED`. Checked with an UPDATE of `web.audit_event` that matched no rows and was rolled back: Security log event 33205 from `MSSQLSERVER$AUDIT`, with the statement and the login. No data or seal changed.
- **AuditChainBroken** is turned on for the dev site (through the admin API, so it is in the Audit Log); no target role, and nothing is sent without SMTP.
- **Tests** (last full local runs, 2026-11-04): web 936 and E2E 110 on #273's branch, API 1,468 on #271's branch (#273 changed no API code; #274 ran only the two `SignInReject` tests), all passing. CI green on every PR.
- **Decision register:** the last row on `main` is **D-331**; next is D-332. **Database scripts:** the last on `main` is `65`; next is `66`. `Database/Manual/09` (the Power BI reader role) is the last manual script.

## 2. What the 2026-10-21 session delivered

| Release | Piece | Decisions |
|---|---|---|
| 1.3.4, 1.4.0 | Self-Hosted Direct phases 3 to 5: the Load reads the REST fields; the EVD source and the shadow pull removed; the installer refuses a site still on the EVD; Upgrade Guide with a version table and a rehearsed backup/restore rollback | D-272 to D-278 |
| 1.5.0 | Resolve/Undo for Self-Hosted members; classic PVWA account details; AD users of Safe-member groups; CyberArk Applications; Progress Rules; editable Blueprint stages; rejected-sign-in logging and alerts, SAML certificate reminder; CPM health | D-279 to D-288 |
| 1.5.1 | The user's answers to the questions backlog and the requirements review | D-289, D-290 |
| 1.5.1 | Per-field permissions seeded, then corrected: Source of Record (Admin), Actual Completion Date (Admin, Approver) and Risk Level (Admin, Analyst) need their own permission; read-only on the account page, not offered in bulk edit | D-291, D-296 |
| 1.5.1 | **Remember each browser's last method** (Admin > Identity Providers) | D-292 |
| 1.5.1 | **Platform Mapping suggestions** (other source first, then keywords; group platforms skipped; Use / Fill in, saved only by Save) | D-293 |
| 1.5.1 | **Number and date formats**: dates and numbers on the pages in the viewer's format; **Number and date format** (Global Application Configuration, default en-US) for emails and run summaries | D-294 |
| 1.5.1 | **Tamper-evident audit log**: seals (HMAC chain), **Verify** on the Audit Log page, AuditChainBroken notification, purge keeps the chain whole; **SQL Server Audit** to the Windows Security log (installer step `Db.SqlServerAudit`, Manual/07) | D-295 |
| 1.5.1 | **CycloneDX SBOMs** in `Sbom/` (`Tools/New-BlueTrackSbom.ps1`, run by the release tool) | D-297 |
| 1.5.1 | Upgrade Guide section 5 row for 1.5.1 (PR #230, renamed from 1.6.0 in #232) | |
| Unreleased (1.5.2) | `Db.SqlServerAudit` compares SIDs: `secedit` lists a domain account by its bare name (`MSSQL_Service`), so the old check missed it, re-granted on every run and wrongly asked for a SQL Server restart (PR #234; `Reference_Lessons-Learned.md`, 2026-10-22) | D-295 |
| (tests) | Playwright spec for the round; live read-only checks of a deployed site (`npm run test:live`, `App/E2E/live`) | |
| (docs) | `User_Docs/` updated for all of the above; the user-docs backlog has one row (the 1.5.2 fix, covered by its Release Notes line) | |

## 2a. Customer questions answered on 2026-10-22 (no code changed)

A customer site runs **1.0.7**. Answers given, from the code and the docs at each tag:

1. **Upgrade order** (`User_Docs/Admin_UpgradeGuide.md`, sections 2 and 5): Self-Hosted Direct first appeared in 1.3.0, so on 1.0.7 Self-Hosted can only come from the EVD. Self-Hosted off: 1.0.7 straight to 1.5.1. Self-Hosted on: 1.0.7 to **1.3.4**, move to Direct there (shadow pull, 7 clean nights, 1.3.4's Deployment Runbook), then to 1.5.1. 1.0.7's `Update-BlueTrack.ps1` already takes `-Release` and requires a signed tag. After 1.5.1's `Db.SqlServerAudit` the re-run's restart message is the bug fixed for 1.5.2; a `STARTED` audit is working.
2. **Adding CyberArk CP during an upgrade:** no installer answer. The CP backend is compiled in when `NetStandardPasswordSDK.dll` exists at build time (D-169, `App/CyberArkSdk.targets`; `CyberArkSdkPath` overrides the default folder), and the installer builds the API on the server (`Build.Api`). So install the CyberArk CP with its SDK before the upgrade, check `Prereq.CyberArkSdk` and that `Build.Api` has no "SDK not found" message, then set the App ID on Admin > Secrets Store Configuration, Test and Make Active. Left to the customer's CyberArk team: the application ID's authentication (hash authentication would change with every BlueTrack build).
3. **Pull now doesn't run the load.** It runs only the PVWA pull into staging (`SelfHostedDirectRunner`); the accounts table changes at the next Import+Load job, or after `sp_start_job` / `EXEC usp_RunNightlyImportLoad` once the pull has succeeded (`Admin_OperationsGuide.md`, "Re-running it manually"). The user was offered a sentence saying so in the Settings Reference, or the app running the load after a manual pull; no answer yet.
4. **EVD-to-REST differences on 1.3.4** are on Admin > Data Sources, **Shadow comparison** (`ShadowComparisonPanel.vue` at `v1.3.4`), not under Reports; 1.4.0 removed the panel. For SQL: `dbo.shd_comparison_difference` (last night's result), `dbo.vw_shd_comparison` (live, same rules), or a full outer join of `stg_sh_files` + `stg_sh_objectproperties` with `stg_shd_accounts` on `<SafeID>_<FileID>` = `AccountId` (the view's own filters, in `Database/43_*` at `v1.3.4`).
5. **A 1.3.5 maintenance release is possible but not started.** It would need: a `release/1.3.x` branch from `v1.3.4`, never merged to `main` (a new decision, D-298); `release/**` added to that branch's `ci.yml` triggers; the release tool taught a branch (it requires `main`, a version above the newest tag and a commit on `origin/main`) or a one-off manual release; `gh release create --latest=false` so `Update-BlueTrack.ps1` without `-Release` doesn't pick it; **no new numbered SQL scripts** (47 to 55 belong to 1.4.0 and later); and on `main`, the Upgrade Guide's EVD stop changed to 1.3.5 plus a 1.3.5 section in the Release Notes. The user hasn't said what 1.3.5 would contain.

**Changed on the dev site by request (data, not code):** Platform Mapping (58 platforms mapped; the group platforms and one test platform left unmapped on purpose); CpmHealth, SignInRejected and SamlCertificateExpiring turned on but nothing is sent (**no SMTP and no recipients**; on hold until a test mail environment exists, D-289); two draft Progress Rules saved **disabled** (the user said not to enable them).

## 2b. What the 2026-11-03 session delivered (Unreleased, for 1.5.2)

The user asked for three things, in this order:

| PR | Piece | Decisions |
|---|---|---|
| #236 | **Account groups on the 1.3.4 shadow comparison** (the one customer still on 1.3.4): the 3 account-group objects that keep it from a clean night are expected; PVWA's account list doesn't return a group's own object, so mark them Expected; they drop out at the first Direct load. Doc only (Upgrade Guide). | |
| #237 | **Archived status** (`Database/56_*`): out of scope for the KPIs, no Risk Exception, its own count on KPI Summary and Dashboard; left out of "in scope" there and on Trends. Progress Rules get an action, **Archives accounts** (any stage, applied before the stage rules), and two fields: **Folder** (read from PVWA classic details) and **Why the CPM doesn't manage it** (the CPM disable reason, for example LCD's `ADArchive`). An account stays Archived until someone changes its status; a hand change exempts it from that rule until the rule stops matching. Optional **re-read of account details** on CyberArk sources (off by default; days 1 to 365, up to 1 to 100,000 accounts per pull), because a folder move may not change the account's `categoryModificationTime`. | D-298, D-299 |
| #238 | **Safe Access Policies** (`Database/57_*`, `Design_Safe-Access-Policies.md`): Safe roles (sets of Safe permissions); policies chosen by Safe name pattern, first enabled one by order; Required and Forbidden member rules with `{SafeName}` and `*`, optionally requiring people in the group. Checked in the nightly load before the Progress Rules, or with **Check now**; results on **Reports > Safe Access**. **Compliance review** once per account (account page, or a whole Safe from the report), cleared when the account changes Safe or someone clears it. Two Progress Rule fields: **Safe access compliant**, **Compliance reviewed**. New permissions `ManageSafeAccessPolicies` (Admin) and `ReviewAccountCompliance` (Admin, Approver). A disabled example policy and an "End user" role are seeded. | D-300, D-301 |

## 2c. Second round, 2026-11-03 (Unreleased, for 1.5.2)

The user's answers to the open items, built on topic branches; merged 2026-11-03.

| PR | Piece | Decisions |
|---|---|---|
| #241 | The Audit Log E2E tests wait for the Event Type choice (a text box until the event-type list arrives, so `selectOption` could land on it); `Planning_API-Access-For-Scripts.md` renamed `Design_API-Access.md` (D-243's phases 1 to 5 are built) | |
| #242 | **Pull now starts the Import and Load SQL Agent job** when the pull succeeds. The app's SQL account can't run the Import and Load itself (TRUNCATE of staging, BULK INSERT of Privilege Cloud files), so the user chose the job: the app's login joins msdb's `SQLAgentOperatorRole` (`Database/Manual/08`, run by `Db.AppPoolAccess`; re-run it on upgrade). Script 58 records the request and any refusal; Data Sources shows the load's result from `etl_run_log` | D-302 |
| #243 | **Reload Rights reads a Windows user's groups from AD** (`tokenGroups` through the LDAP domains), and keeps reading AD at each rights refresh until the next Windows sign-in or 7 days; AD unreachable falls back to the sign-in's groups and says so. OIDC/SAML: no live lookup, the page says to sign out and back in | D-303 |
| #244 | Tracked-data pages need their permission at the router (Access Denied instead of "Request failed: 403"); the remaining status-code-only save failures show the server's reason | D-304 |
| 2026-11-03-first-page | A user who can't open the Dashboard starts on the first page they may open, in the top nav's order (the seeded Auditor: the Audit Log); the Reports entry opens the first report they may see; the top nav and Reports hub hide what they can't open. A dedicated landing page was considered and not built (the user asked "Maybe a dedicated landing page is needed?") | D-305 |

Answers that needed no code: no 1.3.5 maintenance release (not needed); the four 2026-11-03 design choices confirmed as built; PSM for Privilege Cloud goes into Privilege Cloud Direct (`Planning_Backlog.md`); inline Extend/Revoke on the worklists and keeping typed text across an idle sign-out are left out for good.

## 2d. The user's notes, 2026-11-03 and 04 (Unreleased, for 1.5.2)

The user pasted 24 backlog notes and 5 KPIs, asked for them to be worked in order without waiting, and answered the questions they raised. Each note's outcome is in `Archive_Planning_User-Notes-2026-11-03.md`. All merged by 2026-11-04.

| PR | Piece | Decisions |
|---|---|---|
| #246, #248 | **Home** (`/`): everyone with a role starts there; it offers only what the user may open. The Dashboard moved to `/dashboard`. (#246 first sent a user without ViewDashboard to their first page; the user then asked for a landing page.) | D-305, D-307 |
| #247 | Quick fixes from the notes: Recalculate Now, the white Pull details dialog in Dark (a test now rejects undefined color variables), the Secrets Store's Credential box only where it's read, search 400 ms after typing | D-306 |
| #249 | The user's notes checked against the code and put in the backlog |  |
| #250 | **Safe** on Account Detail and the Accounts list; a **Safe details** page (its application, its accounts) | D-308 |
| #251 | Optional **Reason** on CyberArk CP and CCP retrievals | D-309 |
| #252 | Adding the Credential Provider after install (re-run `Build.Api`) | D-310 |
| #253 | `VERSION` and `BlueTrack.version.txt` | D-311 |
| #254, #262 | PVWA behind a load balancer (sticky sessions); **Other PVWA addresses** with failover when one can't be reached | D-312, D-320 |
| #255 | Self-Hosted Direct **Test** is quick; **Test with a full read** is its own button | D-313 |
| #256, #263 | Admin > **Power BI**; the installer's `Db.PowerBiAccess` step (`-PowerBiReaderAccount`, `Database/Manual/09`) creates `BlueTrack_PowerBI_Reader` | D-314, D-321 |
| #257 | Bulk risk score override on Reports > Risk Score | D-315 |
| #258, #264 | A Target re-import updates its risk score, name, type and description | D-316, D-322 |
| #259, #266, #270 | **AD Account Discovery** on Admin > Data Sources: run time (04:30), Run now, run log; LDAP domains ticked **Use as a data source**; domains for discovery only (`UseForIntegration` off) | D-317, D-324, D-328 |
| #260, #265 | Accounts **Export** and **Import changes** (CSV or Excel; an opt-in to clear a field when its cell is blank; risk score overrides) | D-318, D-323 |
| #261, #269 | CyberArk's built-in Safes recognised as internal; an option, off by default, to read them (classic Safe list plus Get Safe details) | D-319, D-327 |
| #267 | **DNA** account-list workbooks uploaded on Data Sources; Reports > **DNA Accounts**. The user's workbook is sensitive: only its structure and counts were read, and nothing from it is in the repo | D-325 |
| #268, #271 | The KPI funnel starts with **Everything discovered** (tracked, plus untracked AD and DNA accounts; dismissed AD candidates count; duplicates and linked accounts once) | D-326, D-329 |

The documentation pass after #271 (its own PR) added the new permissions, the Data Sources settings and `-PowerBiReaderAccount` to the admin guides, and a dated note for D-302 to D-329 in each `Design_` doc.

## 2e. Layout review and dev-site install, 2026-11-04 (Unreleased, for 1.5.2)

The user asked for a review of the page layout "for use and visability" (screenshots at 1440px, Dark and 390px), then "Make all suggested changes", then to deploy, merge and fix what the deploy turned up.

| PR | Piece | Decisions |
|---|---|---|
| #272 | Handoff and docs for D-302 to D-329 (section 2d's documentation pass) | |
| #273 | **Page layout**: system font; 75ch paragraphs; no ring round `<main>` after a route change; below 900px tables scroll inside the page, the hub sidebars go above it and the top nav wraps; the current area marked in the top nav (by path, `NAV_SECTIONS`) and in the hub sidebars, sub-pages too (`useSubnavCurrent`); Admin (5) and Reports (4) sidebars in titled groups, `REPORT_PAGES` in the hub's order (Overdue / At-Risk still first); the pager only with more than one page; Accounts' Deleted and Linked filters under **More filters**; Dashboard attention cards first, charts side by side, pie legends; Home cards with counts needing action; Account Detail field grid, sticky Save/Cancel, Delete under **Deletion** at the end; sign-in page without nav, one box per method; Admin in the top nav only with an Admin page. The user wasn't asked about the group names and members, wrapping rather than a menu button, or the 900px breakpoint (listed in D-330) | D-330 |
| #274 | **Installer prompts**: every `Read-Host` goes through `Read-BlueTrackAnswer`, which stops and names the parameter or `-ConfigFile` entry when `Read-Host` returns `$null` (no console input). Found by the install above: the saved answers predated `PowerBiReaderAccount`, and the run failed with "You cannot call a method on a null-valued expression" before any step. Also `_ =` on `SignInRejectionRecorder`'s fire-and-forget alert (CS4014, there since D-285) | D-331 |

The user-docs backlog has rows for D-330 (User Guide, Getting Started and the admin guides still describe the old layout in places, for example where Delete account sits) and D-331.

## 3. Open items

1. **Merged 2026-11-04:** #246 to #274. Nothing is open from the user's notes or the layout review.
2. **Release 1.5.2** when the user asks. The Upgrade Guide row is written. Then the release tool with `-Version 1.5.2`, and install it on the dev site (it already has every database script and `Db.AppPoolAccess` / `Db.PowerBiAccess` from `main`, so expect no new scripts).3. **Next round** (the user, 2026-11-03): the suggestions (application-owner emails, least-privilege view, sponsor view) and the nested AD group path on "Via AD group" (`Planning_Backlog.md`).
4. **Later** (the user, 2026-11-03): Privilege Cloud Direct, with PSM data for Privilege Cloud in it; the folder-move test in the lab (does moving an account to another folder change its `categoryModificationTime`? The user would move one in PrivateArk Client and run **Pull now**, then check read-only); review what the built-in Safes option reads, for usefulness (notes 22 and 23).
5. **Not proven live** (`Planning_Backlog.md`, "Built, but never proven"): Azure, AWS and Conjur secrets backends; every email (no SMTP); SIEM forwarding; OAuth2 SMTP; LDAPS from another machine and a real second AD domain; identity provider tokens for API access; the AD group read and CyberArk Applications on a large Vault; Pull now starting the real job (#242) and Reload Rights through the deployed site (#243); PVWA failover and a real load balancer; the CP and CCP Reason in a real audit log; Power BI actually reading through the `BlueTrack_PowerBI_Reader` role (`Db.PowerBiAccess` itself ran on the dev install, 2026-11-04); a pull with the built-in Safes option on; a DNA upload of a real workbook.
6. **User docs for D-330 and D-331** (`Planning_User-Docs-Backlog.md`): the User Guide, Getting Started and the admin guides where they describe the old layout or the installer's prompts.

## 4. Conventions that are easy to get wrong

All are in `CLAUDE.md` or the docs it links; these bit during this work.

- **Branches and PRs:** never on `main`; `YYYY-MM-DD-<topic>`; `gh pr merge N --merge --delete-branch`. Commit, push, PR, merge, deploy and release only when the user asks; approval for one piece doesn't carry to the next.
- **Ask, don't assume:** design choices go to the user as numbered options; keep their numbers (they shifted the numbering once in a reply: match answers by content and say so). Writes to the dev site's data need a go-ahead; read-only SQL and PVWA GETs don't.
- **Docs per change** (`CLAUDE.md` table): D-row, the subsystem's `Design_` doc, `Database/README.md` for a script, a line under Unreleased; the Upgrade Guide's section 5 row before each release, in its own PR.
- **SQL:** never edit a script that has run anywhere real (CI's test database counts once merged: script 51 was corrected by a new script 55, not edited). Redefining a procedure copies its latest version; `usp_PurgeAuditLog` was last redefined in script 54, `usp_RunFullLoad` in 50.
- **Audit log:** the app's SQL account can't change or delete audit rows or seals (Manual/02). Tests that purge must seal first (`AuditChainService.SealAllAsync`). Tests that delete their own audit rows break the shared test database's chain, so chain tests verify from their own first seal and look only at their own events.
- **Server-written text** uses `ServerTextCulture` (D-294); audit details, IDs, dedup keys and CSV/Excel stay invariant. In the SPA, `formatDate` is for display only: date inputs need `yyyy-mm-dd`.
- **Stages:** use `dbo.fn_StartStageKey()` and `dbo.fn_MilestoneStageKey/Order(...)`, never a name or position (D-284). **Progress Rule fields** must agree in `fn_ProgressAccountFields`, `ProgressRuleFields` and `progressRuleLabels.js`.
- **Localization:** literal `t('...')` keys only, no unused keys, plurals `"one | many"` called as `t(key, { count: formatCount(n) }, n)`.
- **Live testing:** `Live-Testing.local.md` only for live work; never copy its details or real data values into tracked files.

## 5. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`); uses `BlueTrackTest_Local`: rebuild with `Tools/Reset-BlueTrackLocalTestDb.ps1 -Confirm:$false` (through `powershell -Command '& ./Tools/...'`) after a new or changed script. |
| Apply one script to the local test DB while iterating | replace `$DatabaseName$` with `BlueTrackTest_Local` and run `sqlcmd -S localhost -E -C -I -b -i <file>` with a Windows path (`cygpath -w`); rebuild the DB before committing. |
| Web / E2E | `npm run test` in `App/Web`; `npm test` in `App/E2E` (not while CI runs, including the run on `main` after a merge). |
| Live checks of the dev site | `BLUETRACK_LIVE_URL=https://<site> npm run test:live` in `App/E2E` (Windows sign-in as you; Verify adds one audit entry); `Tools/Test-BlueTrackHealth.ps1 -SiteUrl ...`; `Tools/Test-BlueTrackSso.ps1` with the CP parameters in `Live-Testing.local.md`. |
| Redeploy locally | A wrapper script (written with the Write tool; `printf` turned `\a` in `\answers` into a bell once) running `& .\Deploy\Install-BlueTrack.ps1 -ConfigFile .\Deploy\State\answers.BlueTrack.json`, **without `-Force`**, after CI on `main` has finished. The answers file now saves `InstallSqlServerAudit: true` and `PowerBiReaderAccount`. Run from this shell the installer has no console: since D-331 a missing answer stops it before any step and names the parameter to add. Auto mode may block the run (and `gh pr merge`) until the user allows it. |
| SBOMs | `Tools/New-BlueTrackSbom.ps1` (after package changes; the release tool runs it). |
| Release | Version = newest `v*` tag + 0.0.1. Upgrade Guide row PR first; then `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge after CI, `... -Tag -Push`. Signing key (SSH), python-docx and `gh` are set up on this host. |
| Scripted writes to the API | Windows auth plus the header `X-Requested-With: XMLHttpRequest` (D-197); without it a PUT returns 403. |

## 6. Things that cost time this session

1. **Python edit scripts:** `"""` in C# or SQL ends a Python `"""` string; backslashes are escapes (`\N` broke one); backticks inside a bash heredoc broke the shell's parse once. Write the script with the Write tool, or use the Edit tool.
2. **CRLF files:** the working tree is CRLF (autocrlf); a Python `split('\n')` on a CRLF file leaves `\r`; read with `newline=''`, normalise, write back with the file's own ending (`crlfio.py` in the session scratchpad did this).
3. **`sqlcmd`** needs `-C`, and `-I` for QUOTED_IDENTIFIER. **`| Select-Object -First 1`** on a native command leaves a failing `$LASTEXITCODE` (`Reference_Lessons-Learned.md`).
4. **Playwright:** `getByLabel` on a `<label>` wrapping a `<select>` can match several elements or none; use `getByRole('combobox', { name, exact: true })`. A Vue page's table-cell text in a `<p>` isn't at the start of the line (`/^Last check/` failed). `page.request` doesn't trust a CA the browser trusts (live checks).
5. **The shared test database:** other suites delete audit rows, so "the chain is intact" can't be asserted globally; process-wide state (the format culture) mustn't be changed by a test.
6. **The CI runner went offline again** on 2026-10-22: a PR's run stayed queued for over 30 minutes while GitHub listed the runner offline and its Windows service showed Running. Restarting the service (with the user's go-ahead) fixed it at once. A `gh pr checks --watch` also died on a dropped connection; poll `gh run view <id> --json status,conclusion` instead, and look the run ID up only after the run exists. It happened again on 2026-11-03 (run queued, runner idle) and the same restart fixed it. On 2026-11-04 the runner's broker connection dropped mid-run (socket errors in its log; the run stayed "in progress" with its job already finished, or never started); `gh run cancel <id>` then `gh run rerun <id>` fixed it each time without a service restart.
7. **Stacked PRs get no CI** (2026-11-03): CI runs on `pull_request` to `main` only, so a PR based on another PR's branch shows no checks. `gh workflow run CI --ref <branch>` runs it by hand. Before merging the base PR, `gh pr edit <next> --base main`.
8. **A new permission and the rights cache:** after a script grants a role a new permission, a user's cached rights (`web.distributed_cache`, up to 15 minutes) still lack it, so tests got 403. Rebuilding the local test database fixed it.
9. **Dapper and positional records:** Dapper can't map a positional record that also has `init` properties; `SelfHostedDirectRepository` maps a small class (`CachedDetailRow`) instead.
10. **`sed -i` in Git Bash turns a CRLF file into LF** (2026-11-04, on `Install-BlueTrack.ps1`); restore CRLF before committing, or use the Edit tool. Appending lines to a CRLF file from the shell leaves it mixed (`themes.css`).
11. **New installer answers after an upgrade:** an answers file saved before a new prompt existed lacks its answer, so an unattended install asks for it (D-331 now stops with its name). Check `Deploy/README.md`'s parameter table against the saved file's keys before a redeploy.

## 7. Where to look

- Rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`.
- Tamper evidence: `Claude_Docs/Design_Audit-Logging.md` ("Tamper evidence (D-295)"), `App/Api/Audit/AuditChain.cs`, `Database/54_*`, `Database/Manual/07_*`, `Deploy/Modules/BlueTrack.Database.psm1` (`Install-BlueTrackSqlServerAudit`).
- Formats: `App/Api/Formatting/ServerTextCulture.cs`, `App/Web/src/utils/formatDate.js`, `formatLocale.js`; `Design_Localization.md`.
- Platform Mapping suggestions: `App/Api/Data/PlatformMappingSuggester.cs`.
- Archived status and archive rules: D-298, D-299; `Database/56_*` (`fn_ArchiveRuleMatch`, `usp_Load_AccountProgressAutoAdvance`). Safe Access Policies: `Claude_Docs/Design_Safe-Access-Policies.md`, `Database/57_*` (`usp_Check_SafeAccessPolicies`), `App/Api/Controllers/SafeAccessPoliciesController.cs`.
- Home and Safe details: `App/Web/src/views/Home.vue`, `SafeDetail.vue`, `App/Api/Controllers/SafeDetailsController.cs`. Power BI: `PowerBiController.cs`, `Database/Manual/09`. AD discovery and DNA on Data Sources: `AdDiscoveryRunner.cs`, `AdDiscoveryController.cs`, `DnaReportReader.cs`, `DnaController.cs`, `AdDiscoverySection.vue`, `DnaSection.vue`. Accounts import: `AccountProgressImportService.cs`, `XlsxFileReader.cs`. KPI funnel: `KpiSummary` (`DiscoveredAccounts`).
- Layout (D-330): `App/Web/src/assets/themes.css` (narrow-screen rules, `.hub-subnav*`, `.list-toolbar`, `.row-toggle`), `App/Web/src/App.vue` (`NAV_SECTIONS`, `hasAccess`), `composables/useSubnavCurrent.js`, the `GROUPS` in `AdminHub.vue` and `ReportsHub.vue`, `components/Pager.vue`. Installer prompts (D-331): `Read-BlueTrackAnswer` in `Deploy/Install-BlueTrack.ps1`.
- Self-Hosted Direct: `Claude_Docs/Design_CyberArk-Direct-Integration.md`. Notifications: `Design_Notifications.md`. Upgrades: `User_Docs/Admin_UpgradeGuide.md`. SBOMs: `Design_Dependencies-And-External-Integrations.md` ("SBOMs").

## 8. Suggested next steps

The user decides the order.

1. Check the first scheduled nights on the new dev build (Admin > Data Sources, including AD discovery at 04:30; Reports > CPM Health) and Verify on the Audit Log page.
2. Run `Tools/Test-BlueTrackSso.ps1` on the new build (the health check and `npm run test:live` passed on 2026-11-04).
3. (Done 2026-11-04: #246 to #274 merged; `main` at #273 installed on the dev site.)
4. The dev site has all of 1.5.2's code now, so the "not proven live" items that need only it can be tried there, with the user's go-ahead for data writes: Pull now's load, Reload Rights, PVWA failover, a DNA upload, the built-in Safes option, a real Safe access policy and an archive rule. Release 1.5.2 when the user asks.
5. The user docs for D-330 and D-331 (section 3, item 6).
6. The next round (section 3, item 3).
