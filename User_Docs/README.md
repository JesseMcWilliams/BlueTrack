# Guides

Short, linear, role-based walkthroughs — "what do I do first, then next" — as opposed to the field-by-field reference material in `Claude_Docs/`, which these guides link out to rather than duplicate.

These guides live in `User_Docs/` (they were in `Guides/` before; update any bookmark or link to the old path). The Word deliverables (the Installation, Development Host Setup, Getting Started and Administrator Settings guides, and the Release Notes) are in `Published_Docs/`. They're built from `Admin_Installation.md`, `Developer_HostSetup.md`, `User_GettingStarted.md`, `Admin_SettingsReference.md` and `Release_Notes.md` when a release is made (`python Published_Docs/build_published_docs.py`, needs python-docx), so between releases the guides here are the more current.

## New to BlueTrack? Start here

- [Release_Notes.md](Release_Notes.md) — what changed in each release, and what to do when upgrading. Published as Release Notes.

- [User_GettingStarted.md](User_GettingStarted.md) — what BlueTrack is for, the key terms, and the typical flow from importing data to working accounts through their stages, exceptions and reports. Published as the Getting Started Guide.

## Admin guides (read in order, for a new environment)

1. [Admin_Installation.md](Admin_Installation.md) — first-time setup, from prerequisites through first sign-in.
2. [Admin_Configuration.md](Admin_Configuration.md) — the day-2 settings to review before regular use begins.
3. [Admin_DataManagement.md](Admin_DataManagement.md) — the recurring operational tasks on a running environment.
4. [Admin_SettingsReference.md](Admin_SettingsReference.md) — every setting, its default, the recommended value and why. Published as the Administrator Settings Guide.

## User guides (by role)

Each covers one BlueTrack role in isolation. The roles are siblings built on a shared `ViewDashboard` base, not a strict ladder — check the permission list in each guide against your own role (**Profile & Preferences > Reload My Rights**, in the menu with your name, shows exactly what you hold).

- [User_Viewer.md](User_Viewer.md) — `ViewDashboard` only.
- [User_Analyst.md](User_Analyst.md) — `ViewDashboard` + `EditAccountProgress` + `ManageAccessGroups` + `ManageTargets`.
- [User_Approver.md](User_Approver.md) — `ViewDashboard` + `EditAccountProgress` + `ApproveExceptions` + `ConfirmReconciliation`.
- [User_Auditor.md](User_Auditor.md) — `ViewAuditLog` only.

## See also

- `User_Guide.md` — the full reference guide covering every page in the application, for every role at once.
- `Admin_OperationsGuide.md` — the DBA/operator-facing tasks with no UI of their own: backups and restore, updating to a new version, SQL Server grants, the nightly jobs.
- `Admin_DeploymentRunbook.md` — the step-by-step procedure for standing up or rebuilding an environment.
- [Developer_HostSetup.md](Developer_HostSetup.md) — setting up a machine to develop BlueTrack and run its tests.
- `Claude_Docs/Design_Application-Structure.md` — the authoritative page inventory and navigation conventions.
