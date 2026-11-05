# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-11-04 | D-332: the installer installs IIS Application Initialization and keeps the API running (no idle time-out, preload), so the nightly jobs run on a quiet server | Admin_Installation (prerequisites, covered), Admin_OperationsGuide (checking the night's runs) |
| 2026-11-04 | D-333: Via AD group names the nested groups in between | User_Guide, Who can use this account (covered) |
| 2026-11-04 | D-334: application owners' review emails and their two options | Admin_SettingsReference_Notifications (covered); User_Guide, Application ↔ Safe Mapping (covered) |
| 2026-11-04 | D-335: Reports > Safe Administrators | User_Guide, Reports (covered), and the Reports groups list under Risk and access (covered) |
| 2026-11-04 | D-336: Progress against target dates on Trends | User_Guide, Reports > Trends (covered); User_GettingStarted's Reports table if it describes Trends' charts |
| 2026-11-05 | D-337: Data Sources shows the step a running pull is in (with its count), Pull details lists a pull's steps and a stopped pull's message names its step; a new **Import and Load** section lists the nightly job's runs, the Load's procedures and, for a running one, what its SQL session waits on and who blocks it; installer step `Db.SessionActivity` | Admin_DataManagement (Data Sources), Admin_OperationsGuide (troubleshooting a slow or stuck pull or load), Admin_Installation and Admin_UpgradeGuide (the new step; needs a sysadmin) |
| 2026-11-05 | D-339: Who can use this account lists each Safe-member group (rights, member count, opens to its members; says why when its AD members weren't read) | User_Guide, Who can use this account |
| 2026-11-05 | The account's folder beside its Safe, "(not read yet)" for a Self-Hosted account | User_Guide, the account page |
| 2026-11-05 | D-338: Source picked from a list on Progress Rules, alphabetical field list, conditions ignore case; drop-down lists alphabetical | Admin_SettingsReference (Progress Rules) |
| 2026-11-05 | D-340: rerunning Update-BlueTrack.ps1 reuses the download; the installer saves PowerBiReaderAccount | Admin_UpgradeGuide (running an update again), Admin_Installation (unattended answers) |
