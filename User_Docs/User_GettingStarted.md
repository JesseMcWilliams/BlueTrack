# Getting Started with BlueTrack

**Blueprint Progress Tracking Web Interface**

## About this guide

This guide is for someone opening BlueTrack for the first time: a new analyst, an approver, or a team lead who wants to follow progress. You don't need to know BlueTrack or the CyberArk PAM Blueprint already.

By the end you should understand how an account moves from "we found it" to "it's safely managed", who does what along the way, and what your first real tasks are. For page-by-page detail, see the guides listed in "Where to go next" at the end.

## The big picture

Your organization is bringing its privileged accounts under control with CyberArk. These are the powerful accounts: administrators, service accounts, database owners. CyberArk publishes a recommended way to do this, called the **PAM Blueprint**. In short: find the accounts, decide which matter most, store them in the CyberArk vault, let CyberArk manage their passwords, then confirm it all works.

That takes months, and covers thousands of accounts. BlueTrack is where the program keeps track of it:

- **Every night**, BlueTrack loads the latest data from CyberArk, so it knows which accounts exist and which safes they're in.
- **Every account** gets a progress record: which Blueprint stage it has reached, its status, who owns it, and when it's due.
- **When an account can't follow the normal path**, a **risk exception** records who accepted the risk, why, and when to look at it again.
- **A risk score** for each account helps you decide what to work on first.
- **The Dashboard and reports** show how far the program has come and what's falling behind.

The usual flow looks like this:

1. The nightly load brings in accounts from CyberArk.
2. Analysts assess each account, fill in its owner and risk, and move it through the stages.
3. Accounts that can't be fully managed get an approved risk exception instead.
4. Team leads and approvers watch the Dashboard, the reports and the worklists.

## Key terms

| Term | What it means in BlueTrack |
|---|---|
| Account | A privileged account, usually one stored in CyberArk. It's shown by its **Username** and **Address** (the server or system it belongs to). |
| Safe | A container in CyberArk that holds accounts. Every account imported from CyberArk sits in a safe. |
| Application | A business system (for example, a payroll system). Each safe can be assigned to one application, so you can see and manage accounts by the system they serve. |
| Blueprint Stage | How far an account has come, from **Discovered** to **Verified / Compliant**. See "The five stages" below. |
| Status | Where the work on the current stage stands: **Not Started**, **In Progress**, **Blocked**, **Complete**, or **Risk Accepted / Excluded**. |
| Risk Level | A label you assign during assessment: **Low**, **Medium**, **High** or **Critical**. |
| Risk Score | A number from 0 to 1000, worked out from what the account can reach. Higher means more dangerous if misused. |
| Risk Band | A named range of risk scores (for example, High), set up by your administrator. |
| Target | Something an account's access leads to: a server, database, application or other endpoint. Each target has its own risk score. |
| Access Group | A privileged group in your environment, such as an Active Directory "Server Admins" group. Accounts in the group reach the group's targets. |
| Risk Exception | An approved, time-limited decision to accept the risk of an account (or a whole application) not being fully managed. It has an Exception ID, a justification and a review date. |
| Discovered Account | An Active Directory account found in a known privileged group that isn't in CyberArk yet. |
| Flagged for deletion | An account in a safe (or with a name) that matches your organization's "being retired" naming pattern. Shown with a **⚠**. |
| Ignored safe | A safe your administrator has told BlueTrack to leave out of the nightly import. |

## Signing in and finding your way around

Open the BlueTrack address your administrator gave you, such as `https://<server>/`. Depending on how your organization set it up, you're signed in with your Windows account straight away, or sent to your organization's sign-in page and back. If the **Sign in** page lists several methods, choose yours.

The top navigation has **Dashboard**, **Access Groups**, **Accounts**, **Admin**, **Exceptions**, **Reports** and **Targets**. Your name is on the right. Its menu has **Profile & Preferences** and **Sign out**.

### Roles decide what you see

What you can see and do depends on your **role**. Your administrator gives roles to groups, so you get your role from the groups you're in. BlueTrack comes with these roles; your organization may have changed them or added others.

| Role | What it's for | Main permissions |
|---|---|---|
| Viewer | Following progress, read-only. | `ViewDashboard` |
| Analyst | Working accounts through the stages and keeping targets and access groups up to date. | `ViewDashboard`, `EditAccountProgress`, `ManageTargets`, `ManageAccessGroups` |
| Approver | Approving, extending and revoking risk exceptions, as well as editing accounts. | `ViewDashboard`, `EditAccountProgress`, `ApproveExceptions`, `ConfirmReconciliation` |
| Auditor | Reading the audit trail. | `ViewAuditLog` only |
| Admin | Setting up and running BlueTrack. | Every permission |

A few things to know:

- **Access Groups** and **Targets** only appear in the top navigation if your role can manage them.
- The Dashboard, **Accounts**, **Exceptions** and most reports need `ViewDashboard`. Without it, those pages show an error such as "Request failed: 403" instead of data. The Auditor role doesn't include it.
- Inside **Admin**, the sidebar lists only the sections you have permission for.
- Some work is admin-only out of the box: deleting accounts, the Discovered Accounts report, and data feeds. (Analysts can also see the Risk Score report.) Your administrator can grant these to other roles.
- To check exactly what you hold, open **Profile & Preferences**. If you've just been added to a group, click **Reload My Rights**. Otherwise the change can take up to 15 minutes to reach you.

## How data gets into BlueTrack

Most of BlueTrack's data comes in on its own. You rarely load anything by hand.

### The nightly import and load

Each night (at 2:00 AM unless your administrator changed it), a scheduled job does two things:

1. **Import**: reads the latest exports from CyberArk (Privilege Cloud, Self-Hosted, or both).
2. **Load**: updates BlueTrack's accounts, safes and permissions from that data, then recalculates risk scores and looks for discovered accounts.

What this means for you:

- **New accounts appear on their own**, at the first stage, **Discovered**, with status **Not Started**. Some move straight to **Onboarded to Vault** (see "The five stages" below).
- **Your work is kept.** The nightly load adds new accounts but doesn't overwrite the stage, status, owner or notes you've entered.
- **Accounts deleted in CyberArk** are kept in BlueTrack with their history, but marked deleted and hidden from lists and reports.
- **Accounts in ignored safes** aren't imported. Any already in BlueTrack are marked deleted, labelled **Not imported (ignored safe)**.
- **New addresses become targets.** For each account address BlueTrack hasn't seen before, it adds a target of type *Other* with a risk score of 0, waiting for someone to set a real score.

### Data feeds and CSV imports

Some information doesn't come from CyberArk, such as which servers exist, which groups reach them, and which applications own which safes. There are two ways to bring it in:

- **Data feeds** (admin): **Admin > Data Sources** picks up CSV files from folders every night, after the CyberArk load.
- **Bulk Actions** (by hand): **Targets**, **Access Groups**, **Exceptions** and **Admin > Application ↔ Safe Mapping** each have a **Bulk Actions** button. It opens a CSV import with a **Download template** link. A bad row is reported with its row number, and the other rows still import.

### If the data looks out of date

The changes you make in BlueTrack show up straight away. Only CyberArk data waits for the nightly run. If an account you know was added or changed in CyberArk more than a day ago still isn't right:

1. Check you're not hiding it. For example, the **Deleted accounts** filter on **Accounts** is set to **Hide** by default.
2. Check whether its safe matches the ignored safe pattern.
3. Tell your BlueTrack administrator. They can check whether last night's job succeeded and run it again if needed. BlueTrack doesn't show the job's result on a page.

## Working an account through its lifecycle

This is the heart of BlueTrack. Every account has one progress record, and your job is to move it forward, one stage at a time, until it's verified.

### The five stages

| # | Stage | What it means | How an account gets here |
|---|---|---|---|
| 1 | Discovered | BlueTrack knows the account exists. Nobody has looked at it yet. | Automatic: every new account starts here. An account accepted from the Discovered Accounts report also starts here. |
| 2 | Assessed / Prioritized | Someone has reviewed it: who owns it, what it's for, how risky it is, and when it should be done. | An analyst sets it after filling in the assessment fields. |
| 3 | Onboarded to Vault | The account is stored in the CyberArk vault. | Often automatic (see below). Otherwise an analyst sets it. |
| 4 | Managed / Rotation Enabled | CyberArk manages the account, for example by rotating its password. | An analyst sets it. BlueTrack doesn't detect rotation on its own. |
| 5 | Verified / Compliant | The setup has been checked and the account meets your controls. | An analyst sets it, together with status **Complete**. |

Your organization may attach its own exact meaning to each stage. Ask your team lead how your program defines "assessed" or "verified".

**The automatic move to Onboarded to Vault.** Each night, an account that was imported from CyberArk moves to **Onboarded to Vault** if all of these are true:

- It's still at **Discovered** with status **Not Started**, so nobody has worked on it yet.
- It isn't deleted.
- Its safe name doesn't contain `_Pending`. CyberArk uses such safes for accounts it has found but that aren't onboarded yet.

So when an account leaves a pending safe for a real one, it moves forward on its own the next night. The automatic move never touches an account someone has already changed. If you've already marked an account **Assessed / Prioritized**, you move it to **Onboarded to Vault** yourself.

### The statuses

The **Status** tells you how the work at the current stage is going:

- **Not Started**: nothing done yet at this stage.
- **In Progress**: someone is working on it.
- **Blocked**: something outside your control is holding it up. Say what in **Notes**.
- **Complete**: done. This needs an **Actual Completion Date**.
- **Risk Accepted / Excluded**: the account won't go further, and an approved risk exception backs that up. It needs a linked exception (see "When an account can't meet a control").

### The fields you fill in

The **Details** tab of an account shows these fields (your administrator can adjust the list):

- **Blueprint Stage** and **Status** (both required).
- **Risk Level**: Low, Medium, High or Critical, chosen during assessment.
- **Account Type** and **Source of Record**: may already be filled in, based on the account's CyberArk platform. Correct them if they're wrong for this account.
- **Owner Name** and **Business Unit**: who is responsible for the account.
- **Target Remediation Date**: when the account should be done. Once this date passes without an **Actual Completion Date**, the account shows up as overdue.
- **Actual Completion Date**: when it was actually done.
- **Notes**: anything the next person needs to know.

### Opening and editing an account

1. Click **Accounts** in the top navigation.
2. Use **Search** (username, address or account name) and the **Stage**, **Status**, **Risk Level** and **Owner** filters to find it. Click a column header to sort.
3. Click the account's **Username** to open it.

When you open an account with `EditAccountProgress`, BlueTrack **locks** it for you so two people can't overwrite each other's changes:

- If someone else is editing it, you see "Currently being edited by" with their name and when they started. You get a read-only view. Use **Force Release Lock** only if you're sure they've stopped, for example because they've gone home.
- Your lock stays active while the page is open. A lock left idle for the lock timeout (30 minutes on a new install) is released on its own.
- Your own lock never blocks you, even from another browser tab.

Make your changes and click **Save**, or **Cancel** to leave without saving. Either way you go back to the Accounts list.

Two rules apply when you save:

- **Moving backward needs a reason.** If you set an earlier stage than the account's current one, fill in **Reason**. It's recorded in the audit log, so others can see why the account went back.
- **Complete needs a date.** Setting status **Complete** needs an **Actual Completion Date**.

If your administrator has limited a field to certain people, saving a change to it without the right permission is refused with a message naming the permission.

### The Risk Score tab

The **Risk Score** tab shows **Calculated Risk**, **Risk Band** and **Effective Risk Score**. The score comes from the targets the account can reach, directly or through its access groups.

- **Recalculate** refreshes this one account's score straight away, without waiting for the nightly run.
- **Override Score** lets you set the score by hand (0 to 1000) when you know better than the calculation. A **Reason** is required. Leave it blank and click **Save Override** to go back to the calculated score.

The **Effective Risk Score** is the override if there is one, and the calculated score if not. It's the number used in lists and reports.

### Changing many accounts at once

When the same change applies to many accounts, for example a new owner for 40 service accounts, use bulk edit:

1. On **Accounts**, tick **Select accounts for bulk actions**.
2. Tick the rows you want, or use **Select all on page** or **Select all matching** to take everything that matches your search and filters.
3. Click **Bulk edit…**. On the **Bulk Edit Accounts** page, tick **Change** next to each field you want to set. Fields you don't tick stay as they are on each account.
4. For **Notes**, choose **Add to the existing notes** or **Replace the notes**.
5. Fill in **Reason** if the new stage is earlier than any account's current stage.

You can't link a risk exception in bulk. Do that on each account. Accounts someone else is editing are skipped and listed, and stay selected so you can try again later. There's a limit to how many accounts one bulk action may change (500 unless your administrator changed it).

### Deleting and undeleting

Sometimes an account shouldn't be tracked at all, for example a test account. With the `DeleteAccounts` permission (admins by default), use **Delete account…** on the account's page, or **Delete…** after selecting accounts on the list.

- A **reason is always required**, for undelete too.
- Deleting hides the account but keeps its progress, exceptions and history. A BlueTrack delete stays in place through the nightly import.
- **Undelete account…** reverses a delete made in BlueTrack. An account deleted in CyberArk comes back only if it reappears in CyberArk.
- **Delete / undelete history** on the account's page shows who did what, when and why.
- To see deleted accounts on the list, set the **Deleted accounts** filter to **Show** or **Only deleted**.

### How the Dashboard reflects your work

Every save changes the Dashboard straight away:

- **Accounts by Stage** counts accounts at each stage, and how many are **Complete**.
- **Key Progress Indicators** shows four ratios, each narrowing the one before:

| Indicator | Counts as "yes" when the account is... |
|---|---|
| In Scope vs. All Accounts | not **Risk Accepted / Excluded**. |
| Onboarded vs. In Scope | in scope, and at **Onboarded to Vault** or later. |
| Managed vs. Onboarded | in scope, and at **Managed / Rotation Enabled** or later. |
| Compliant vs. Managed | at **Verified / Compliant** with status **Complete**. |

Deleted accounts aren't counted. An account at stage 5 that's still **In Progress** isn't counted as compliant yet.

### Example: one account from first import to done

Here's how a typical service account, `svc-backup` on `app-server-01.example.com`, might move through BlueTrack.

1. **Day 1, first import.** CyberArk's discovery puts the account in a safe named `Backup_Pending`. The nightly load adds it to BlueTrack at **Discovered**, **Not Started**. Its address is new, so BlueTrack also adds a target named after the server, with a score of 0.
2. **Day 2, an analyst sets the target's score.** On **Targets**, the analyst opens the new server target and sets a real **Risk Score**, because it's a production backup server. The account's calculated risk goes up at the next recalculation (or straight away with **Recalculate** on the account).
3. **Day 3, assessment.** The analyst opens the account, sets **Risk Level** to High, fills in **Owner Name** and **Business Unit**, sets a **Target Remediation Date** six weeks out, and moves it to **Assessed / Prioritized**, **In Progress**.
4. **Week 2, onboarding.** The CyberArk team moves the account from the pending safe into its real safe. The account was already assessed, so the automatic move doesn't apply. The analyst sets **Onboarded to Vault**, **In Progress**.
5. **Week 4, password management.** Password rotation is turned on in CyberArk. The analyst sets **Managed / Rotation Enabled**. One dependent application breaks after a rotation, so the status goes to **Blocked**, with a note explaining why.
6. **Week 5, verification.** The fix is in and the owner confirms everything works. The analyst sets **Verified / Compliant**, **Complete**, with today's **Actual Completion Date**.

The account now counts as compliant on the Dashboard. Because it has an Actual Completion Date, it never shows as overdue, even though the target date eventually passes.

## When an account can't meet a control: risk exceptions

Not every account can be vaulted or rotated. A legacy system might break if its password changes, or an account might be retired next month. Rather than leaving such accounts stuck, the business can **accept the risk** for a set time. BlueTrack records that as a **risk exception**.

### What a risk exception holds

- An **Exception ID** (for example `EXC-2026-0001`), to quote in tickets and audits.
- A **scope**: one **Account**, or a whole **Application** (all the accounts in its safes).
- A **Justification**: why the risk is accepted.
- **Approved By** and **Approval Date**.
- A **Review Date**: exceptions are deliberately temporary, so someone must look at it again by this date.
- A **Status**: **Active**, **Expired** or **Revoked**.
- An optional **External Ticket Reference**.

### Who does what

BlueTrack has no separate "request" step. Creating an exception approves it at the same time, so only people with `ApproveExceptions` (Approvers and Admins by default) can create one.

1. **The analyst agrees the case** with the account owner and an approver, outside BlueTrack or through your usual ticket process.
2. **The approver creates the exception.** They use **+ New Exception** on **Exceptions**, or open the account, set **Status** to **Risk Accepted / Excluded**, and use **+ Create New Exception** on its **Risk Exception** tab. Creating it from the account is easiest, because the exception is tied to that account automatically.
3. **Someone links it to the account.** An account can only be saved as **Risk Accepted / Excluded** with an Active exception for that account chosen under **Linked Exception**. Any user with `EditAccountProgress`, including an analyst, can link an existing exception.
4. **The account leaves scope.** It no longer counts toward the in-scope total on the Dashboard.

Accounts covered by an application-wide exception show a note on their page naming that exception and its review date.

### Segregation of duties

Your administrator can turn on **Enforce segregation of duties on Risk Exception approval**. When it's on, **the person who approved an exception can't be the one who links it to an account**. Trying shows a message and nothing is saved, so have a colleague link it. This is off by default, for teams too small to split the work.

Whether or not it's on, **Reports > Risk Exception SoD** lists every case where the same person approved an exception and linked it, so auditors can review them.

### Reviewing, extending and revoking

BlueTrack doesn't end an exception on its own when its review date passes. Instead, the exception shows up for review:

- The Dashboard's **Risk Exceptions Needing Attention** card counts exceptions past their review date. Its **View worklist** link opens **Overdue Reviews**.
- Approvers also see a count of Active exceptions, and a **View worklist** link to the **Exception Approval Worklist**.

For each one, an approver opens the exception and either:

- **Extends it**: picks a new date under **Re-approve (extend review date)** and clicks **Extend Review Date**, or
- **Revokes it**: fills in **Reason to revoke** (required) and clicks **Revoke Exception**.

To handle many at once, tick **Select exceptions for bulk actions** on **Exceptions**, then use **Extend review…** or **Revoke…**. Only Active exceptions can be extended or revoked.

Revoking an exception doesn't change the account's status. After revoking, open the account and either move it back to a working status or link a new exception.

If your organization approves exceptions in another tool, an approver can bring them in with **Bulk Actions** on **Exceptions**. Each one gets a BlueTrack Exception ID and keeps a link back to its source.

## Targets, access groups, applications and discovered accounts

These pages hold the information behind risk scores and ownership. You won't use them every day, but they make BlueTrack's priorities accurate.

### Targets

**Targets** (for `ManageTargets`) lists the servers, databases and other endpoints accounts can reach. Each target has a **Risk Score** (0 to 1000) that you set, plus identifiers such as a hostname or IP address.

**When you'd use it:** after a nightly load adds new *Other* targets with a score of 0, set real scores so accounts on important systems rank higher. **Link a Single Account to a Target** connects an account to a target directly.

### Access groups

**Access Groups** (for `ManageAccessGroups`) lists privileged groups in your environment and which targets they reach. Each group has a **Base Risk Score**; its **Computed Risk Score** adds in what it can reach.

**When you'd use it:** when your organization adds or reviews privileged groups. Every account in a group picks up its risk.

### Applications and safes

**Admin > Application ↔ Safe Mapping** (for `CurateApplicationMapping`, admins by default) lists applications and assigns each CyberArk safe to one. That decides which accounts an application-wide risk exception covers.

**When you'd use it:** when a new application's safes are created, or before an approver creates an application-wide exception.

### Target Match Review

**Admin > Target Match Review** (also for `ManageTargets`) holds imported identifiers that only weakly match an existing target, such as a bare IP address that could have been reused. For each one choose **Merge**, **New Target** or **Ignore**.

**When you'd use it:** after a targets import, or every week or so.

### Discovered accounts

**Reports > Discovered Accounts** (for `ViewDiscoveredAccounts`) lists Active Directory accounts that belong to known privileged groups but aren't in CyberArk. These are risky accounts nobody is tracking yet. With `ManageDiscoveredAccounts`, **Accept** adds one to BlueTrack at **Discovered**, **Not Started**, and **Dismiss** marks it as a false positive.

**When you'd use it:** as a regular check that new privileged accounts aren't slipping past the program.

## Reports and the Dashboard

| Where | The question it answers |
|---|---|
| Dashboard | How are we doing overall, and is anything urgent? |
| Reports > Overdue / At-Risk | Which accounts have passed their target remediation date without being completed? |
| Reports > Stage/Status Summary | How many accounts sit at each stage and status? Where is work piling up? |
| Reports > KPI Summary | What share of accounts are in scope, onboarded, managed and compliant, as counts and percentages? |
| Reports > Risk Score | Which accounts are the most dangerous, and which targets and access groups make them so? |
| Reports > Discovered Accounts | Which privileged AD accounts aren't in CyberArk yet? |
| Reports > Safes Flagged for Deletion | Which safes are being retired, and how many of their accounts are still active? |
| Reports > Accounts Flagged for Deletion | Which accounts are due to be removed, and have any already moved to another safe? |
| Reports > Unresolved Entitlement Members | Which safe permissions belong to someone BlueTrack can't identify? |
| Reports > Reconciliation Review | Which accounts might be the same account in both Privilege Cloud and Self-Hosted? (Read-only for now.) |
| Reports > Risk Exception SoD | Who both approved an exception and linked it to an account? |
| Exceptions > Overdue Reviews | Which exceptions are past their review date? |

Some reports appear only if your role has the matching permission.

## Your first week

### Everyone

1. Sign in, open **Profile & Preferences**, and check your role and permissions. Pick a display theme (Light, Dark or High Visibility) while you're there.
2. Look at the Dashboard and open each card's link to see the report behind it.
3. Ask your team lead how your program defines each stage, and what a good Target Remediation Date looks like.

### Viewer or team lead

1. Note the four Key Progress Indicators this week, so you can see the trend next week.
2. Open **Reports > Overdue / At-Risk** and **Reports > Stage/Status Summary** to see where work is stuck.
3. Check **Exceptions > Overdue Reviews** for exceptions nobody has looked at.

### Analyst

1. On **Accounts**, filter **Stage** to **Discovered** and pick a handful of accounts to assess.
2. For each, fill in Risk Level, Owner Name, Business Unit and a Target Remediation Date, and move it to **Assessed / Prioritized**.
3. Move one account a stage forward, then go back to an earlier stage, and see the **Reason** you're asked for.
4. On **Targets**, find a few *Other* targets with a score of 0 and set real scores.
5. Check **Admin > Target Match Review** for anything waiting.
6. Try a small bulk edit, such as setting the owner on three accounts.

### Approver

1. Read through the active exceptions on **Exceptions** and their justifications.
2. Work **Exceptions > Overdue Reviews**: extend or revoke each one.
3. Create one exception from an account's **Risk Exception** tab, so you know the flow.
4. Find out whether segregation of duties is on, and agree with your analysts who links exceptions.

### Auditor

1. Open **Admin > Audit Log Viewer**, filter by date range, and click **Filter**.
2. Click a row's **Occurred At** to see the reason and the old and new values.
3. If you also need the Dashboard and reports, ask for a role with `ViewDashboard`.

## Where to go next

- `User_Viewer.md`, `User_Analyst.md`, `User_Approver.md` and `User_Auditor.md`: short guides for each role.
- `User_Guide.md`: the full reference, page by page, including every Admin section.
- Your BlueTrack administrator: for access, roles, the nightly import, and anything that looks wrong in the data.
