# User Guide: Analyst

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewDashboard` + `EditAccountProgress` + `ManageAccessGroups` + `ManageTargets` — tracking account progress and maintaining the risk-scoring inventory.

## The Dashboard

**Where**: `Dashboard` in the top nav. Four summary cards, each linking to the full report behind it. See `User_Guide.md`'s Dashboard section for details.

## Working Account Progress

**Where**: **Accounts** in the top nav (`/accounts`).

Search by username, address or account name, and filter the list by Stage, Status, Risk Level, or Owner; click an account's Username to open it. An account with no username is listed by its account name, marked *(no username)*. A **⚠** marks an account flagged for deletion.

Opening an account tries to acquire an edit lock automatically — if someone else has it locked, you'll see who, with a **Force Release Lock** button. An account you already have open in another tab (or left without saving) opens for editing as usual. A lock with no activity for the lock timeout (30 minutes on a new install) is released on its own, so the next person to open the account gets it.

To change many accounts at once, tick **Select accounts for bulk actions**, select them, and click **Bulk edit…**. See `User_Guide.md`, "Selecting accounts for bulk actions".

The edit form has up to three tabs:
- **Details** — stage, status, risk level, owner, business unit, dates, notes. A Reason is required only when regressing an account to an earlier stage.
- **Risk Score** — read-only computed values, a **Recalculate** button, and an editable Override Score.
- **Risk Exception** — appears only when Status is "Risk Accepted / Excluded"; pick an existing Active exception or create one inline.

If your organization has segregation of duties turned on, you can't link an exception you approved yourself — pick a different exception, or ask a colleague to link it.

## Maintaining Targets and Access Groups

**Where**: **Targets** and **Access Groups** in the top nav (`/targets`, `/access-groups`).

- **Targets** — servers, databases, applications, or other endpoints an account's access leads to. Each has a Risk Score (0-1000) and one or more identifiers. Click a Target's name to edit it. Use **Bulk Actions** for CSV imports. The nightly load adds a Target (type *Other*, risk score 0) for each account address that has none yet; set a real score on these.
- **Access Groups** — privileged-access groups in the managed environment, each with a Base Risk Score and a derived Computed Risk Score. Click a group's name to edit it. Use **Bulk Actions** here too. The same local group can be added once per server.
- **Bulk edit and delete** — tick **Select targets for bulk actions** (or **Select access groups for bulk actions**), select rows, then **Bulk edit…** to set fields on all of them or **Delete…** to delete them. A bulk delete needs a reason. Items that can't be changed (for example, still in use) are skipped and listed.

If a save or delete is refused (a duplicate name or identifier, or a row still in use), the page says why.

## Reports you'll likely use

**Reports** in the top nav — Risk Score, KPI Summary, Stage/Status Summary, and Overdue/At-Risk.

## See also

- `User_Guide.md` — the full reference guide for every page.
- `Claude_Docs/Design_Risk-Scoring.md` — the model behind Targets, Access Groups, and computed risk scores.
