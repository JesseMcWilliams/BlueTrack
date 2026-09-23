# User Guide: Analyst

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewDashboard` + `EditAccountProgress` + `ManageAccessGroups` + `ManageTargets` — tracking account progress and maintaining the risk-scoring inventory.

## The Dashboard

**Where**: `Dashboard` in the top nav. Four summary cards, each linking to the full report behind it. See `User_Guide.md`'s Dashboard section for details.

## Working Account Progress

**Where**: **Accounts** in the top nav (`/accounts`).

Filter the list by Stage, Status, Risk Level, or Owner; click an account's Username to open it. Opening an account tries to acquire an edit lock automatically — if someone else has it locked, you'll see who, with a **Force Release Lock** button.

The edit form has up to three tabs:
- **Details** — stage, status, risk level, owner, business unit, dates, notes. A Reason is required only when regressing an account to an earlier stage.
- **Risk Score** — read-only computed values, a **Recalculate** button, and an editable Override Score.
- **Risk Exception** — appears only when Status is "Risk Accepted / Excluded"; pick an existing Active exception or create one inline.

If your organization has segregation of duties turned on, you can't link an exception you approved yourself — pick a different exception, or ask a colleague to link it.

## Maintaining Targets and Access Groups

**Where**: **Targets** and **Access Groups** in the top nav (`/targets`, `/access-groups`).

- **Targets** — servers, databases, applications, or other endpoints an account's access leads to. Each has a Risk Score (0-1000) and one or more identifiers. Use **Bulk Actions** for CSV imports.
- **Access Groups** — privileged-access groups in the managed environment, each with a Base Risk Score and a derived Computed Risk Score. Use **Bulk Actions** here too.

## Reports you'll likely use

**Reports** in the top nav — Risk Score, KPI Summary, Stage/Status Summary, and Overdue/At-Risk.

## See also

- `User_Guide.md` — the full reference guide for every page.
- `Design Documents/Design_Risk_Scoring.md` — the model behind Targets, Access Groups, and computed risk scores.
