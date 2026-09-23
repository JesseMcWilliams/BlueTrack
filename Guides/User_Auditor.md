# User Guide: Auditor

**Blueprint Progress Tracking Web Interface**

## Who this is for

Someone whose BlueTrack role grants only `ViewAuditLog` — read-only visibility into the audit trail, with no editing rights anywhere in the app.

**A precise note on the Dashboard**: `ViewDashboard` is the permission that conceptually gates the Dashboard page, but it is not currently enforced by any controller policy — nothing in the API actually checks for it. In practice this means an Auditor role, despite not holding `ViewDashboard`, can still open the Dashboard page like any signed-in user. Don't assume Auditor is blocked from it; it isn't.

## Signing in

Use whichever identity provider your organization has configured — see your organization's own sign-in instructions if you're not sure which.

## The Audit Log Viewer

**Where**: **Admin > Audit Log Viewer** — this is the page the Auditor role exists for.

Read-only — no add/edit/delete anywhere on it. Filter by Event Type, Entity, and a From/To date range, submitted via an explicit **Filter** button rather than automatically as you type. The list shows Occurred At, Event Type, By, Entity, and Detail. Click the **Occurred At** value on any row to expand an inline panel showing the recorded Reason (if any) and a Field / Old Value / New Value table for that specific change.

## The Dashboard (also reachable, see note above)

**Where**: `Dashboard` in the top nav.

Four summary cards — Accounts by Stage, Key Progress Indicators, Overdue/At-Risk Accounts, and Risk Exceptions Needing Attention. Since an Auditor role doesn't hold `ApproveExceptions`, the Exceptions card shows only the general overdue-review count, not an approval-queue count. See `Design Documents/Design_User_Guide.md`'s Dashboard section for the full breakdown.

## Your profile

**Where**: the user menu (`/profile`). Shows your display name, mapped role, and permission list, and lets you pick a display theme.

## See also

- `Design Documents/Design_User_Guide.md` — the full reference guide, covering every page in the application.
- `Design Documents/Design_Audit_Logging.md` — what gets logged and why, for the data behind the Audit Log Viewer.
