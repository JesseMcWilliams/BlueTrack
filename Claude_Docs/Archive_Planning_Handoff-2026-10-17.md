# BlueTrack: Handoff, 2026-10-17

> **Archived 2026-10-18:** superseded by `Planning_Handoff-2026-10-18.md`, which carries its open items and gotchas.
>
> **Stage: Planning.** Point-in-time snapshot for the next Claude session, written 2026-10-17. Supersedes `Archive_Planning_Handoff-2026-10-15.md` (PRs #124 to #142). Once every open item below is closed, rename this to `Archive_Planning_Handoff-2026-10-17.md` with `git mv`.

## What was done since the last handoff
| PR | Decisions | What |
|---|---|---|
| #143, #144 | (none) | Handoff update; release notes' Upgrading lists scripts 11 to 26 and the Hosting Bundle by hand. |
| #145, #147, #152 | (none) | Releases v1.0.5, v1.0.6 and v1.0.7 (`Tools/New-BlueTrackRelease.ps1`, D-226). |
| #146 | D-235 | `Update-BlueTrack.ps1` leaves the download folder's permissions alone unless `-LockDownFolder`. |
| #148 | D-236 | Feed accounts accept a user name plus address of up to 400 characters. |
| #149 | D-237 | Skip group rows on an account inventory feed. |
| #150 | D-238 | Assign an account to an application; its exceptions cover the account. |
| #151 | D-239 | Bulk-assign accounts to an application (bulk edit and a CSV import). |
| #153 | D-240 | Local test runs use `BlueTrackTest_Local`; CI keeps `BlueTrackTest`. `Tools/Reset-BlueTrackLocalTestDb.ps1` builds it. |
| #154 | D-241 | `AccountAssignments` data feed: the D-239 CSV import on a schedule. Script 30. |
| #155 | D-242 | Installer: the Hosting Bundle needs ASP.NET Core runtime 10.0.13 or later (`$script:MinimumAspNetCoreRuntime`). |
| #156 | (none) | `Planning_API-Access-For-Scripts.md`: options, proposed design, eight questions. |
| #157 | (none) | Handoff refreshed through #156. |
| #158 | D-243 | API access phase 1: API keys (`web.api_key`, hash only), `POST /api/auth/token`, 10-minute HS256 token, bearer middleware (not an authentication scheme), `/api/admin/api-keys`, refusal throttle. Script 31. |
| #159 | D-243 | Admin > API Keys page; API key default lifetime setting (90 days, 1 to 365); `Planning_Questions-Backlog.md`. |
| #160 | D-243 | Rotate; `ApiKeyExpiring` notification (30 and 7 days); per-key rate limit (600 a minute, a setting). Script 32. |
| #161 | D-243 | Option B: `idToken` from an enabled OIDC provider; API client mappings (client or group, lowest order wins, never Admin); API callers' rights looked up per request. Script 33. |
| #162 | (none) | `User_Docs/Admin_ApiAccess.md` (calling the API from a script); handoff. |
| #163 | D-243 | The user's answers: owner email on a key; owner emailed the lead time (7 days, a setting) before expiry and on the day, admins on the day (`ApiKeyExpiring`, `ApiKeyExpired`; replaces the 30/7-day emails); edit a key in place; a per-key rate limit; the API Access guide joins `build_published_docs.py`. Script 34. |
| #164 | (none) | Handoff through #163. |
| #165 | D-243 | Expired API keys are deleted automatically after a setting (365 days; 0 keeps them): `ApiKeyPurgeService`, audited by the system user. Script 35. |
| #166, #167 | (none) | Handoff through #165; fake-provider tests accepted for option B for now. |
| #168 | (none) | The delete/undelete E2E flake fixed (90 s limit; the test undeletes TestAccount01 first). |
| #169 | (none) | Backlog: the release script and nightly job items removed (proven). |
| #170 | (none) | **Release v1.1.0** (signed tag, GitHub release, `Published_Docs` rebuilt including the new `API_Access_Guide.docx`). |
| #171 | (none) | Handoff through v1.1.0. |
| #172 | D-244 | Self-Hosted platforms (one per PolicyID), `PlatformLogonDomain` from LogonDomain, `AutoManaged`/`ManualReason` from CPMDisabled; `stg_discovered_accounts` dropped. Script 36. |
| #173 | D-245 | Link two existing targets by hand (Edit Target); deleting a target with Target Match Review rows no longer fails; the filtered list pages drop a slow earlier reply (`useLatestRequest`). Script 37. |
| #174 | (none) | Handoff through #173. |
| #175 | D-246 | "Use browser default" in the Language picker (`DELETE /api/me/preferences/{key}`). |
| #176 | D-247 | Admin > Platform Mapping page (account type and source of record per platform; optional fill of accounts that have none). |
| this PR | (none) | The questions backlog (9 questions), the server-side localization proposal, handoff through #176. |

## State
- **`main`** at the #176 merge (`1e74d22`; the last release is tag `v1.1.0`) plus this PR. Last full local runs 2026-10-18: xUnit 1114/1114, Vitest 457/457, Playwright 95/95. Last full local runs 2026-10-17 against `BlueTrackTest_Local`: xUnit 1106/1106, Vitest 441/441, Playwright 91/91. CI passed on every PR above.
- **Dev site:** redeployed to `main` as of #165 (`1.0.7-dev.26+e5f53b2`, scripts up to 35). Health 14 pass, 0 fail. Live: a key with an owner and a limit of 3 (calls 4 and on answered 429), edited to Auditor with a new owner and expiry (the same token then acted as Auditor), then deleted; Windows sign-in unchanged; SAML and OIDC sign-in pass.
- **Released:** v1.1.0 (2026-10-17: D-241 to D-243). **Unreleased** since: D-244 to D-247 and the stale-reply fix; Upgrading: scripts 36 and 37. The dev site runs `main` as of #176 (`1.1.0-dev.13+1e74d22`, scripts up to 37), health 14 pass, 0 fail; the Platform Mapping page lists 53 platforms, all unmapped (nobody has mapped any: question 2).
- **Off until configured on the dev site:** the D-222 notification types, `ApiKeyExpiring`, `ApiKeyExpired`, account classification, SIEM forwarding. No API keys or client mappings exist there.

## Open items
1. **Live test of option B (identity provider token) not done; the user accepted the fake-provider tests for now (2026-10-17).** The code is tested against a fake provider with real RSA signatures only; `OidcIdpMetadataSource` has never run against a real provider. The lab script was refused by the permission system (credential exploration) and authentik refused every client-credentials style with `invalid_grant`. To close it later: a token and client ID from the user and a small `Tools/` script that exchanges it (not built), or a permission rule for one narrowly written lab script.
2. **Questions backlog:** `Claude_Docs/Planning_Questions-Backlog.md` holds 9 numbered questions written 2026-10-18 while the user was away (release and version, the platform mapping data and assumptions, Self-Hosted properties, server-side localization, real-data items not built, target link assumptions, notification types on the dev site, and the live tests only the user can do). Ask them, record the answers, change what differs, and delete each one answered.
3. **Next release:** `Tools/New-BlueTrackRelease.ps1 -Version x.y.z -Push`, merge its PR, then `-Tag -Push` (D-226); it worked first time for v1.1.0. Wait for the PR's check to finish and gate the merge on it passing.
4. **LDAPS from DCACYBSQL01** (backlog): the user runs `Tools\Test-BlueTrackLdaps.ps1` there, then adds the domain in BlueTrack.
5. **Live tests still owed** (backlog, "Built, but never proven"): SIEM forwarding, account classification against the real AD, OAuth2 SMTP, break-glass email, Azure/AWS/Conjur. (The release script and the nightly job are proven and were removed from this list on 2026-10-17.)
6. **Backlog** (`Planning_Backlog.md`): server-side localization (proposal written: `Planning_Server-Side-Localization.md`; deferred until a second language is planned) and the real-data findings still open, now questions 4 and 6 (Self-Hosted properties with no home, the unread `CAF*` columns, the unused Privilege Cloud columns and exports).

## Gotchas for the next session
- **Local tests use their own database (D-240).** `dotnet test` and `npm test` default to `BlueTrackTest_Local`; build or reset it with `Tools\Reset-BlueTrackLocalTestDb.ps1` (also after a new numbered script). CI owns `BlueTrackTest`, so a local run no longer breaks a CI run's data. Still don't run Playwright locally while CI runs: the CPU and the ports (4173, 7033) are shared. Check with `gh run list --status in_progress`.
- **Redeploy from a clean `main`.** The installer builds the working copy. From Bash: `powershell -Command "& ./Install-BlueTrack.ps1 -Step Db.Backup,Db.Migrate,Build.Api,Build.Web,Iis.SiteRestart"` (with `-File` the step list arrives as one string). Add `Db.NightlyJob` when the job script changes and `Db.AppSettings` when the settings change. Run `Tools\Test-BlueTrackHealth.ps1` afterwards.
- **Installed site settings live in `appsettings.Site.json`** (D-230), next to the published API. Don't add `appsettings.<Environment>.json` files to an install or set `ASPNETCORE_ENVIRONMENT=Development` on IIS: Development turns on the test sign-in endpoint and DevFakeAuth.
- **Deleting a PR's head branch closes the PR without merging.** On 2026-10-14 a branch-delete list included two branches whose PRs were still open; both closed and had to be pushed back and reopened. Only list branches whose PRs show MERGED.
- **Waiting on CI with `gh pr view --json statusCheckRollup`:** a reopened or re-pushed PR has more than one check run; test "every check COMPLETED" (`[.statusCheckRollup[]|select(.status!="COMPLETED")]|length == 0`), not one exact status string.
- **`.gitignore` ignores `*.csv`** so real exports are never committed; synthetic test fixtures go under `App/Api.Tests/Fixtures/` (the only exception). Check `git ls-files` after adding one.
- **Never copy `Live-Testing.local.md` details into tracked files** (lab account, AppID, Safe, Object); use placeholders in examples. Real CyberArk data: report schema-level facts only (names of columns, counts), never values.
- **Backslashes through Bash** mangle heredocs and inline Python (`\r\n` became line breaks, `\04` an octal character, `DOMAIN\name` a line break). Write such text with the Write/Edit tools, then grep for control characters.
- **POSTs to the live API** need `X-Requested-With: XMLHttpRequest` (D-197).
- **E2E label clashes:** `getByLabel` is case-insensitive and matches substrings; after a navigation, wait for something only the new page has before filling (see `Reference_Lessons-Learned.md`, 2026-10-13). A UI test that changes shared data and undoes it must wait for each save.
- **The delete/undelete E2E flake had two causes, both fixed:** the test had a 30-second limit and a timed-out attempt left TestAccount01 deleted for its retries (2026-10-17: now 90 seconds, and it undeletes the account first), and the filtered list pages let a slow earlier reply overwrite a newer one (2026-10-18: `useLatestRequest`). See `Reference_Lessons-Learned.md`. If an E2E run fails on all three attempts, read the first attempt's error.
- **The test database has no CyberArk accounts with an address**; feed-linking tests add a temporary `PRIVCLOUD` account and remove it (xUnit runs one test at a time).
- **Live SSO checks without a browser:** `Tools\Test-BlueTrackSso.ps1` (authentik's flow API; the lab account's Credential Provider values are in `Live-Testing.local.md`). For the two front-channel Single Logout legs use a browser: headless Chromium from `App/E2E/node_modules/playwright`, waiting for authentik's "Not you?" before filling the password. IIS writes its log (`C:\inetpub\logs\LogFiles\W3SVC2`) about a minute late.
- **After a reboot**, check SQL Server Agent too (the health check does).
- **Merge with `gh pr merge N --merge --delete-branch`** (the user's standing rule) unless another open PR is stacked on that branch; scripted wait-and-merge loops too.
- **A query that must not wait on in-flight rows has to seek past them** (D-234 lesson): CI's fresh database chose a clustered scan that blocked on an open transaction while the dev box used the index. Force the index; don't use `READPAST` where skipping a locked committed row loses data.
- **Stacked PRs:** merging one with `--delete-branch` closes the next; merge without `--delete-branch` and delete branches once the whole stack is merged. When an earlier PR changes the same doc lines, merge `main` into the first stacked branch and pass the merge down the stack.
- **CI runner wedge** (queued, runner idle, broker errors in `C:\actions-runner\_diag`): restart the runner service, after asking the user.
- **Rights cache lives in SQL Server** and outlasts a test run: a test that depends on a new grant clears it first (`UserRightsCache.InvalidateAllAsync`).
- **UI text is in message files** (D-227): new SPA text goes in `App/Web/src/locales/en/<area>.json`, never hard-coded; the key-coverage test fails otherwise (`Claude_Docs/Design_Localization.md`).
- **Release notes:** every user-visible change gets a line under Unreleased in `User_Docs/Release_Notes.md`, and one in `Planning_User-Docs-Backlog.md`.
- **API access does not touch `AuthenticationExtensions.cs`** (D-243): bearer handling is `ApiTokenAuthenticationMiddleware`, run after the cookie middleware and before `UseAuthorization` in `Program.cs`. Keep it that way, and regression-test Windows, SAML and OIDC sign-in after any change there. API callers' rights are never cached (`UserRightsResolver.ResolveApiAccessAsync`).
- **Wait for CI before a redeploy, and wait for the run to exist.** Right after a merge the push run on `main` may not be listed yet, so "no run in progress" is not enough: wait about 45 seconds, then until it completes. On 2026-10-17 a redeploy started during the push run of #165 and the known flaky E2E test ("Admin deletes an account ... undeletes it in bulk") timed out; re-running the run alone passed.
