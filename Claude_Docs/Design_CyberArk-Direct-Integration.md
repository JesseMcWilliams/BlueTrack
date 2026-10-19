# BlueTrack: CyberArk Direct Integration

> **Stage: Design.** How Self-Hosted Direct works: the API reads a Self-Hosted PVWA's REST API and fills the same staging tables the EVD import fills. Requested 2026-10-19 and built the same day (D-248, script 38). Privilege Cloud Direct is planned (`Planning_Backlog.md`). Facts about PVWA's REST API were checked against the lab PVWA on 2026-10-19 with the Idira reference docs (PAM Self-Hosted 14.2).

## What it does

Each CyberArk platform has a **source** chosen on Admin > Data Sources > Edit CyberArk sources:

| Option | How data arrives | Status |
|---|---|---|
| Self-Hosted: EVD database | The nightly job's Import step copies the EVD database into `stg_sh_*` (`usp_Import_SelfHosted_EVD`). | existing |
| **Self-Hosted Direct** | The API reads PVWA's REST API into `stg_sh_*` each night at the **pull time** (default 01:30), before the Import job. | **built (D-248)** |
| Privilege Cloud: export folder | The Import step bulk-loads the seven CSV exports (`usp_Import_PC_*`). | existing |
| Privilege Cloud Direct | The API would read Privilege Cloud's API into `stg_pc_*`. | planned, shown as unavailable |

The Load (`usp_RunFullLoad`) and everything after it are unchanged: they read `stg_sh_*` whichever way it was filled. A new platform or source is added by writing a provider that fills that platform's staging tables.

## How a Self-Hosted Direct night works

1. **01:30 (the pull time):** `SelfHostedDirectBackgroundService` (wakes every minute, re-reads the setting, so a change applies without a restart) calls `SelfHostedDirectRunner`, which runs `SelfHostedDirectPullService`.
2. The service fetches the **named credential** (`web.credential`, D-116) at that moment: a CyberArk **CP** or CCP credential is read from the Vault (`CredentialRepository.ResolveForUseAsync`), so no PVWA password is stored in BlueTrack. It logs on with `POST /PasswordVault/API/auth/{CyberArk|LDAP}/Logon/`, reads everything, and logs off.
3. The **change guard** (`PullChangeGuard`) compares the Safe and account counts with the last *successful* pull. A move of more than the allowed percentage (default 20%, either direction, never less than 5 items) is **rejected**: nothing is written, and the pull is logged as Rejected.
4. Otherwise `SelfHostedDirectRepository.StageAsync` replaces the `stg_sh_*` tables in **one transaction** (`DELETE`, then `SqlBulkCopy`; the app's database account has no ALTER, so no `TRUNCATE`). A failure rolls back and leaves the previous staging.
5. **02:00 (the Import job):** `usp_Import_Configured` sees `SelfHostedSource = 'Direct'` and, instead of reading an EVD, checks the **latest** row of `dbo.cyberark_direct_pull`. It must be Succeeded and less than 20 hours old. A Failed, Rejected, never-finished or stale pull **fails the night**: the Load doesn't run, and `NightlyImportFailed` mails the reason (`Design_Notifications.md`). `usp_Import_All` takes `@SelfHostedStaged = 1`, so the Import leaves the pulled staging alone instead of clearing it.
6. The Load runs as after any import.

**Pull now** (Admin > Data Sources, `ManageDataSources`; the EVD and the folder still have no Run now) starts a pull in the background and the page polls the log. After a **Rejected** pull the page offers **Accept the change and pull**, which pulls again and accepts whatever PVWA returns, after a confirmation (always, not only in business hours). Both are audit-logged. The nightly pull skips quietly if one is running.

## Settings (`web.cyberark_source_config`, script 38)

| Setting | Default | Notes |
|---|---|---|
| `SelfHostedSource` | `Evd` | `Evd` or `Direct`. The EVD database name is kept only for `Evd`; the PVWA values only for `Direct`. |
| `PvwaBaseUrl` | none | `https://host`, with or without `/PasswordVault`. https only: the credential goes over the connection. |
| `PvwaCredentialKey` | none | A `web.credential` row. The Credentials page needs its own permission, so Data Sources has its own names-only list (`GET .../credentials`). |
| `PvwaLogonMethod` | `CyberArk` | `CyberArk` or `LDAP`; both take a user name and password. Windows and SAML logon aren't supported. |
| `DirectPullTime` | `01:30` | `HH:mm`, server time. Must come before the Import job. |
| `DirectMaxChangePercent` | 20 | 0 to 100. |

`dbo.cyberark_direct_pull` logs each pull (start, end, outcome, who started it, whether a change was accepted, counts, message). Rows older than 30 days are purged after the nightly pull; the latest is always kept. A pull still "Started" after two hours (the app stopped) is marked Failed by the next one.

## What it reads, and what each becomes

All calls are `GET` under `/PasswordVault/API/` except logon and logoff. Paging is `limit=1000&offset=N` until a short page. Safe members are read one Safe at a time (4 at a time); user details one user at a time (8 at a time) because the user list has no email.

| PVWA | Staging | Notes |
|---|---|---|
| `Safes?extendedDetails=true` | `stg_sh_safes` | `safeNumber` is the EVD's `CASSafeID`. `numberOfVersionsRetention` and `numberOfDaysRetention` go to `CASYearlyVersions` and `CASObjectsRetentionPeriod`, the columns the Load reads. |
| `Accounts` | `stg_sh_files` (type 2) and `stg_sh_objectproperties` | Account id `59_3` is Safe 59, file 3: the EVD's `CAFSafeID` and `CAFFileID`, so `SourceAccountId` is identical either way. `UserName`, `Address`, `PolicyID` (the `platformId`), each platform account property, and `CPMDisabled` (the reason, or "No reason given", when automatic management is off). A property name longer than 29 characters is left out and reported. |
| `Safes/{safeUrlId}/Members` | `stg_sh_owners` | The 22 named permissions map to the EVD's `CAO*` columns (table below). |
| `Users?ExtendedDetails=true` and `Users/{id}` | `stg_sh_users` | `userType` maps to the EVD's `CAUUserTypeID` (Built-InAdmins 10, ENE 11, CPM 31, PVWA 32, AppProvider 33, EPVUser 34, AIMAccount 35, PSM 36, DRUser 504; any other stays NULL). `source` CyberArk is Internal (1), anything else External (2). Email from the user's details. |
| `UserGroups?includeMembers=true` | `stg_sh_groups`, `stg_sh_groupmembers` | A member whose id is a group's is a nested group (`MemberIsGroup = YES`); users and groups share one id space. |
| `Platforms` | `stg_sh_platforms` (new) | Name, description, active, type. `usp_Load_DimPlatform` names and describes the Self-Hosted platforms from it and adds platforms no account uses. |

Locations: PVWA gives a path, the EVD a number. A known location name keeps its number from `dim_location`; a new one takes the next free number.

**Permission mapping** (compared with the EVD for 88 Safe members; all agreed): `listAccounts` List, `retrieveAccounts` Retrieve, `addAccounts` CreateObject, `updateAccountContent` UpdateObject, `updateAccountProperties` UpdateObjectProperties, `renameAccounts` RenameObject, `deleteAccounts` Delete, `viewAuditLog` ViewAudit, `viewSafeMembers` ViewOwners, `useAccounts` UsePassword, `initiateCPMAccountManagementOperations` InitiateCPMChange, `specifyNextAccountContent` InitiateCPMChangeWithManualPassword, `createFolders` CreateFolder, `deleteFolders` DeleteFolder, `unlockAccounts` UnlockObject, `moveAccountsAndFolders` MoveFrom and MoveInto, `manageSafe` ManageSafe, `manageSafeMembers` ManageSafeOwners, `backupSafe` Backup, `accessWithoutConfirmation` NoConfirmRequired, `requestsAuthorizationLevel1` Confirm. Left NULL (no REST counterpart): ValidateSafeContent, EventsList, EventsAdd.

**Not read:** discovered (pending) accounts (a separate pool that can be cleared in bulk, so it would distort the change guard); requests and confirmations (dual-control workflow; the tables are emptied); `LastVerified`, `LastReconciled`, `RemoteMachines`, `DeviceType`, `CreationMethod` and the linked logon and reconcile account properties, which this PVWA's REST API didn't return (see below).

## Lab comparison (2026-10-19, same Vault read through REST and the EVD)

Users 43 of 43 matched on id, name, type, source, names, location and enabled flag; groups 21 of 21; Safe members 88 of 88; 2,451 of 2,452 account names, with `UserName`, `Address`, `PolicyID` and `CPMDisabled` identical and timestamps identical (both UTC). Differences:

1. **Visibility.** The service user saw 40 of 57 Safes and 43 of 51 users: 17 internal Vault Safes (System, PVWA\*, PasswordManager\*, PSM\*, AppProviderConf and others), plus `Test_Radius_00`, were hidden; two Safes created after the EVD copy were visible. Internal Safes are not business data; the change guard compares counts with the last pull, never with a fixed number.
2. **Retention.** The EVD reports `CASYearlyVersions` 0 and a days value of 7 even for Safes that retain by versions. REST reports the real mode: versions or days, the other null. Direct therefore shows the real retention; the EVD's is a default.
3. **Properties missing from REST.** `LogonDomain` for two PSM accounts, and the properties listed under "Not read". The docs say internal properties aren't returned. Whether `LastVerified` and `LastReconciled` appear for accounts the CPM has processed is **unverified** (the lab's accounts hadn't been).
4. **Email** needs one call per user (the list has none).
5. **Times.** Account and Safe creation times are epoch seconds; a Safe's `lastModificationTime` is epoch microseconds (not read).

## Constraints

- PVWA returns only what the service user can see. It needs **List Accounts** and **View Safe Members** in each Safe to read, and the **Audit Users** Vault authorization for the user list ("users on the same level or lower" in the hierarchy). A user that isn't a member of a business Safe shows as an accounts drop: the change guard stops it, and an admin decides.
- A pull that returns **no Safes** is a failure, not a change.
- Accounts the pull no longer returns are marked deleted at the next load, as after an EVD import.
- The pull needs PAM Self-Hosted **14.2 or later** (the 2nd gen REST API).
- Run time was about 8 seconds for 40 Safes and 2,452 accounts; it grows with Safes (one call each) and users (one call each).

## Where the code is

`App/Api/CyberArkDirect/`: `PvwaClient` (REST), `PvwaModels`, `SelfHostedStagingMapper` (PVWA data to staging rows), `PullChangeGuard`, `SelfHostedDirectRepository` (pull log, staging write), `SelfHostedDirectPullService`, `SelfHostedDirectRunner` and `SelfHostedDirectBackgroundService`. The settings, Test, pull log and Run now are in `CyberArkSourcesController` (`/api/admin/cyberark-sources`, `ManageDataSources`). Test (on unsaved values) fetches the credential, logs on, counts what it can see and compares the counts with the last pull, writing nothing. The page is `CyberArkSourcesEdit.vue` and `DataSources.vue`. Tests: `PvwaClientTests`, `SelfHostedStagingMapperTests`, `PullChangeGuardTests`, `CyberArkSourceValidationDirectTests` (unit), `SelfHostedDirectTests` (integration: the SQL gate, the staging write, the pull log), `CyberArkSourcesControllerTests` (contract) and the two page tests.

## Decisions made on 2026-10-19

| Question | Answer |
|---|---|
| Scope | Self-Hosted first; Privilege Cloud Direct later behind the same idea. |
| Versions | 14.2 and later. |
| Relation to the EVD | Replaces it when chosen; the EVD stays an option. |
| Credentials | A named credential, so CP or CCP. CyberArk logon (LDAP too). |
| Where it runs | In the API, ahead of the nightly job. |
| Change threshold | A setting (percent); a Safe disappearing may be a deletion, so only counts are compared. Pending accounts are not counted or read. |
| A tripped guard | Fails the night (mail), until an admin accepts the change. |
| Extras | Platforms, Safe details, Safe members, user details (email) and group members, with nested groups. Last verified, last reconciled, remote machines and linked accounts were asked for too, and this PVWA didn't return them. |
