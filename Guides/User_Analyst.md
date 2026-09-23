# User Guide: Analyst

**Blueprint Progress Tracking Web Interface**

## Who this is for

Someone whose BlueTrack role grants `ViewDashboard`, `EditAccountProgress`, `ManageAccessGroups`, and `ManageTargets` — the day-to-day workflow of tracking account progress and maintaining the risk-scoring inventory behind it. This role is a sibling of the Approver role ([User_Approver.md](User_Approver.md)), not a subset or superset of it — an Analyst can edit account progress but cannot approve Risk Exceptions or confirm reconciliation, while an Approver can do those two things but can't manage Targets or Access Groups.

## The Dashboard

**Where**: the home page after signing in (`Dashboard` in the top nav). Four summary cards — Accounts by Stage, Key Progress Indicators, Overdue/At-Risk Accounts, and Risk Exceptions Needing Attention — each linking to the full report behind it. See `Design Documents/Design_User_Guide.md`'s Dashboard section for the full breakdown.

## Working Account Progress

**Where**: **Accounts** in the top nav (`/accounts`).

This is the core of the Analyst role. Filter the list by Stage, Status, Risk Level, or Owner; click an account's Username to open it. Opening an account you hold `EditAccountProgress` for tries to acquire an edit lock automatically — if someone else has it locked, you'll see who and can **Force Release Lock** if you need to take over.

The edit form has up to three tabs:
- **Details** — stage, status, risk level, owner, business unit, dates, notes. A Reason is required only when regressing an account to an earlier stage than it's currently at.
- **Risk Score** — read-only computed values, a **Recalculate** button, and an editable Override Score.
- **Risk Exception** — appears only when Status is "Risk Accepted / Excluded"; pick an existing Active exception or create one inline.

Note: linking a Risk Exception here may be blocked if your organization has turned on segregation-of-duties enforcement and you're the same person who approved that exception — that's expected, not a bug; pick a different exception or ask someone else to link it.

## Maintaining Targets and Access Groups

**Where**: **Targets** and **Access Groups** in the top nav (`/targets`, `/access-groups`) — your `ManageTargets`/`ManageAccessGroups` permissions gate these.

These two pages are the inventory behind every account's computed risk score:
- **Targets** — servers, databases, applications, or other endpoints an account's access leads to. Each carries a Risk Score (0-1000) and one or more identifiers. Use **Bulk Actions** for CSV imports of the Target inventory or the Account → Target map.
- **Access Groups** — privileged-access groups in the managed environment (e.g. an AD group), each with a Base Risk Score and a derived Computed Risk Score. Use **Bulk Actions** here for the group inventory, its reachable Targets, and its member Accounts.

See `Design Documents/Design_Risk_Scoring.md` for the full model behind how these feed into an account's effective risk score.

## Reports you'll likely use

**Reports** in the top nav — Risk Score (if you hold `ViewRiskReport`), KPI Summary, Stage/Status Summary, and Overdue/At-Risk are the ones most relevant to this role's daily work.

## See also

- `Design Documents/Design_User_Guide.md` — the full reference guide for every page.
- `Design Documents/Design_Risk_Scoring.md` / `Design_Risk_Scoring_Import.md` — the data model and algorithm behind Targets, Access Groups, and computed risk scores.
