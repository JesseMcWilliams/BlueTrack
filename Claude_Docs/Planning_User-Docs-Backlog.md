# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-10-13 | D-210: Test buttons audit-logged (`ConnectionTest`), Secrets Store test no longer shows password length; SAML unreadable SP key gives a clear 503 and a Degraded health check. Already written into `User_Docs/`; rebuild the `.docx` guides at the next release. | `Admin_SettingsReference.md` (Secrets Store, Health checks), `Admin_Installation.md` (Certificates) |
| 2026-10-13 | D-211: CyberArk sources (export folder, file names, EVD database) edited on Admin > Data Sources; installer answers only seed them; upgrade needs `-Step Db.NightlyJob`. Written into `User_Docs/`; rebuild the `.docx` guides at the next release. | `Admin_SettingsReference.md` and `Admin_Configuration.md` (Data Sources), `Admin_Installation.md`, `Admin_OperationsGuide.md`, `Admin_DeploymentRunbook.md` |
| 2026-10-13 | D-212: Account inventory data feeds (accounts from other systems at Discovered, with source systems added on the feed page). Written into `User_Docs/`; rebuild the `.docx` guides at the next release. | `Admin_SettingsReference.md` (Data Sources (feeds)), `Admin_Configuration.md` (8. Data Sources) |
| 2026-10-13 | D-212 (4b): feed accounts linked to CyberArk accounts (moved to Onboarded, hidden, **Linked feed accounts** filter, "from <source>" mark). Written into `User_Docs/`. | `User_Guide.md` (Accounts), `Admin_SettingsReference.md` (Data Sources (feeds)) |
