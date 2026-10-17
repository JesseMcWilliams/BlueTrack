# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-10-15 | `Update-BlueTrack.ps1` no longer changes the download folder's permissions; it warns, and `-LockDownFolder` limits the folder unless it's already restricted (D-235). | `Admin_OperationsGuide.md` and `Admin_DeploymentRunbook.md` (Updating) |
| 2026-10-15 | Account inventory feeds take a user name plus address of up to 400 characters (was 99; D-236, script 27). | `Admin_SettingsReference.md` (Data Sources, feeds) |
| 2026-10-15 | Data feeds: **Skip group rows** on an account inventory feed (D-237, script 28). | `Admin_SettingsReference.md` (Data Sources, feeds); `Admin_DataManagement.md` if feeds are described there |
| 2026-10-15 | Account Progress edit: **Application** field; an account is covered by its own application's exceptions and its safe's (D-238, script 29). | `User_Guide.md` (editing an account), `User_GettingStarted.md`, `Admin_SettingsReference.md` (Application mapping) |
| 2026-10-15 | Bulk edit can set an Application; Account → Application Assignments CSV import on Application ↔ Safe Mapping > Bulk Actions (D-239). | `User_Guide.md` (done for both), `User_GettingStarted.md` (Bulk Actions line), `Admin_SettingsReference.md` |
| 2026-10-17 | Data feeds: new **Account → application assignments** feed type, the Account assignments CSV import on a schedule (D-241, script 30). | `Admin_SettingsReference.md` (done), `User_Guide.md` (done) |
| 2026-10-17 | The installer's Hosting Bundle check now requires ASP.NET Core runtime 10.0.13 or later and offers to install the current bundle (D-242). | `Admin_Installation.md` (done) |
| 2026-10-17 | API access for scripts (D-243, script 31): Admin > API Keys page, the API key default lifetime setting, keys exchanged for 10-minute tokens. Identity-provider tokens, expiry emails and a "calling the API from a script" guide are still to write. | `Admin_SettingsReference_Notifications.md` section 9 (done), `Admin_SettingsReference.md` (done), `Admin_Installation.md` (upgrade: script 31), a new script-access guide |
| 2026-10-17 | API keys: Rotate, the ApiKeyExpiring notification (30 and 7 days), the per-key rate limit setting (D-243, script 32). | `Admin_SettingsReference_Notifications.md` (done), `Admin_SettingsReference.md` (done) |
| 2026-10-17 | API access with an identity provider's token (D-243 phase 2, script 33): API client mappings on Admin > API Keys, `idToken` on the token endpoint. The "calling the API from a script" guide is still to write (both credentials, with examples). | `Admin_SettingsReference_Notifications.md` section 9 (done), a new script-access guide |
