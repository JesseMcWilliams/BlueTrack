# User Guide

**Blueprint Progress Tracking Web Interface**

## Scope

This is a task-oriented, "how do I use this" guide for BlueTrack administrators working in the web UI — distinct from the `Design_*.md` documents elsewhere in this folder, which record *why* something was built a particular way, not how to operate it day to day.

**Scope note (updated 2026-09-21)**: this guide now covers every page in the application inventory (`Design_Application_Structure.md`) — the core analyst workflow (Dashboard, Account Progress, Risk Exceptions, Reports), Targets/Access Groups, and the full 14-section Admin hub. Each section was written from the actual Vue component source, not guessed. Kept up to date feature by feature as the app changes, rather than a one-time snapshot — if a page's UI changes, its section here should be revisited.

## Getting Around

The top navigation is permission-gated — you only see the entries you actually have rights for. A signed-in user with every permission sees: **Dashboard | Accounts | Exceptions | Reports | Targets | Access Groups | Admin** (Targets/Access Groups/Admin only appear if you hold their gating permission), plus a user menu for your profile, reloading your rights, and logging out. Every page shows a breadcrumb trail at the top so you always know where you are.

## Dashboard

**Where**: the home page you land on after signing in.

Four at-a-glance summary cards, each linking into the full report/worklist behind it:
- **Accounts by Stage** — a table plus a small pie chart of how many accounts sit at each Blueprint stage, with a count of how many are Complete. Links to the full Stage/Status Summary report.
- **Key Progress Indicators** (added 2026-09-22) — the four KPI Summary ratios (In Scope vs. All Accounts, Onboarded vs. In Scope, Managed vs. Onboarded, Compliant vs. Managed) as percentages, plus a pie chart breaking every account into exactly one of five buckets: Out of Scope, In Scope-Not Onboarded, Onboarded-Not Managed, Managed-Not Compliant, or Compliant. Links to the full KPI Summary report.
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
- **KPI Summary** (added 2026-09-22, no permission required) — the four progress ratios (In Scope vs. All Accounts, Onboarded vs. In Scope, Managed vs. Onboarded, Compliant vs. Managed) as counts and percentages. "In scope" means not currently excluded via an active Risk Accepted / Excluded exception. The same four ratios, plus a summary pie chart, also appear on the Dashboard's Key Progress Indicators card.

## My Profile

**Where**: the user menu (`/profile`).

Shows your display name, mapped role(s), and full permission list. Click **Reload My Rights** to re-check your current group membership immediately, without waiting for anything to expire — useful right after you know you've been added to a new group. Also where you pick a display theme: Light, Dark, or High Visibility.

## Targets and Access Groups

**Where**: **Targets** and **Access Groups** in the top nav (`/targets`, `/access-groups`) — promoted out of the Admin hub to their own top-level entries, gated by `ManageTargets`/`ManageAccessGroups` respectively.

These two pages manage the Risk Scoring inventory behind every account's computed risk score (see `Design_Risk_Scoring.md` for the full model). Both follow the same shape:

### Targets

A **Target** is any final destination an account's access leads to — a server, database, application, or other endpoint. The list shows Name, Type, Application, Risk Score (always analyst-set, 0-1000, regardless of how the row was created), and every recognized identifier (e.g. `ADGuid=...`, `FQDN=...`) — a Target can carry several. Filter by Type or Application; click a column header to sort (shift-click to add a secondary key). Click a Target's name to edit it, including adding/removing identifier rows; **+ New Target** opens the same form blank.

Below the table, **Link a Single Account to a Target** creates a direct account-to-target link without going through a group — type an account name and pick a Target.

**Bulk Actions** (its own page, reached via the button above the table) offers two CSV imports: **Target Inventory** and the **Account → Target Map**. Each accepts a file upload and reports success/errors per row rather than failing the whole batch on one bad row.

### Access Groups

An **Access Group** is a privileged-access group in the *managed environment* (e.g. an AD "Server Admins" group) — not this app's own CyberArk Safe-permission groups, and not its own login/role mapping (`Design_Risk_Scoring.md` is explicit about keeping these three concepts separate). The list shows Name, Identifier, Scope (Domain/Local — a Local group also shows which Target it was found on), SOR Type, Base Risk Score (analyst-set) and Computed Risk Score (derived from Base plus every reachable Target, marked "(stale)" if it needs recalculating), SOR Address, and Discovery Source. Filter by Scope or SOR Type; sort the same way as Targets. Click a group's name to edit it; **+ New Access Group** for a blank form.

**Bulk Actions** here covers three CSV imports: the group inventory itself, which Targets each group reaches, and which Accounts belong to each group.

Deleting either a Target or an Access Group still referenced by a mapping fails with an explanation rather than silently breaking the mapping — every Delete asks for confirmation first, showing the row's key identifying details.

## Admin Hub

**Where**: **Admin** in the top nav (`/admin`) — a single hub page with a sidebar listing only the sections you hold permission for. If you hold none, the sidebar says so plainly rather than showing an empty page.

### Identity Providers

Manages how people sign in. The list shows Type, Display Name, Enabled, and Order; **+ New Provider** and each row's **Edit** link open a form for one provider. Provider Type is WindowsIntegrated, OIDC, SAML, or DevFakeAuth. OIDC gets structured fields (Authority, Client ID, Callback Path, Groups Claim Type); SAML gets its own set (SP/IdP Entity IDs, SSO/SLO destinations, certificate thumbprints — these refer to certificates already installed in the Windows Certificate Store, not a file you upload). A write-only Secret field shows `(already set — leave blank to keep)` once one exists; the stored value is never shown back to you. **Enabling/disabling OIDC or changing its Authority/Client ID/secret needs an app restart to take effect** — SAML settings are read fresh on every request, no restart needed.

### Group / Role Mapping

Maps an AD security group to one of this app's own roles. The list shows Provider, Group (the stored identifier), and Role, with Delete per row; **+ Add Mapping** opens its own form to pick a group and role.

The **Lookup / Test Tool** section lets you resolve a group name and see what it currently grants, without saving anything — type a name, click **Resolve**, and see the resolved account, its SID, and its current role(s)/permission(s).

#### Generating a `db_backupstatus_reader` Grant Script

The Deployment page's SQL Server backup-status check needs a SQL Server account to be a member of a `db_backupstatus_reader` role in `msdb` before it can report anything — a permission this application can never grant itself (see `Design_Operations_Guide.md` for why). Once you've resolved a group in the Lookup / Test Tool above, a **Generate db_backupstatus_reader Script** button appears below the result.

**Steps**: resolve the target group (above), click **Generate db_backupstatus_reader Script**, and your browser downloads a `.sql` file already targeted at that group. Send it to whoever administers the SQL Server instance — **this application never runs the script itself**; it must be run manually via `sqlcmd` against `msdb`. See `Design_Operations_Guide.md` for the exact command.

### Roles & Permissions

Manages this app's own roles and which permissions each one bundles. The list shows Role, Description, Notification Email, and a comma-joined list of that role's permissions; Delete is blocked (with an explanation) if the role is still mapped to a group. **+ New Role** and each row's **Edit** open a form: Role Name, Description, Notification Email, and a checklist of every permission in the (fixed, not editable here) permission catalog, each with its own description.

### Application ↔ Safe Mapping

Two sections on one page. **Applications**: Code/Name/Owner, with an **Edit** link per row (no Delete here) and **+ New Application** opening a form (Code, Name, Description, Owner Name/Email, Technical Contact Name/Email, Notes). **Safes**: each Safe gets a per-row dropdown assigning it to an Application — changing the dropdown saves immediately, there's no separate Save button for this section.

### Secrets Store Configuration

Exactly one backend is active at a time — this is the vault backend used to resolve privileged-account secrets (distinct from the plain named-credential store on the Credentials & LDAP page). The table lists every backend (`WindowsDpapi`, `CyberArkCP`, `CyberArkCCP`, `CyberArkConjur`, `AzureKeyVault`, `AwsSecretsManager`) with backend-specific structured settings fields (e.g. CyberArk CP just needs an App ID; Azure Key Vault needs a Vault URI and an Auth Method, with Tenant ID/Client ID appearing only when that method is Service Principal). A write-only Credential field per row works the same "already set, leave blank to keep" way as everywhere else. Each row's action button reads **Save** if that backend is already active, or **Make Active** to switch to it.

**Test Connection**: pick a Safe/Folder/Object and click **Test** — attempts a real retrieval against the currently active backend and reports success (with non-secret metadata: username/address) or failure, but **never shows the retrieved secret itself**. If `WindowsDpapi` is active, a hint reminds you to enter a Credentials page credential's *name* as Object (Safe/Folder are ignored for that backend).

### Field Metadata Management

The governed list of fields the Account Progress edit form actually renders. List columns: Field Name, Display Label, Type, Required, Order. **+ New Field** / **Edit** open a form: Field Name, Display Label, Field Type (a plain text value, not a dropdown), Reference Table, Required, Display Order.

### Audit Log Viewer

Read-only — no add/edit/delete. Filter by Event Type, Entity, and a From/To date range (submitted via an explicit **Filter** button, not automatically as you type). Columns: Occurred At, Event Type, By, Entity, and Detail. Click the **Occurred At** value on any row to expand an inline panel showing the Reason (if one was recorded) and a Field / Old Value / New Value table for that specific change.

### Global Application Configuration

Every admin-wide setting on one form, saved together with a single **Save** button: Idle Timeout (minutes), Breadcrumb Position (Top Left/Top Right), Exception ID Pattern, Account Progress Lock Timeout (minutes), Audit Retention in days (blank = keep forever), a "Log read/view events" checkbox, Backup Folder (where the Deployment page's Backup App button writes to), and the Risk Score Algorithm (Dominant Plus Tail / Combined Exposure — see `Design_Risk_Scoring.md`).

#### Enforcing Segregation of Duties on Risk Exception Approval

A checkbox, **"Enforce segregation of duties on Risk Exception approval"**, controls whether the same person who approved a Risk Exception can also be the one who links that exception to an account.

- **Off by default.** Some organizations don't have separate staff available to both approve an exception and later link it to an account — leaving this off (the default) never blocks anyone from doing both.
- **When turned on**: if you approved a given Risk Exception, you personally can no longer be the one who sets an account's status to "Risk Accepted / Excluded" using that exception on the Account Progress edit screen. Attempting to do so shows a validation error explaining why, and nothing is saved — pick a different exception, or have a different person link it.
- This only checks the *specific pairing* of approver and linker for one exception — it does not restrict who can approve exceptions or who can edit Account Progress records in general.

**Reviewing past cases (regardless of whether enforcement is on)**: Reports > Risk Exception SoD (requires `ViewRiskExceptionSodReport`) lists every historical case where the same person both approved a Risk Exception and later linked it to an account — whether or not the checkbox above was on at the time. Each row shows the Exception ID, the account, the person, and when the link happened.

### Deployment

Read-only environment/version info, health checks, and SQL Server backup status.
- **Environment**: Environment name, Version, and build timestamp (UTC).
- **Health Checks**: a table of Component / Status / Description — checks SQL Server connectivity, the active Secrets Store backend, and configured identity providers.
- **SQL Server Backup Status**: a table of Backup Type / Last Backup Finish Date, read from SQL Server's own native backup history — or an alert explaining why it's unavailable (typically a missing `db_backupstatus_reader` grant; see `Design_Operations_Guide.md`). A **Backup App** button (gated separately by `TriggerBackup`, distinct from just viewing this page) triggers a real on-demand database backup and downloads a zip of the app's own config files.

### Notifications

SMTP configuration, notification-type role targeting, and recipients, all on one page.
- **SMTP Configuration**: Host, Port, "Enable STARTTLS," Auth Method (None or Basic — Basic reveals a Credential dropdown sourced from the Credentials & LDAP page), From Address/Display Name, and two TLS-relaxation checkboxes ("Ignore CRL issues," "Ignore all SSL errors — use with caution").
- **Notification Types**: a table where each notification type gets an optional Target Role via a per-row dropdown that saves immediately — that role's recipients are resolved from the role's own email plus (if LDAP is configured) every member of its mapped AD groups, *additive* to the flat recipient list below.
- **Test Connection**: a **Send Test Email** button sends a real email, using the saved SMTP config, to every active recipient.
- **Recipients**: Email / Display Name / Active, with Deactivate-or-Activate and Delete per row; **+ Add Recipient** opens its own small form (Email, Display Name).

### Credentials & LDAP

Two independent sections. **Credentials** is a generic named-credential store (distinct from the single active Secrets Store backend above) — list shows Name/Backend/Username/Scope; **+ New Credential** opens an inline form whose fields depend on the chosen Backend (Windows DPAPI takes Username/Password plus a Machine-vs-User DPAPI Scope choice — a new "User" scope credential starts as Machine scope and upgrades automatically the first time the app itself decrypts it; every other backend takes Safe/Folder/Object). Each row has Edit, Delete, and **Test** (confirms the credential resolves, inline).

**LDAP Configuration**: one row per AD domain/forest this app needs to query (recipient resolution, AD Account Discovery) — each independently enabled and bound with its own credential. **+ New Domain** opens a form: Domain Name, Enabled, Domain Controller (blank = default), Search Base, Use SSL, and either "Use trusted connection" (the app pool/local computer account) or a Bind Account Credential picked from the Credentials list above.

### Target Match Review

A queue of weak import matches (e.g. an identifier that's only an IP address, which can be reassigned by DHCP/NAT) that didn't get auto-merged during an import and need a human decision. Each row shows the identifier, the candidate Target it weakly matched, and the source file, with an editable "Merge Into" target key (pre-filled with the candidate, but you can redirect it to a different target before deciding) and three buttons: **Merge**, **New Target**, **Ignore** — no confirmation dialog, since none of the three destroys data.

### Import Mapping Profiles

One named profile per distinct source file shape for the risk-scoring import feeds — not needed at all for this app's own generated CSV templates, only for uploading an existing external export as-is. List shows Feed Type, Name, Active, and a flattened summary of every mapped field. **+ New Profile** / **Edit** open a form: Feed Type, Profile Name, Active, Description, and a repeatable Fields list (Source Column, Internal Field, Required, Default value, each removable, plus **+ Add Field**).

### Risk Score Bands

Named ranges over the computed risk score (e.g. Low/Medium/High/Critical) — the resulting band name is what shows up on the Account Progress list and the Risk Score report. List shows Name/Min Score/Max Score/Order. **+ New Band** / **Edit** open a form (Name, Min Score, Max Score, Risk Order); **a new or edited range can't overlap any other existing band** — saving an overlapping range shows the specific validation error from the server rather than a generic failure message.

## See also

- `../Guides/` (added 2026-09-23) — short, linear, role-based walkthroughs (Admin_Installation/Configuration/DataManagement, User_Viewer/Analyst/Approver/Auditor) that supplement this page's field-by-field reference rather than duplicate it.
- `Design_Operations_Guide.md` — the DBA/operator-facing side of everything with no UI of its own (rollback backup/restore scripts) or that a DBA has to act on once this app hands it over (the generated `db_backupstatus_reader` script).
- `Design_Application_Structure.md` — the authoritative full page inventory and navigation conventions this guide is organized around.
- `Design_Risk_Exception_Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
- `Design_Risk_Scoring.md` / `Design_Risk_Scoring_Import.md` — the data model and algorithm behind Targets, Access Groups, the Risk Score tab/report, and Risk Bands.
- `Design_Interface_Extensibility.md` — the field-metadata system behind the Account Progress edit form and Field Metadata Management.
- `Design_Credentials_Management.md` — the Credentials & LDAP page's own design rationale.
- `Design_Notifications.md` — the Notifications page's own design rationale.
- `Design_Admin_Deployment_Management.md` — the Identity Providers/Secrets Store structured-field UI and the Deployment page, including the backup-status check that `db_backupstatus_reader` exists to support.
