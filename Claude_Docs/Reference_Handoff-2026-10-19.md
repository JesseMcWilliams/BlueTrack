# BlueTrack: Handoff, 2026-10-19

> **Stage: Reference (point-in-time snapshot).** Written at the end of the 2026-10-19 session for whoever picks the project up next (a person or another Claude session). It says where things stand, how the last two days of work were done, and what to watch for. Facts about the code live in the code and the other `Claude_Docs/` files; this note points at them instead of repeating them.

## 1. Where things stand

- **Released: v1.3.0** (signed tag, GitHub release, 2026-10-19). `main` is at `a6f1ca7`, the merge of release PR #203. The working tree was clean and `main` matched `origin/main` when this was written.
- **Deployed locally** (this host, `https://bluetrack.company.com`) from `main` just before the release commit. The release commit changes only `<Version>`, the release notes and `Published_Docs/`, so the running code equals v1.3.0, but About and the Deployment page will say a dev version until the next deploy from `v1.3.0`.
- **Tests at the last full run**: web (Vitest) 816, API (xUnit) 1273, local E2E (Playwright) 95, all passing; CI green on every PR.
- **Decision register**: the last row is **D-269**. New decisions take D-270 onward (`Claude_Docs/Design_Decision-Register.md`, append-only).
- **Database scripts**: the last one is `40_BlueTrack_DecomSafeApplicationKey.sql` (applied to the real `BlueTrack` database by the 2026-10-19 deploy). The next is `41`.

## 2. What the 2026-10-19 session delivered

All of it is in the 1.3.0 release notes (`User_Docs/Release_Notes.md`) and has a decision row.

| Piece | Decisions |
|---|---|
| **Self-Hosted Direct**: BlueTrack reads a Self-Hosted Vault through PVWA's REST API (PAM 14.2+) instead of the EVD database; CP/CCP credentials, pull gate, rejected-change threshold, failure notification | D-248, D-249, D-250 |
| **UI workflow review** (twelve workflows, read from the code): bugs and confirmations, list state in the page address, saved messages and empty states, no-access experience, then every remaining finding | D-251 to D-269 |

The review plan was archived as `Claude_Docs/Archive_Planning_UI-Workflow-Review.md`. It lists, at the end under "Not built", the three things left out on purpose:

1. Inline Extend and Revoke on the two risk exception worklists (the exceptions list's bulk actions and the exception's own page do both).
2. The Credentials LDAP save, the Notifications selects and Account Detail's other-status branch still show only a status code on failure.
3. Text typed into a form is lost at an actual idle sign-out (signing in again returns to the page).

Nothing else from the review is open. There is no other known unfinished work from this session. (A later analysis, setting stages automatically, is in section 7; nothing was built.) `Claude_Docs/Planning_Backlog.md` holds the older, unrelated backlog (Privilege Cloud Direct and the rest).

## 3. Conventions that are easy to get wrong

These are all in `CLAUDE.md` or the docs it links; the ones that bit during this work are repeated here.

- **Branches and PRs**: never work on `main`. Topic branch `YYYY-MM-DD-<topic>`, PR into `main`, `gh pr merge --merge --delete-branch`. Commit, push, open a PR or merge only when the user asks. The user approved the whole "commit, push, PR, wait for CI, merge, redeploy locally" cycle for the UI review; that approval was for that work.
- **Numbered lists**: the user's global preference is that actions, options and questions are numbered lists.
- **Docs per change**: a design decision gets a D-row; a user-visible change gets one line in `Planning_User-Docs-Backlog.md` and one under **Unreleased** in `User_Docs/Release_Notes.md`; a new SQL script goes in `Database/README.md`. Don't bump `<Version>` by hand.
- **SQL**: never edit a script that has already run (even the baseline); `CREATE OR ALTER` for views and procedures; the app's DB account lacks `ALTER` (staging is `DELETE`, not `TRUNCATE`).
- **Localization**: every string is a key in `App/Web/src/locales/en/*.json`; `src/i18n/keyCoverage.test.js` fails on a missing key, an unused key, or a dynamic key (build the key list with literal `t('...')` calls). Leading spaces inside a Vue element are dropped: write `{{ ' ' + t('...') }}`.
- **Permissions in the SPA** are presentation only. `App/Web/src/router/routePermissions.js` maps pages to permissions for the router (a new gated page or admin section is added there; a test fails for an `admin-` route that isn't). The API's `[Authorize(Policy=...)]` is the enforcement.
- **After touching `App/Api/Auth/AuthenticationExtensions.cs`**, regression-test Windows Integrated sign-in (`Claude_Docs/Design_Authentication-Architecture.md`).
- **Live testing**: lab details are in the gitignored `Live-Testing.local.md`; read it only for live tests, never copy it into tracked files, never write secrets anywhere. Use `ThisIsMy_FAKE_Password6!` as a placeholder.

## 4. How to work here (commands)

| Task | Command |
|---|---|
| API tests | `dotnet test App/Api.Tests` (one class: `--filter "FullyQualifiedName~Name"`). Integration/contract tests use `BlueTrackTest_Local`; rebuild it with `Tools/Reset-BlueTrackLocalTestDb.ps1` after adding a SQL script. |
| Web tests | `npx vitest run` in `App/Web` (one file: `npx vitest run <path>`). |
| E2E | `npm test` in `App/E2E` (one spec: `npx playwright test tests/<file>.spec.js`). `workers: 1`. |
| Redeploy locally | `Deploy/Install-BlueTrack.ps1 -ConfigFile ./State/answers.BlueTrack.json` from `Deploy/`; the last lines show `Smoke ... Healthy`. It runs the Migrator, so new SQL scripts apply to the real database. |
| Release | `Tools/New-BlueTrackRelease.ps1 -Version X.Y.Z -Push`, merge the PR, then `... -Version X.Y.Z -Tag -Push` (`Tools/README.md`). Needs git, python-docx, `gh`, and the signing key (already set up on this host). |

## 5. Things that cost time, so you don't repeat them

1. **Never run local Playwright while a CI run is in progress.** Both share the test database and this host; it failed a CI spec once. Check `gh run list --limit 1` is `completed` first (after a merge, CI runs again on `main`). Never `taskkill /IM`; kill only a PID you started.
2. **A contiguous block of E2E failures that all fail fast** means the harness server or the Vite proxy is wedged (once: `ECONNREFUSED` on port 4173), not a code bug. Look at the first error, then re-run.
3. **Large edits through the Bash tool**: a heredoc containing apostrophes, backticks or `$` together with other quoting was misparsed several times ("unexpected EOF"). Write the script with the Write tool, then run it. The same applies to tests with long quoted strings.
4. **Line endings**: many repo files are CRLF. A scripted edit must read with `newline=''`, normalise to `\n`, and write back with the file's own ending, or `str.replace` of a multi-line string silently doesn't match.
5. **Component tests**: pages mutate the fixtures a mock hands them (return copies); `vi.restoreAllMocks()` doesn't clear module-level mocks (set `mockResolvedValue` explicitly); the test environment's `localStorage` is Node's, so stub an in-memory one when a store writes to it; a page that uses a Pinia store needs `setActivePinia(createPinia())`.
6. **Playwright locators** match by substring and ignore case: a new button whose name contains an old button's name (for example "Clear filters" next to "Filter") breaks `getByRole('button', { name: 'Filter' })` with a strict-mode error. Use `exact: true`, or `.first()` for a table row that now has two links.
7. **E2E-breaking UI changes**: a new confirmation or message changes what a spec has to click. Search `App/E2E/tests` for the label before changing a page (`tests/confirmDialog.js` has `confirmDelete(page, label)`; `tests/accountPicker.js` has `pickAccount(page, name)`).
8. **Lessons log**: `Claude_Docs/Reference_Lessons-Learned.md` has the older ones (REST versus EVD comparison, first live use, test fixture mutation, E2E-breaking changes).

## 6. Where to look

- Project rules and folder map: `CLAUDE.md`. Architecture: `Claude_Docs/Design_Architecture.md`. UI conventions (lists, flash messages, empty states, no-access pages, the page-to-permission map): `Claude_Docs/Design_Application-Structure.md`, "Cross-Cutting UI Conventions".
- Self-Hosted Direct: D-248 to D-250; code under `App/Api` (the pull service, `usp_Import_Configured` gate on `dbo.cyberark_direct_pull`) and the sources pages `CyberArkSourcesEdit.vue` and `DataSources.vue`.
- Shared UI pieces added or extended during the review: `components/FlashMessage.vue` and `composables/useFlash.js`, `components/EmptyListMessage.vue`, `utils/problemText.js`, `composables/useAddressState.js` and `utils/listQuery.js` (page state in the address), `composables/useClientTable.js` (search, sort and paging done in the browser for the reports whose APIs send every row), `stores/listState.js`.
- Archived review with the reasoning behind each finding: `Claude_Docs/Archive_Planning_UI-Workflow-Review.md` (read it only if you need that history).

## 7. After the release: setting Blueprint stages automatically (analysis only)

> **Superseded in part (2026-10-20):** the live checks were run and the follow-on work is planned in `Claude_Docs/Planning_Self-Hosted-Direct-And-EVD-Retirement.md`. D-270 and script `41` went to the `stg_shd_*` staging tables (phase 1 of that plan), not to the stage rules below. Read the plan first; this section is kept as the analysis it started from.

A later session on 2026-10-19 looked into whether an account's Blueprint stage can be set automatically, and what changes if Self-Hosted data comes only from Self-Hosted Direct (PVWA REST) and not the EVD. **Nothing was built, decided or committed.** No D-row was written. The facts below were read from the code and the local Idira docs.

### What's automatic today

The five stages are in `dim_blueprint_stage` (`01_BlueTrack_Baseline_CoreSchema.sql`, line 218).

| # | Stage | How it's set |
|---|---|---|
| 1 | Discovered | Automatic: every new account starts here (`usp_Load_FactAccountProgress`) |
| 2 | Assessed / Prioritized | By hand |
| 3 | Onboarded to Vault | Automatic: `usp_Load_AccountProgressAutoAdvance` (D-91, narrowed by D-212; current version in script 13) |
| 4 | Managed / Rotation Enabled | By hand |
| 5 | Verified / Compliant | By hand |

The stage 3 rule moves an account only if all of these are true:
- It's a `PRIVCLOUD` or `SELFHOSTED` account.
- It isn't in a `*_Pending` Safe.
- It's untouched: still Discovered and Not Started.

It never overrides an analyst's edit, and it isn't audit-logged.

### The data gap for stages 4 and 5

`fact_account` has `AutoManaged`, `CPMStatus`, `ManualReason`, `LastCPMModifiedDate`, `LastReconciledDate` and `LastVerifiedDate`.
- **Privilege Cloud exports** fill all of them.
- **Self-Hosted fills them only partly, from either source.** Both sources write the EVD-shaped `stg_sh_*` tables, and the self-hosted part of `usp_Load_FactAccount` (script 36) reads them:
  - `AutoManaged` is 0 when `CPMDisabled` has a reason, and NULL otherwise. It is never 1.
  - `CPMStatus` and `LastReconciledDate` are never set.
  - `LastVerifiedDate` comes from `CAFLastUsedDate`. Whether that column really means "last verified" is unconfirmed.

PVWA REST returns more than the pull keeps. `secretManagement` holds `automaticManagementEnabled`, `manualManagementReason`, `status`, `lastModifiedTime`, `lastReconciledTime` and `lastVerifiedTime` (Self-Hosted Get Accounts doc, 14.2 and 15.2, under `C:\Code\References\Idira_Documents\PAM\PAM-SH\`).
- `PvwaClient.cs` (lines 93-96) reads the flag, the reason, the last-modified time and the last-verified time.
- `SelfHostedStagingMapper.cs` (line 111) keeps the flag only when it is false, written as `CPMDisabled`.
- `status` and `lastReconciledTime` aren't read.

### What dropping the EVD would allow

1. Keep `automaticManagementEnabled = true`, `status` and `lastReconciledTime` from the pull, and fill `AutoManaged`, `CPMStatus` and `LastReconciledDate` for Self-Hosted as Privilege Cloud already does. A new script `41` would add whatever staging columns are needed.
2. Removing the EVD option entirely would let Direct stop imitating the EVD's tables and remove one import path. That would need its own D-row.
3. Losses, none of which affect stages:
   - Internal Vault Safes are hidden from the service user.
   - `RemoteMachines`, `DeviceType`, `CreationMethod`, some `LogonDomain` values, and the linked logon and reconcile accounts aren't returned.
   - ValidateSafeContent, EventsList and EventsAdd have no REST counterpart.
   - Requests and confirmations aren't read.

   See `Design_CyberArk-Direct-Integration.md`, "Not read" and "Lab comparison".

### Must verify before building

4. **Whether REST returns the CPM fields.** The lab's accounts had never been processed by the CPM, so whether `lastVerifiedTime`, `lastReconciledTime` and `status` come back is unknown. Get the CPM to verify or change one lab account, run a pull, and look.
5. **The values `status` can take.** The 14.2 and 15.2 docs say `inProcess, Success, Failure`; the 12.2 doc says `inProcess, succeeded, failed, partiallySucceeded`. Match without regard to case, and check what the lab returns.
6. **What `CAFLastUsedDate` means.** Confirm whether the EVD's `CAFLastUsedDate` is really a verify date.

### Candidate rules (not agreed)

7. **Stage 4**: onboarded, `AutoManaged = 1`, and CPM not failing.
8. **Stage 5**: stage 4 is met, and the last verify or change falls within N days. Optionally also: no open risk exception.
9. **Stage 2**: likely stays manual, because it records a judgement. Possible signals are an account type and system of record being set, or a risk level being assigned.

### Questions the user still has to answer (ask, don't assume, as for D-91)

10. Which stages to automate: 4 and 5 only, or 2 as well?
11. What counts as a CPM failure, and what is N days?
12. Should the rules move only untouched accounts, as stage 3 does, or also move hand-set accounts forward?
13. When an account stops qualifying, should it move back a stage or only be flagged? D-51 says moving back needs a reason, so an automatic move back needs a system reason, as in D-212.
14. Should each automatic move be audit-logged as a system edit, as D-212's feed-link move is?
15. Should the EVD stay as a Self-Hosted option, or be removed?

## 8. Suggested next steps

None are urgent. The user decides the order.

1. Redeploy locally from the `v1.3.0` tag or `main` so About shows `1.3.0`.
2. The three "Not built" items in section 2, if the user wants them.
3. Privilege Cloud Direct, the next option on the CyberArk sources page (`Planning_Backlog.md`).
4. Automatic stages (section 7). Do these in order:
   - Run the live checks in items 4 to 6. Read `Live-Testing.local.md` first.
   - Get the user's answers to items 10 to 15.
   - Write D-270.
   - Change the pull and the Load to keep the CPM fields (script `41`).
   - Add the stage 4 and 5 rules.
   - Update `Design_CyberArk-Direct-Integration.md`, `Design_Data-Editing-Behavior.md`, the user-docs backlog and the release notes.
