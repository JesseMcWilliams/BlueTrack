# User Guide

**Blueprint Progress Tracking Web Interface**

## Scope

This is a task-oriented, "how do I use this" guide for BlueTrack administrators working in the web UI — distinct from the `Design_*.md` documents elsewhere in this folder, which record *why* something was built a particular way, not how to operate it day to day.

**Honest scope note (updated 2026-09-21)**: this guide now covers the core day-to-day analyst workflow — Dashboard, Account Progress, Risk Exceptions, and Reports — plus the two newest admin-facing capabilities added 2026-09-16. It does **not** yet cover **Targets / Access Groups** or any of the **Admin hub** (Identity Providers, Group/Role Mapping beyond the script generator below, Roles & Permissions, Application Mapping, Secrets Store, Field Metadata, Audit Log, Global Application Configuration beyond the SoD toggle below, Deployment Info, Notifications, Credentials & LDAP, Target Match Review, Import Mapping Profiles, Risk Score Bands) — tracked as the next phase in `Audit_Findings_Tracker.md`. Extend it feature by feature rather than treating this note as a promise the rest will appear on its own.

## Getting Around

The top navigation is permission-gated — you only see the entries you actually have rights for. A signed-in user with every permission sees: **Dashboard | Accounts | Exceptions | Reports | Targets | Access Groups | Admin** (Targets/Access Groups/Admin only appear if you hold their gating permission), plus a user menu for your profile, reloading your rights, and logging out. Every page shows a breadcrumb trail at the top so you always know where you are.

## Dashboard

**Where**: the home page you land on after signing in.

Three at-a-glance summary cards, each linking into the full report/worklist behind it:
- **Accounts by Stage** — a table plus a small pie chart of how many accounts sit at each Blueprint stage, with a count of how many are Complete. Links to the full Stage/Status Summary report.
- **Overdue / At-Risk Accounts** — a count of accounts past their target remediation date. Links to that worklist.
- **Risk Exceptions Needing Attention** — a count of exceptions past their review date (everyone sees this). If you hold `ApproveExceptions`, you also see a count of Active exceptions awaiting approval.

## Account Progress

**Where**: **Accounts** in the top nav (`/accounts`).

### The list

Filter by Stage, Status, Risk Level, or Owner (a "contains" text match) — filters stack together. Click any column header to sort by it; shift-click another header to add it as a secondary sort key (a small numbered arrow shows the resulting priority). Click an account's **Username** to open it.

### Editing an account

Opening an account tries to acquire an edit lock automatically if you hold `EditAccountProgress`. If someone else already has it locked, you see who and since when, with a **Force Release Lock** button if you need to take over. Without a lock (or without `EditAccountProgress`), you get a read-only view instead.

The edit form is split into up to three tabs:
- **Details** — the governed set of editable fields (stage, status, risk level, owner, business unit, dates, notes, etc.). A **Reason** field at the bottom of the form is only required if you're regressing the account to an earlier Blueprint stage than it's currently at.
- **Risk Score** — read-only Calculated Risk / Risk Band / Effective Risk Score, a **Recalculate** button that refreshes just this one account's score right now, and an editable **Override Score** (a Reason is required when setting one, not when clearing it back to blank).
- **Risk Exception** — only appears when Status is set to "Risk Accepted / Excluded." Pick an existing Active exception scoped to this account from the dropdown, or click **+ Create New Exception** to add one inline without leaving the page.

Saving (or Cancel) returns you to the Accounts list.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

### The list

Filter by Status (Active/Expired/Revoked) and Scope (Account/Application). If you hold `ApproveExceptions`, a **+ New Exception** button appears above the filters. Click an Exception ID to open it.

### Creating one

Choose Account or Application scope. Account scope takes a raw Account Key (there's no search/picker yet — a known limitation, not an oversight); Application scope gets a real dropdown. Fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.

### Managing an existing exception

While an exception is Active, its edit page offers:
- **Re-approve** — extend the Review Date without changing anything else.
- **Revoke** — end the exception immediately.

### Worklists

- **Approval Worklist** (`Exceptions > Approval Worklist`, requires `ApproveExceptions`) — every currently-Active exception, click a row to open it.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date, needing re-approval or revocation.

## Reports

**Where**: **Reports** in the top nav (`/reports`) — a hub with sub-navigation. Each link only appears if you hold that report's gating permission; a few have none and are visible to anyone signed in.

- **Overdue / At-Risk** — accounts past their target remediation date that aren't yet Complete (no permission required).
- **Stage/Status Summary** — an account count per Stage × Status cell, with row totals (no permission required).
- **Reconciliation Review** (requires `ConfirmReconciliation`) — unconfirmed cross-source account matches; currently read-only, no confirm/reject action wired up yet.
- **Unresolved Entitlement Members** — Safe entitlements granted to a member this app can't resolve to a known user or group (no permission required).
- **Risk Score** (requires `ViewRiskReport`) — every account's Computed/Override/Effective risk score and Risk Band, sortable, with a **Recalculate Now** button and a per-account click-to-expand drill-down showing the real Targets/Access Groups behind that score.
- **Discovered Accounts** (requires `ViewDiscoveredAccounts`) — real AD accounts not yet onboarded into CyberArk, found nightly and risk-scored the same way as tracked accounts. If you hold `ManageDiscoveredAccounts`, **Accept**/**Dismiss** buttons let you move a candidate into real Blueprint tracking or resolve it as a false positive.
- **Risk Exception SoD** (requires `ViewRiskExceptionSodReport`) — see "Reviewing past cases" below.

## My Profile

**Where**: the user menu (`/profile`).

Shows your display name, mapped role(s), and full permission list. Click **Reload My Rights** to re-check your current group membership immediately, without waiting for anything to expire — useful right after you know you've been added to a new group. Also where you pick a display theme: Light, Dark, or High Visibility.

## Enforcing Segregation of Duties on Risk Exception Approval

**Where**: Admin > Configuration (the Global Application Configuration page).

A new checkbox, **"Enforce segregation of duties on Risk Exception approval"**, controls whether the same person who approved a Risk Exception can also be the one who links that exception to an account.

- **Off by default.** Some organizations don't have separate staff available to both approve an exception and later link it to an account — leaving this off (the default) never blocks anyone from doing both.
- **When turned on**: if you approved a given Risk Exception, you personally can no longer be the one who sets an account's status to "Risk Accepted / Excluded" using that exception on the Account Progress edit screen. Attempting to do so shows a validation error explaining why, and nothing is saved — pick a different exception, or have a different person link it.
- This only checks the *specific pairing* of approver and linker for one exception — it does not restrict who can approve exceptions or who can edit Account Progress records in general.
- Saved from the same Global Application Configuration form as every other admin-wide setting (idle timeout, breadcrumb position, etc.) — click **Save** after checking the box.

### Reviewing past cases (regardless of whether enforcement is on)

**Where**: Reports > Risk Exception SoD (requires the `ViewRiskExceptionSodReport` permission).

This report lists every historical case where the same person both approved a Risk Exception and later linked it to an account — whether or not the enforcement checkbox above was turned on at the time. Use it to spot-check past activity even in an organization that keeps enforcement off, or to confirm enforcement is doing its job once turned on. Each row shows the Exception ID, the account, the person, and when the link happened.

## Generating a `db_backupstatus_reader` Grant Script

**Where**: Admin > Group / Role Mapping, in the existing "Lookup / Test Tool" section.

The Deployment page's SQL Server backup-status check needs a SQL Server account to be a member of a `db_backupstatus_reader` role in `msdb` before it can report anything — a permission this application can never grant itself (see `Design_Operations_Guide.md` for why). This page now has a button to generate the exact script a DBA needs to run to grant that role to a specific AD group, instead of hand-writing it.

**Steps**:
1. Type the group's name into the **Group name** field under "Lookup / Test Tool" and click **Resolve** (same lookup this page already offered — it resolves the friendly name to the account BlueTrack actually matches on).
2. Once resolved, a **Generate db_backupstatus_reader Script** button appears below the resolved result.
3. Click it — your browser downloads a `.sql` file (named after the resolved account) already targeted at that group.
4. Send the downloaded file to whoever administers the SQL Server instance. **This application never runs the script itself** — it must be run manually via `sqlcmd` against `msdb`, the same way the file's own header explains. See `Design_Operations_Guide.md` for the exact command.

If the group name can't be resolved, the button never appears — resolve it successfully first, same as the existing lookup tool already required.

## See also

- `Design_Operations_Guide.md` — the DBA/operator-facing side of the two admin features above (running the generated grant script, and the separate rollback backup/restore scripts, which have no UI of their own at all).
- `Design_Application_Structure.md` — the authoritative full page inventory (including everything not yet covered in this guide) and navigation conventions.
- `Design_Risk_Exception_Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
- `Design_Risk_Scoring.md` / `Design_Risk_Scoring_Import.md` — the data model and algorithm behind the Risk Score tab/report and Risk Band shown throughout Account Progress and Reports.
- `Design_Admin_Deployment_Management.md` — the Deployment page and its backup-status check that `db_backupstatus_reader` exists to support.
