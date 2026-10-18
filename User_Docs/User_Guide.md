# User Guide

**Blueprint Progress Tracking Web Interface**

## Scope

This is a task-oriented, "how do I use this" guide for BlueTrack administrators working in the web UI — distinct from the `Design_*.md` documents in `Claude_Docs/`, which record *why* something was built a particular way, not how to operate it day to day.

**Scope note**: this guide now covers every page in the application inventory (`Claude_Docs/Design_Application-Structure.md`) — the core analyst workflow (Dashboard, Account Progress, Risk Exceptions, Reports), Targets/Access Groups, and every section of the Admin hub. Each section was written from the actual Vue component source, not guessed. Kept up to date feature by feature as the app changes, rather than a one-time snapshot — if a page's UI changes, its section here should be revisited.

## Getting Around

The top navigation reads **Dashboard | Access Groups | Accounts | Exceptions | Targets | Reports | Admin**, with a menu showing your name on the right: **Profile & Preferences**, **About** (shows BlueTrack's version) and **Sign out**. **Access Groups** appears only with `ManageAccessGroups`, and **Targets** only with `ManageTargets`. Inside **Admin**, the sidebar lists only the sections you hold permission for. Every page shows a breadcrumb trail at the top so you always know where you are.

Seeing tracked data (the Dashboard, Accounts, Exceptions and most reports) needs a role with `ViewDashboard`. Without it, those pages show an error (such as "Request failed: 403") instead of data. An administrator's changes to roles or group mappings apply straight away. A change to your own group membership (for example, being added to an AD group) can take up to 15 minutes; **Reload My Rights** on **Profile & Preferences** picks it up straight away.

When BlueTrack refuses a save or delete, the page says why: for example, a name, identifier or score range that's already used, something still in use by other records, or a record someone else changed first.

## Signing in

**Where**: the **Sign in** page (`/login`), which opens on its own when you aren't signed in.

How it behaves depends on the **Default sign-in method** your administrator sets (Admin > Identity Providers):
- **Windows** is the default: you're signed in silently and land on the page you asked for (the Dashboard by default). If that doesn't finish, the page lists the methods; choose **Continue** next to Windows. If it still fails, the page says "Windows sign-in didn't complete"; check any credentials your browser asked for and choose **Continue** again.
- **OIDC or SAML** is the default: you go straight to your organization's identity provider, sign in there, and come back to BlueTrack.
- **No default**: the page says "Choose a sign-in method:" and lists every enabled method. The default, if there is one, is marked *(default)*.

Each method also has its own direct address, which uses that method whatever the default is: `/login/windows`, `/login/oidc` and `/login/saml` (after the site's address, e.g. `https://<server>/login/saml`; the Identity Providers page shows the full addresses). Your administrator may give you one of these, or use it as the launch link of an app tile in your identity provider. A direct OIDC or SAML address goes to the identity provider without a Windows prompt first. If that method isn't enabled, the page says "That sign-in method isn't enabled. Choose one below."

If you sign in with OIDC or SAML and your identity provider sends no full name, BlueTrack shows your username instead.

### Signing out

Open the menu with your name at the right of the top nav and choose **Sign out**. BlueTrack ends your session and shows the sign-in page with "You're signed out."; it doesn't start the default sign-in method again on its own.

- **Windows sign-in:** if Windows Integrated is the default method, going to any other page signs you straight back in with your Windows account. To use another account, choose another method on the sign-in page.
- **SSO (OIDC or SAML):** you're signed out of BlueTrack, not out of the identity provider. Choosing the same method again may sign you in without asking for your password, if your session with the identity provider is still open.

**Inactivity.** If you don't use BlueTrack for the Idle Timeout your administrator set (30 minutes by default), it signs you out and the sign-in page says so. Using BlueTrack in any tab counts as activity for all of them.

## Dashboard

**Where**: the home page you land on after signing in.

Four at-a-glance summary cards, each linking into the full report/worklist behind it:
- **Accounts by Stage** — a table plus a small pie chart of how many accounts sit at each Blueprint stage, with a count of how many are Complete. Links to the full Stage/Status Summary report.
- **Key Progress Indicators** — the four KPI Summary ratios (In Scope vs. All Accounts, Onboarded vs. In Scope, Managed vs. Onboarded, Compliant vs. Managed) as percentages, plus a pie chart breaking every account into exactly one of five buckets: Out of Scope, In Scope-Not Onboarded, Onboarded-Not Managed, Managed-Not Compliant, or Compliant. Links to the full KPI Summary report.
- **Overdue / At-Risk Accounts** — a count of accounts past their target remediation date. Links to that worklist.
- **Risk Exceptions Needing Attention** — a count of exceptions past their review date (everyone sees this). If you hold `ApproveExceptions`, you also see a count of Active exceptions awaiting approval.

## Account Progress

**Where**: **Accounts** in the top nav (`/accounts`).

### The list

**Search** finds accounts whose username, address or account name contains what you type. An account with no username is listed by its CyberArk account name, marked *(no username)*; click it to open the account. Filter by Stage, Status, Risk Level, or Owner (a "contains" text match) — filters stack together. Click any column header to sort by it; shift-click another header to add it as a secondary sort key (a small numbered arrow shows the resulting priority). Click an account's **Username** to open it.

A **⚠** next to a username means the account is flagged for deletion: it's in a safe matching the safe decommission pattern, or its name matches the account pattern. Point at it (or use a screen reader) for the safe's name and any other safe the account is also in; the account's page shows the same as a notice.

**Deleted accounts** are hidden. The **Deleted accounts** filter shows them alongside the others (**Show**) or on their own (**Only deleted**). A deleted account is marked **Deleted**; point at the mark to see who deleted it and why, whether CyberArk deleted it, or **Not imported (ignored safe)** for an account in a safe matching the ignored safe pattern.

**Accounts from other systems.** An account an administrator imports from another system (an account inventory data feed) is marked **from** and the system's name. When CyberArk has the same account (the same user name and address), BlueTrack links the two: the other system's account moves from **Discovered** to **Onboarded to Vault** and is hidden, so the list and the reports count the account once, as its CyberArk account. Set the **Linked feed accounts** filter to **Show** to see them; each is marked **Linked**, and the mark opens its CyberArk account.

### Selecting accounts for bulk actions

Tick **Select accounts for bulk actions** above the list (you need `EditAccountProgress` or `DeleteAccounts`) to add a checkbox to every row. The bar shows how many accounts are selected (and how many of those are on this page), with:
- **Select all on page**, **Invert selection on page** and **Clear**.
- **Select all matching**: every account matching the current search and filters, on every page. It refuses, with a message, if more match than the bulk limit (500 by default; Global Application Configuration).
- **Bulk edit…** (`EditAccountProgress`): opens the Bulk Edit page. Tick **Change** next to each field to set (Stage, Status, Risk Level, Account Type, Source of Record, Application, Owner, Business Unit, the two dates, and Notes, added to or replacing the existing notes; choosing no Application clears it); other fields stay as they are on each account. **Reason** is required if the new stage is earlier than an account's current one. Risk Exception can't be bulk edited; link it on each account.
- **Delete…** / **Undelete…** (`DeleteAccounts`): asks for a reason, then deletes or undeletes every selected account.

The selection stays as you page, sort and filter. After a bulk action, a summary lists any account that was skipped and why (for example, someone else is editing it); skipped accounts stay selected.

### Deleting and undeleting accounts

With `DeleteAccounts`, an account's page has **Delete account…** (or **Undelete account…** if it was deleted in BlueTrack), and the list has the bulk **Delete…** / **Undelete…** buttons above.
- **A reason is always required**, for undelete as well as delete. Every delete and undelete is kept, with who, when and why: open **Delete / undelete history** on the account's page. Each is also in the Audit Log.
- A deleted account keeps its progress, risk exceptions and history; it's just hidden from the list, the Dashboard and the reports. A delete made in BlueTrack stays in place through the nightly import.
- **Undelete** reverses a BlueTrack delete only. An account CyberArk deleted is marked "Deleted in CyberArk" and comes back only if it reappears in CyberArk.
- An account someone else is editing is skipped, not deleted.

### Editing an account

Opening an account tries to acquire an edit lock automatically if you hold `EditAccountProgress`. If someone else already has it locked, you see "Currently being edited by" with who and since when, and a **Force Release Lock** button if you need to take over. Without a lock (or without `EditAccountProgress`), you get a read-only view instead.
- An account you already have open elsewhere (another tab, or a visit you left without saving) opens for editing as usual; your own lock never blocks you.
- While the page is open it keeps the lock alive. A lock with no activity for the **Account Progress Lock Timeout** (Global Application Configuration; 30 minutes on a new install) is treated as abandoned: the next person to open the account gets the lock, with no force-release needed.

The edit form is split into up to three tabs:
- **Details** — the governed set of editable fields (stage, status, risk level, owner, business unit, dates, notes, etc.). A **Reason** field at the bottom of the form is only required if you're regressing the account to an earlier Blueprint stage than it's currently at.
- **Risk Score** — read-only Calculated Risk / Risk Band / Effective Risk Score, a **Recalculate** button that refreshes just this one account's score right now, and an editable **Override Score** (a Reason is required when setting one, not when clearing it back to blank).
- **Risk Exception** — only appears when Status is set to "Risk Accepted / Excluded." Pick an existing Active exception scoped to this account from the dropdown, or click **+ Create New Exception** to add one inline without leaving the page.

Saving (or Cancel) returns you to the Accounts list.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

### The list

Filter by Status (Active/Expired/Revoked) and Scope (Account/Application). If you hold `ApproveExceptions`, a **+ New Exception** button appears above the filters. Click an Exception ID to open it.

Columns: Exception ID, Scope, Approved By, Approval Date, Review Date, Status, Justification and **Source**. Source names the tool and ID an imported exception came from, as a link when the import gave one; it's blank for exceptions created in BlueTrack.

With `ApproveExceptions`, tick **Select exceptions for bulk actions** for checkboxes and the selection bar (see Account Progress, "Selecting accounts for bulk actions"): **Extend review…** sets a new review date on every selected Active exception (a re-approval), and **Revoke…** revokes them after you give a reason. Exceptions that aren't Active are skipped and listed.

### Importing exceptions approved in another tool

With `ApproveExceptions`, **Bulk Actions** (next to **+ New Exception**) opens the import page. **Download template**, fill one row per exception, choose the file and click **Import**.
- Each row gets a new BlueTrack Exception ID; the result lists the new IDs next to the source IDs.
- **SourceTool** and **SourceExceptionId** are required; the same tool and ID imported twice is an error. **SourceUrl** (optional) is a link to it in that tool.
- The exception covers either an account (**AccountUserName** + **AccountAddress**, both required, an exact match, so an account without an address can't be matched) or an application (**ApplicationCode**).
- **Justification**, **ApprovedByName** (the approver in the other tool), **ApprovalDate** and **ReviewDate** are required, with dates as yyyy-MM-dd. **Status** (Active, Expired or Revoked) is optional and blank means Active; **ExternalTicketReference** is optional.
- **LinkToAccountProgress** = Yes also sets the account to Risk Accepted / Excluded, linked to the exception (Active account exceptions only).
- A bad row is reported with its row number; the other rows still import.

An imported exception's page shows its **Approved By** (the other tool's approver), **Source**, and **Imported** (when and by whom).

### Creating one

Choose Account or Application scope. Account scope takes a raw Account Key (there's no search/picker yet — a known limitation, not an oversight); Application scope gets a real dropdown. Fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.

### Managing an existing exception

While an exception is Active, its edit page offers:
- **Re-approve** — pick a new date and click **Extend Review Date**; nothing else changes.
- **Revoke** — enter a **Reason to revoke** (required) and click **Revoke Exception** to end it immediately. The reason is recorded in the Audit Log.

An exception that's no longer Active can't be re-approved or revoked; if someone else changed it first, the page says why.

### Worklists

- **Approval Worklist** (`Exceptions > Approval Worklist`, requires `ApproveExceptions`) — every currently-Active exception, click a row to open it.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date, needing re-approval or revocation.

## Reports

**Where**: **Reports** in the top nav (`/reports`). The reports are listed in a column on the left (as in the Admin hub) and the chosen report fills the right. A report marked with a permission below appears only if you hold it; the others are listed for everyone but need `ViewDashboard` to show data.

**Exporting.** Every report has **Export CSV** and **Export Excel** buttons. The file holds every row that matches the report's current filters and sort, not just the page you see, with the same columns in the same order. Anyone who can view a report can export it. If more rows match than the **Export Max Rows** limit your administrator set (100,000 unless changed), the export is refused with the number of matching rows: narrow the filters and export again. Each export is recorded in the Audit Log (event type `ReportExport`). The file is named `BlueTrack_<Report>_<date-time>`.

- **Overdue / At-Risk** — accounts past their target remediation date that aren't yet Complete.
- **Stage/Status Summary** — an account count per Stage × Status cell, with row totals.
- **Reconciliation Review** (requires `ConfirmReconciliation`) — unconfirmed cross-source account matches; currently read-only, no confirm/reject action wired up yet.
- **Possible Account Matches** (requires `ConfirmReconciliation`) — accounts from other systems that may be a CyberArk account (same user name; the addresses agree by host name or DNS). **Link** (after a confirmation) treats them as one account, as an exact match would; **Dismiss** means they're different, and the pair isn't suggested again. Both are recorded in the Audit Log.
- **Unresolved Entitlement Members** — Safe entitlements whose member BlueTrack can't match to a user or group from the Users and Groups exports, with the member's name and ID. The nightly load matches a member by its exact name (ignoring case) when exactly one user or group has that name; the rest are listed here, typically built-in cloud roles or CyberArk Identity / Entra ID identities. Tick **Show resolved** to also list resolved members, with how each was resolved: *Matched by name*, *Linked by* someone, or *External / cloud role*. If you hold `ConfirmReconciliation` (Approvers and Admins), **Resolve** opens a short form: link the member to a user or group (type at least 2 characters to search; the one user or group with exactly the member's name is filled in for you), or choose **External / cloud role** with an optional note. A resolution applies to that member on every safe, takes effect at once and is kept through the nightly load; **Undo** removes it, so the member is matched by name again or listed as unresolved. Both are recorded in the Audit Log (event type `EntitlementMemberResolution`).
- **Risk Score** (requires `ViewRiskReport`) — every account's Computed/Override/Effective risk score and Risk Band, sortable, with a **Recalculate Now** button and a per-account click-to-expand drill-down showing the real Targets/Access Groups behind that score.
- **Discovered Accounts** (requires `ViewDiscoveredAccounts`) — real AD accounts not yet onboarded into CyberArk, found nightly and risk-scored the same way as tracked accounts. If you hold `ManageDiscoveredAccounts`, **Accept**/**Dismiss** buttons let you move a candidate into real Blueprint tracking or resolve it as a false positive. An account that's already been accepted can't be dismissed; the page says so. **Account Class**, **Owner** and **Matched by** (set up on **Admin > Account Classification**) show whether the account is a person's Primary account, a Secondary or Admin account, or Unknown; whose it is; and which rule (or the default, or a manual setting) decided it, with how the owner was found. **Class:** filters by class, and the export uses the same filter. With `ManageDiscoveredAccounts`, **Classify** on a row sets the class and/or the owner (the owner's sAMAccountName) by hand; that stays through later discovery runs until you **Clear Override**, and is recorded in the Audit Log.
- **Risk Exception SoD** (requires `ViewRiskExceptionSodReport`) — see "Reviewing past cases" below.
- **Safes Flagged for Deletion** — safes whose name matches the *safe decommission pattern* (Global Application Configuration), with how many of their accounts aren't deleted yet. Ignored safes aren't listed.
- **Accounts Flagged for Deletion** — accounts in those safes, or whose own name matches the *account decommission pattern*. Accounts still active are marked **Not deleted yet**; **Also in** names any other safe holding an account with the same username and address, which usually means it has moved there. Tick *Only accounts not yet deleted* to hide the rest.
- **KPI Summary** — the four progress ratios (In Scope vs. All Accounts, Onboarded vs. In Scope, Managed vs. Onboarded, Compliant vs. Managed) as counts and percentages. "In scope" means not currently excluded via an active Risk Accepted / Excluded exception. The same four ratios, plus a summary pie chart, also appear on the Dashboard's Key Progress Indicators card.
- **Trends** — how accounts have moved over time, in three charts: **Accounts by stage** (stacked bars), **% complete** (a line, using the KPI Summary's rule: accounts at the final stage with status Complete, out of the accounts in scope) and **Accounts by risk level** (stacked bars, with a *Not set* series for accounts without a risk level). Filters: **From:** and **To:** (the last 90 days by default), **By:** **Day** or **Week** (a week shows the last snapshot taken in that week, not a sum), **Application:** and **Safe:** (an account's current safe and that safe's application). Each chart has a **Show data table** button with the numbers behind it. The export has one row per day or week, with every chart's columns. The data comes from the nightly snapshot, so there is one point per night; an empty chart means no snapshot has been taken in that range yet.

## Profile & Preferences

**Where**: open the menu with your name at the right of the top nav and choose **Profile & Preferences** (`/profile`).

Shows your display name, mapped role(s), and full permission list. Click **Reload My Rights** to re-check your current group membership immediately, without waiting for anything to expire — useful right after you know you've been added to a new group. Also where you pick a display theme: Light, Dark, or High Visibility.

**Language** lists the languages BlueTrack has; for now that's English only. Until you pick one, BlueTrack uses your browser's language if it has it, otherwise English. Your choice is saved for you, like the theme, so it follows you to other computers.

**Numbers and dates.** Counts on the pages (for example `1,234` or `1.234`) and dates use your browser's language format, unless you pick a language on this page that differs from your browser's. Emails, audit details and other text the server writes keep their own format.

## Targets and Access Groups

**Where**: **Targets** and **Access Groups** in the top nav (`/targets`, `/access-groups`) — promoted out of the Admin hub to their own top-level entries, gated by `ManageTargets`/`ManageAccessGroups` respectively.

These two pages manage the Risk Scoring inventory behind every account's computed risk score (see `Design_Risk-Scoring.md` for the full model). Both follow the same shape:

### Targets

A **Target** is any final destination an account's access leads to — a server, database, application, or other endpoint. The list shows Name, Type, Application, Risk Score (always analyst-set, 0-1000, regardless of how the row was created), and every recognized identifier (e.g. `ADGuid=...`, `FQDN=...`) — a Target can carry several. Filter by Type or Application; click a column header to sort (shift-click to add a secondary key). Click a Target's name to edit it, including adding/removing identifier rows; **+ New Target** opens the same form blank.

**Linked targets.** A target can be *linked* to another target, its primary (made by **Link** on Target Match Review, below). Anything that reaches a linked target counts as its primary, once, at the primary's risk score. Linked targets are hidden from the list, its counts and Select all; set the **Linked targets:** filter to **Show** to see them, each marked **Linked** (the mark opens its primary). A linked target's own page shows its primary under **Linked Target**, and a primary's page lists its **Linked Targets**. **Unlink** (on either page, after a confirmation) makes the target count at its own risk score again; BlueTrack won't link it again automatically. You can also link two targets yourself: on a target's page, **Link to another target**, search for the primary, choose it and click **Link** (after a confirmation). A target that is linked already, or that has targets linked to it, can't be linked; unlink first. A pair you unlinked can be linked again this way.

Below the table, **Link a Single Account to a Target** creates a direct account-to-target link without going through a group — type an account name and pick a Target.

**Select targets for bulk actions** adds checkboxes and the selection bar (as on Account Progress): **Bulk edit…** sets Type, Application, Risk Score or Description on every selected Target (tick *Change* for each field to set), and **Delete…** deletes them after you give a reason, recorded in the Audit Log. A Target still used by an access group or account is skipped, not deleted.

**Bulk Actions** (its own page, reached via the button above the table) offers two CSV imports: **Target Inventory** and the **Account → Target Map**. Each accepts a file upload and reports success/errors per row rather than failing the whole batch on one bad row. When the import has active Import Mapping Profiles, a **Mapping profile** list appears next to the file picker: leave it at **None** for a file in the template's format, or choose the profile that matches the file.

### Access Groups

An **Access Group** is a privileged-access group in the *managed environment* (e.g. an AD "Server Admins" group) — not this app's own CyberArk Safe-permission groups, and not its own login/role mapping (`Design_Risk-Scoring.md` is explicit about keeping these three concepts separate). The list shows Name, Identifier, Scope (Domain/Local — a Local group also shows which Target it was found on), SOR Type, Base Risk Score (analyst-set) and Computed Risk Score (derived from Base plus every reachable Target, marked "(stale)" if it needs recalculating), SOR Address, and Discovery Source. Filter by Scope or SOR Type; sort the same way as Targets. Click a group's name to edit it; **+ New Access Group** for a blank form.

**Select access groups for bulk actions** works the same way: **Bulk edit…** sets Scope, Base Risk Score or Description, and **Delete…** needs a reason. Switching a group to Local scope needs the target it was found on, so groups without one are skipped (set it on the group's own page).

**Bulk Actions** here covers three CSV imports: the group inventory itself, which Targets each group reaches, and which Accounts belong to each group. Each offers a **Mapping profile** choice when the import has active profiles, as on Targets.

An Access Group is unique by name, identifier and the Target it was found on, so a local group such as `BUILTIN\Administrators` (the same SID on every server) can be added once per server; adding an exact duplicate shows a message saying it already exists.

The nightly load also creates a Target for every CyberArk account address that no Target has yet: type *Other*, risk score 0 and source *CyberArk ETL*, waiting for an analyst to set a real score. Each account is linked directly to the Targets matching its address (shown as source *AddressDerived*), so once a Target has a real score, the accounts on it pick up that risk; these links follow address changes and deletions on their own.

Deleting either a Target or an Access Group still referenced by a mapping fails with an explanation rather than silently breaking the mapping — every Delete asks for confirmation first, showing the row's key identifying details.

## Admin Hub

**Where**: **Admin** in the top nav (`/admin`) — a single hub page with a sidebar listing only the sections you hold permission for. If you hold none, the sidebar says so plainly rather than showing an empty page.

### Identity Providers

Manages how people sign in. The list shows Type, Display Name, Enabled, and Order; **+ New Provider** opens a blank form; click a provider's **Display Name** to edit it. Provider Type is WindowsIntegrated, OIDC, SAML, or DevFakeAuth. OIDC gets structured fields (Authority, Client ID, Callback Path, Groups Claim Type); SAML gets its own set (SP/IdP Entity IDs, SSO/SLO destinations, certificate thumbprints — these refer to certificates already installed in the Windows Certificate Store, not a file you upload). A write-only Secret field shows `(already set — leave blank to keep)` once one exists; the stored value is never shown back to you. **Enabling/disabling OIDC or changing its Authority/Client ID/secret needs an app restart to take effect** — SAML settings are read fresh on every request, no restart needed. Setting up a real external IdP (Okta, Entra ID, Ping, Authentik) also requires giving that IdP BlueTrack's own redirect URI/ACS URL/metadata endpoint — see `Claude_Docs/Design_Admin-Deployment-Management.md` for the exact values and how they map onto each vendor's own terminology, not repeated here.

**Sign-in** (below the list) sets the **Default sign-in method**: one of the provider types, or **None (the sign-in page lists the methods)**. A disabled type is marked *(disabled)*. Click **Save**; it applies the next time someone signs in. With a default, people without a session go straight to it (Windows signs in silently; OIDC and SAML go to the identity provider). The section also lists each method's direct address (`/login/windows`, `/login/oidc`, `/login/saml`), which always uses its own method: use it as an identity provider's launch URL or a link you hand out. See "Signing in" above for what users see.

### Group → Role Mapping

Maps a group from a sign-in provider to one of this app's own roles. The list shows Provider, Group, and Role, with Delete per row. A Windows group shows its name (`DOMAIN\Group`) with the stored SID beneath, or the SID with "(name could not be resolved)" if the group was deleted or its domain can't be reached.

**+ Add Mapping** opens a form: pick the **Provider**, then the group and the **Role**, and click **Add**.
- **Windows**: enter the group name (e.g. `BUILTIN\Administrators` or `DOMAIN\GroupName`); it's resolved to its SID when saved.
- **OIDC or SAML**: enter the group name exactly as the identity provider sends it (up to 300 characters). It's matched against the groups claim set on that provider's Identity Providers page, and isn't checked against the identity provider when saved.
- Adding a mapping that already exists shows a message.

The **Lookup / Test Tool** section lets you check a group name and see what it currently grants, without saving anything. Pick the **Provider** (shown when there's more than one), type a **Group name** and click **Resolve**. A Windows group shows the resolved account and its SID; an OIDC or SAML group shows the group claim value. Both show the current role(s) and permission(s).

#### Generating a `db_backupstatus_reader` Grant Script

The Deployment page's SQL Server backup-status check needs a SQL Server account to be a member of a `db_backupstatus_reader` role in `msdb` before it can report anything — a permission this application can never grant itself (see `Admin_OperationsGuide.md` for why). Once you've resolved a Windows group in the Lookup / Test Tool above, a **Generate db_backupstatus_reader Script** button appears below the result.

**Steps**: resolve the target group (above), click **Generate db_backupstatus_reader Script**, and your browser downloads a `.sql` file already targeted at that group. Send it to whoever administers the SQL Server instance — **this application never runs the script itself**; it must be run manually via `sqlcmd` against `msdb`. See `Admin_OperationsGuide.md` for the exact command.

### Roles & Permissions

Manages this app's own roles and which permissions each one bundles. The list shows Role, Description, Notification Email, and a comma-joined list of that role's permissions; Delete is blocked (with an explanation) if the role is still mapped to a group. **+ New Role** opens a blank form, and clicking a role's name opens it for editing: Role Name, Description, Notification Email, and a checklist of every permission in the (fixed, not editable here) permission catalog, each with its own description.

### Application ↔ Safe Mapping

Two sections on one page. **Applications**: Code/Name/Owner; click an application's **Name** to edit it (no Delete here), and **+ New Application** opens a blank form (Code, Name, Description, Owner Name/Email, Technical Contact Name/Email, Notes). **Safes**: each Safe gets a per-row dropdown assigning it to an Application — changing the dropdown saves immediately, there's no separate Save button for this section. Both tables show a page at a time, with their own **« Prev** / **Next »**, at your page size: **Rows per page:** under either table (25, 50, 100 or 250) is saved for you and shared with the other paged lists.

**Bulk Actions** (next to **+ New Application**) opens a page with three CSV imports. Each has a **Download template** link, a file picker and an **Import** button, and reports per-row errors without failing the rest.
- **Applications**: one row per application, matched by ApplicationCode. A new code creates the application; an existing code updates it, and a blank cell leaves that field as it is. A name already used by another application is a row error. The result counts created, updated and unchanged rows.
- **Account → Application Assignments**: one row per account: UserName and Address (both required, matched ignoring case), Application (its code or name; it must already exist) and an optional Source (PrivilegeCloud, SelfHosted or a source system name) for the same username and address in more than one source. Every matching account gets the application as its own, replacing any it had; accounts not in the file are left alone. The result counts accounts assigned, changed and unchanged. A row for an account that has no progress record yet is an error. An administrator can also have a data feed (Admin > Data Sources, import "Account → application assignments") run it on a schedule.
- **Safe → Application Assignments**: one row per safe: SafeName, Application (its code or name; it must already exist) and an optional Source (PrivilegeCloud or SelfHosted). Without a Source, every safe with that name is assigned. An existing assignment is replaced; safes not in the file are left alone. Every change is recorded in the Audit Log, as on the page.

### Secrets Store Configuration

Exactly one backend is active at a time — this is the vault backend used to resolve privileged-account secrets (distinct from the plain named-credential store on the Credentials & LDAP page). The table lists every backend (`WindowsDpapi`, `CyberArkCP`, `CyberArkCCP`, `CyberArkConjur`, `AzureKeyVault`, `AwsSecretsManager`) with backend-specific structured settings fields (e.g. CyberArk CP just needs an App ID; Azure Key Vault needs a Vault URI and an Auth Method, with Tenant ID/Client ID appearing only when that method is Service Principal). A write-only Credential field per row works the same "already set, leave blank to keep" way as everywhere else. Each row's action button reads **Save** if that backend is already active, or **Make Active** to switch to it.

**Test Connection**: pick a Safe/Folder/Object and click **Test** — attempts a real retrieval against the currently active backend and reports success (with non-secret metadata: username/address) or failure, but **never shows the retrieved secret itself**. If `WindowsDpapi` is active, a hint reminds you to enter a Credentials page credential's *name* as Object (Safe/Folder are ignored for that backend).

### Field Metadata Management

The governed list of fields the Account Progress edit form actually renders. List columns: Field Name, Display Label, Type, Required, Order. **+ New Field** opens a blank form, and clicking a field's **Field Name** opens it for editing: Field Name, Display Label, Field Type (a plain text value, not a dropdown), Reference Table, Required, Display Order.

### Audit Log Viewer

Read-only — no add/edit/delete. Filter by Event Type, Entity, and a From/To date range (submitted via an explicit **Filter** button, not automatically as you type). Columns: Occurred At, Event Type, By, Entity, and Detail. Click the **Occurred At** value on any row to expand an inline panel showing the Reason (if one was recorded) and a Field / Old Value / New Value table for that specific change.

**Exporting.** **Export CSV** and **Export Excel** download every event that matches the filters you last applied with **Filter** (not what you've typed since), in the list's current sort, with the list's five columns. **Occurred At** keeps the time, to the second, in UTC, as the list shows it, so the column is headed **Occurred At (UTC)**; report exports show dates only. The Reason and field changes aren't in the file. More events than the **Export Max Rows** limit are refused with the count, so narrow the dates or event type first. Each export is itself recorded in the Audit Log as a `ReportExport` event.

### Global Application Configuration

Every admin-wide setting on one form, saved together with a single **Save** button:
- Idle Timeout (minutes), Breadcrumb Position (Top Left/Top Right) and Exception ID Pattern.
- **Account Progress Lock Timeout (minutes)**: 1 to 720 (12 hours); 30 on a new install. An edit lock with no activity for this long is treated as abandoned (see Account Progress, "Editing an account").
- Audit Retention in days (blank = keep forever) and a "Log read/view events" checkbox.
- **Backup Folder**: where the Deployment page's **Backup App** button and scheduled backups write the database backup file.
- **Scheduled backup time**: a daily backup of the BlueTrack database into the Backup Folder, at this server-local time. Leave blank for none (the default).
- **Keep scheduled backups (days)**: 1 to 3650. After each scheduled backup, older scheduled backup files are deleted; backups made with **Backup App** are never deleted.
- The Risk Score Algorithm (Dominant Plus Tail / Combined Exposure — see `Design_Risk-Scoring.md`) and the segregation-of-duties checkbox (below).
- **Bulk edit limit (accounts)**: 1 to 10000; the most accounts one Account Progress bulk action may change.
- **Export Max Rows**: 1 to 1,000,000 (100,000 on a new install); the most rows one report or Audit Log export (**Export CSV** / **Export Excel**) may hold. When more rows match, the export is refused with the count rather than cut short.
- **API key default lifetime (days)**: 1 to 365 (90 on a new install); how long a new API key lasts. See **Admin > API Keys**.
- **Site URL**: the address people open BlueTrack at (for example `https://bluetrack.company.com`; `https://` only, or `http://` for localhost). The notification emails link to pages under it; leave it blank and they name the pages instead. The installer fills it in when it's blank.

#### Data feeds

Settings for the scheduled feeds on **Admin > Data Sources** (see `Admin_Configuration.md`, "Data Sources"):
- **Nightly run time**: server local time; default 04:00, after the 02:00 Import and Load job.
- **Keep run history (days)**: 1 to 365.
- **Business hours start**, **Business hours end** and **Business days**: **Run now** asks for confirmation inside these hours, since an import changes data people are working with.

#### Decommissioned and ignored safes

Three name patterns, each with a type (**Off**, **Prefix**, **Suffix** or **Regex**) and a value; all ignore case:
- **Safe decommission pattern** — safes being retired, e.g. a `DEL_` prefix or `_DECOM` suffix. Shown on *Safes Flagged for Deletion*; their accounts are marked and listed on *Accounts Flagged for Deletion*.
- **Account decommission pattern** — accounts whose own name marks them for deletion.
- **Ignored safe pattern** — safes left out of the nightly import, e.g. a `ZZ_` prefix. Accounts already imported from such a safe are flagged deleted, labelled **Not imported (ignored safe)** (their history stays).

Use **Test patterns** at the top of the section to see which sample names (one per line) each pattern matches before you save; it uses exactly the same matching as the reports and the import. **Regex** works only when BlueTrack's database is on SQL Server 2025; on earlier versions the option is disabled. Example: `^(DEL|ZZ)_|_DECOM$` matches `DEL_Finance`, `zz_Archive` and `Finance_Decom`.

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
- **SQL Server Backup Status**: a table of Backup Type / Last Backup Finish Date, read from SQL Server's own native backup history — or an alert explaining why it's unavailable (typically a missing `db_backupstatus_reader` grant; see `Admin_OperationsGuide.md`). A **Backup App** button (gated separately by `TriggerBackup`, distinct from just viewing this page) triggers a real on-demand database backup and downloads a zip of the app's own config files. **Recent backups** lists recent backups, scheduled or manual: Started (UTC), Type, Outcome, File, Old files deleted, and Note.

### Notifications

SMTP configuration, notification-type role targeting, and recipients, all on one page.
- **SMTP Configuration**: Host, Port, "Enable STARTTLS," Auth Method (None (open relay), Basic (username/password), Microsoft 365 (OAuth2), Google Workspace (OAuth2) or Generic OAuth2 (client credentials); each shows only the fields it needs, and the password, client secret, certificate or key file is a credential chosen from the Credentials & LDAP page — setup per provider in `Admin_SettingsReference_Notifications.md`), From Address/Display Name, and two TLS-relaxation checkboxes ("Ignore CRL issues," "Ignore all SSL errors — use with caution").
- **Notification Types**: a table where each notification type has an **Enabled** checkbox (the newer types NightlyImportFailed, OverdueRiskExceptions and WeeklySummary start turned off) and an optional Target Role via a per-row dropdown; both save immediately — that role's recipients are resolved from the role's own email plus (if LDAP is configured) every member of its mapped AD groups, *additive* to the flat recipient list below.
- **Schedule**: **Daily digest time:** (the OverdueRiskExceptions digest), **Weekly summary day:** and **Weekly summary time:** (the WeeklySummary), in the server's local time; **Save Schedule** saves them.
- **Test Connection**: a **Send Test Email** button sends a real email, using the saved SMTP config, to every active recipient; it is recorded in the Audit Log as a `ConnectionTest` event.
- **Recipients**: Email / Display Name / Active, with Deactivate-or-Activate and Delete per row; **+ Add Recipient** opens its own small form (Email, Display Name).

### Credentials & LDAP

Two independent sections. **Credentials** is a generic named-credential store (distinct from the single active Secrets Store backend above) — list shows Name/Backend/Username/Scope; **+ New Credential** opens an inline form whose fields depend on the chosen Backend (Windows DPAPI takes Username/Password plus a Machine-vs-User DPAPI Scope choice — a new "User" scope credential starts as Machine scope and upgrades automatically the first time the app itself decrypts it; every other backend takes Safe/Folder/Object). Each row has Edit, Delete, and **Test** (confirms the credential resolves, inline).

**LDAP Configuration**: one row per AD domain/forest this app needs to query (recipient resolution, AD Account Discovery) — each independently enabled and bound with its own credential. **+ New Domain** opens a form: Domain Name, Enabled, Domain Controller (blank = default), Search Base, Use SSL, and either "Use trusted connection" (the app pool/local computer account) or a Bind Account Credential picked from the Credentials list above.

### Target Match Review

Requires `ManageTargets`. A queue of weak import matches (e.g. an identifier that's only an IP address, which can be reassigned by DHCP/NAT) that weren't merged automatically and need a human decision. Each row shows the **Identifier**, the **Imported Target** (its name and type, Source, Risk Score and any other identifiers from the file), the **Candidate Target** it weakly matched, the **Source File**, and a **Link To** search box. A row queued before the imported details were kept shows *Not stored (queued before this version). Name:* with a box for the new target's name.

Three buttons, with no confirmation dialog (none of them changes an existing target's identifiers):
- **Link** creates a target from the imported row and links it to the candidate, or to another target you choose in **Link To**: type at least 2 characters of a target's name or of any identifier, then pick from the list (up to 20 matches, each with its type and identifiers). The IP address stays where it is, and the pair counts once, at the risk score of the target it's linked to (see Targets, "Linked targets").
- **New Target** creates a separate target from the imported row, not linked.
- **Ignore** dismisses the row, and later imports skip the same row (same source and name).

Later imports of a row you linked or made a new target from update that target instead of queueing it again; a target you **Unlink** isn't linked again. A match someone else has already resolved is refused with a message.

### Import Mapping Profiles

One named profile per distinct source file shape for the risk-scoring import feeds — not needed at all for this app's own generated CSV templates, only for uploading an existing external export as-is. List shows Feed Type, Name, Active, and a flattened summary of every mapped field. **+ New Profile** opens a blank form, and clicking a profile's **Name** opens it for editing: Feed Type, Profile Name, Active, Description, and a repeatable Fields list (Source Column, Internal Field, Required, Default value, each removable, plus **+ Add Field**).

### Risk Score Bands

Named ranges over the computed risk score (e.g. Low/Medium/High/Critical) — the resulting band name is what shows up on the Account Progress list and the Risk Score report. List shows Name/Min Score/Max Score/Order. **+ New Band** opens a blank form, and clicking a band's **Name** opens it for editing (Name, Min Score, Max Score, Risk Order); **a new or edited range can't overlap any other existing band** — saving an overlapping range shows a message saying why rather than a generic failure.

### Account Classification

Requires `ManageDiscoveredAccounts`. The settings and rules (AD attribute, name prefix/suffix/pattern, OU, shared employee ID) behind the Account Class and Owner columns on Reports > Discovered Accounts. Off, with no rules, until set up; every change reclassifies all discovered accounts at once and is recorded in the Audit Log. Each setting and rule type is described in the Settings Reference ("Account Classification").

### SIEM Forwarding

Requires `ManageApplicationConfiguration`. Sends every new audit log event to your SIEM's syslog collector as CEF, over TLS or TCP: the collector, how its TLS certificate is trusted, the message settings, which event types are sent and at what severity, the forwarding status, and **Send Test Event**. Details: `Admin_SettingsReference_Notifications.md`, "SIEM Forwarding".

## See also

- `README.md` (this folder) — the index of short, linear, role-based walkthroughs (Admin_Installation/Configuration/DataManagement, User_Viewer/Analyst/Approver/Auditor) that supplement this page's field-by-field reference rather than duplicate it.
- `Admin_OperationsGuide.md` — the DBA/operator-facing side of everything with no UI of its own (rollback backup/restore scripts) or that a DBA has to act on once this app hands it over (the generated `db_backupstatus_reader` script).
- `Claude_Docs/Design_Application-Structure.md` — the authoritative full page inventory and navigation conventions this guide is organized around.
- `Claude_Docs/Design_Risk-Exception-Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
- `Claude_Docs/Design_Risk-Scoring.md` / `Design_Risk-Scoring-Import.md` — the data model and algorithm behind Targets, Access Groups, the Risk Score tab/report, and Risk Bands.
- `Claude_Docs/Design_Interface-Extensibility.md` — the field-metadata system behind the Account Progress edit form and Field Metadata Management.
- `Claude_Docs/Design_Credentials-Management.md` — the Credentials & LDAP page's own design rationale.
- `Claude_Docs/Design_Notifications.md` — the Notifications page's own design rationale.
- `Claude_Docs/Design_Admin-Deployment-Management.md` — the Identity Providers/Secrets Store structured-field UI and the Deployment page, including the backup-status check that `db_backupstatus_reader` exists to support.
