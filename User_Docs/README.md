# Guides

Short, linear, role-based walkthroughs — "what do I do first, then next" — as opposed to the field-by-field reference material in `Claude_Docs/`, which these guides link out to rather than duplicate.

## Admin guides (read in order, for a new environment)

1. [Admin_Installation.md](Admin_Installation.md) — first-time setup, from prerequisites through first sign-in.
2. [Admin_Configuration.md](Admin_Configuration.md) — the day-2 settings to review before regular use begins.
3. [Admin_DataManagement.md](Admin_DataManagement.md) — the recurring operational tasks on a running environment.

## User guides (by role)

Each covers one BlueTrack role in isolation. The roles are siblings built on a shared `ViewDashboard` base, not a strict ladder — check the permission list in each guide against your own role (**your profile menu > Reload My Rights** shows exactly what you hold).

- [User_Viewer.md](User_Viewer.md) — `ViewDashboard` only.
- [User_Analyst.md](User_Analyst.md) — `ViewDashboard` + `EditAccountProgress` + `ManageAccessGroups` + `ManageTargets`.
- [User_Approver.md](User_Approver.md) — `ViewDashboard` + `EditAccountProgress` + `ApproveExceptions` + `ConfirmReconciliation`.
- [User_Auditor.md](User_Auditor.md) — `ViewAuditLog` only.

## See also

- `User_Guide.md` — the full reference guide covering every page in the application, for every role at once.
- `Admin_OperationsGuide.md` — the DBA/operator-facing tasks with no UI of their own.
- `Admin_DeploymentRunbook.md` — the step-by-step procedure for standing up or rebuilding an environment.
- `Claude_Docs/Design_Application-Structure.md` — the authoritative page inventory and navigation conventions.
