# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-11-10 | DNA import reads the workbook a row at a time (less memory); audit sealing and SIEM forwarding every 10 s (was 5); new `Tools/Get-BlueTrackResourceUsage.ps1` | Admin_OperationsGuide.md (DNA import, SIEM forwarding; a "Performance" note pointing at the tool) |
| 2026-11-09 | The installer's smoke test no longer fails an install because a health check is Degraded (e.g. the latest Import failed); it warns and names it. Its "-Resume" hints show the full installer path and site. | Admin_UpgradeGuide.md section 4; Admin_Installation.md "Smoke"; Deploy/README.md "Smoke test failures" |
