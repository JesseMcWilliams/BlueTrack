# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-11-18 | Index on staged Self-Hosted Direct accounts: faster clean-up after each pull | Release notes only (no user-visible behavior change) |
| 2026-11-18 | A missed pull runs when the API starts within 12 hours of the pull time and before the Import ("schedule (catch-up)") | Admin_OperationsGuide.md "A Nightly Pull Didn't Run" (done); Admin_DataManagement.md (Self-Hosted pulls) |
| 2026-11-18 | `Get-BlueTrackResourceUsage.ps1`: checks IIS keeps the API loaded (start mode, idle time-out, preload, Application Initialization), the pool's IIS events (7 days), and lists API starts, pulls and Import and Load runs | Tools README (done); Admin_OperationsGuide.md "A Nightly Pull Didn't Run" (done) |
| 2026-11-18 | Operations Guide: new "A Nightly Pull Didn't Run" section (signs, why, catch up, API starts, IIS settings and events) | Done: Admin_OperationsGuide.md |
| 2026-11-18 | Nightly Import's "pull more than 20 hours old" error says no pull started since, whether the API is running now, and where to look | Admin_OperationsGuide.md (troubleshooting a missing nightly run) |
| 2026-11-18 | A missed nightly pull: Pull and Load health check Degraded with whether the API was running; new notification type SelfHostedPullMissed (off by default) | Admin_SettingsReference_Notifications.md (types); Admin_OperationsGuide.md (troubleshooting) |
| 2026-11-18 | Admin > Data Sources: next pull time, how long the API has been running, when the pull scheduler last checked; an alert when it has stopped checking | Admin_OperationsGuide.md (troubleshooting a missing nightly run) |
| 2026-11-18 | Admin > Deployment: **API starts** table (each start and stop of the API, version, the time it wasn't running before each start; a run that didn't stop cleanly is noted) | Admin_OperationsGuide.md (troubleshooting a missing nightly run) |
| 2026-11-12 | Account page, Who can use this account: User source column (Vault or LDAP) | User_Guide.md (account page, Who can use this account) |
| 2026-11-12 | Progress Rules: last run, each rule's Last applied, a rule's Applied to list; Global Application Configuration: Keep the Progress Rules log (days), 1-3650, default 90 | Admin_SettingsReference_Data.md (Progress Rules); Admin_SettingsReference.md (Global Application Configuration) |
| 2026-11-12 | Account Progress list: CPM Status and Managed by the CPM columns (sortable; failure highlighted) | User_Guide.md (Account Progress list columns) |
| 2026-11-12 | Safe Access Policies: Duplicate on each policy and Safe role (opens a filled-in new form; a policy copy starts disabled) | Admin_SettingsReference_Data.md (Safe Access Policies) |
| 2026-11-12 | Progress Rules and DNA tracking rules: a days condition saves; changing a condition's field to another kind clears its value | None (a fix; behaves as documented) |
| 2026-11-12 | Safe access check: Self-Hosted's Move accounts from + into equals Privilege Cloud's Move accounts and folders, both ways (roles hold and exact-match across sources) | Admin_SettingsReference_Data.md (Safe Access Policies: Safe roles) |
| 2026-11-12 | Reports > DNA Accounts and DNA Hard-Coded Credentials ask SQL Server for far less memory per page; a DNA import holds its rows in memory once instead of twice | Release notes only (no user-visible behavior change) |
| 2026-11-12 | `Get-BlueTrackResourceUsage.ps1` shows where the server's memory is, when it started, and warns when SQL Server is short of memory or its statement statistics cover under an hour | Tools README; Admin_OperationsGuide.md (the "Performance" note) |
| 2026-11-10 | DNA import reads the workbook a row at a time (less memory); audit sealing and SIEM forwarding every 10 s (was 5); the API runs the workstation garbage collector (less memory); new `Tools/Get-BlueTrackResourceUsage.ps1` | Admin_OperationsGuide.md (DNA import, SIEM forwarding; a "Performance" note pointing at the tool) |
| 2026-11-09 | The installer's smoke test no longer fails an install because a health check is Degraded (e.g. the latest Import failed); it warns and names it. Its "-Resume" hints show the full installer path and site. | Admin_UpgradeGuide.md section 4; Admin_Installation.md "Smoke"; Deploy/README.md "Smoke test failures" |
