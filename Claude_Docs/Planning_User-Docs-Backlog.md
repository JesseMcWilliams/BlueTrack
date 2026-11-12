# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-11-12 | Progress Rules and DNA tracking rules: a days condition saves; changing a condition's field to another kind clears its value | None (a fix; behaves as documented) |
| 2026-11-12 | Safe access check: Self-Hosted's Move accounts from + into equals Privilege Cloud's Move accounts and folders, both ways (roles hold and exact-match across sources) | Admin_SettingsReference_Data.md (Safe Access Policies: Safe roles) |
| 2026-11-12 | Reports > DNA Accounts and DNA Hard-Coded Credentials ask SQL Server for far less memory per page; a DNA import holds its rows in memory once instead of twice | Release notes only (no user-visible behavior change) |
| 2026-11-12 | `Get-BlueTrackResourceUsage.ps1` shows where the server's memory is, when it started, and warns when SQL Server is short of memory or its statement statistics cover under an hour | Tools README; Admin_OperationsGuide.md (the "Performance" note) |
| 2026-11-10 | DNA import reads the workbook a row at a time (less memory); audit sealing and SIEM forwarding every 10 s (was 5); the API runs the workstation garbage collector (less memory); new `Tools/Get-BlueTrackResourceUsage.ps1` | Admin_OperationsGuide.md (DNA import, SIEM forwarding; a "Performance" note pointing at the tool) |
| 2026-11-09 | The installer's smoke test no longer fails an install because a health check is Degraded (e.g. the latest Import failed); it warns and names it. Its "-Resume" hints show the full installer path and site. | Admin_UpgradeGuide.md section 4; Admin_Installation.md "Smoke"; Deploy/README.md "Smoke test failures" |
