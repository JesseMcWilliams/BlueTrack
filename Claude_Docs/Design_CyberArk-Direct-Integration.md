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

**Setting it up (D-249).** Edit CyberArk sources has **Create a credential** (opens Admin > Credentials with a new CyberArk CP credential and comes back with it chosen; unsaved values are kept for an hour), and every credential has **Use for Self-Hosted Direct**. A CyberArk CP or CCP credential with a blank folder uses `Root`.

**Seeing a problem (D-250).** A failed or rejected pull shows as a banner on Data Sources, as a warning on the admin menu's Data Sources entry, and, once the `SelfHostedPullFailed` notification type is switched on (it starts disabled) and has recipients, as an email when it happens rather than at the 02:00 import. A rejected pull's message names the Safes that are no longer returned (and new ones), by number against the Safes the last good pull staged, and the log shows before and after counts.

**Credential checks (D-250).** The credential list on the sources page says whether a CP or CCP credential's backend has its settings (CP: an AppID; CCP: a base URL and an AppID) and links to Admin > Secrets Store when it doesn't; Test says so first. Admin > Credentials shows what uses each credential and won't delete one in use; **Cancel and return to CyberArk sources** appears when it was opened from there.

**Pull details.** Each pull in the Data Sources log has a **Details** button: a modal with the outcome, who started it and how long it took, every message on its own line, all the counts, what the outcome means and what to do about it.

**Pull now** (Admin > Data Sources, `ManageDataSources`; the EVD and the folder still have no Run now) starts a pull in the background and the page polls the log. After a **Rejected** pull the page offers **Accept the change and pull**, which pulls again and accepts whatever PVWA returns, after a confirmation (always, not only in business hours). Both are audit-logged. The nightly pull skips quietly if one is running.

## Settings (`web.cyberark_source_config`, script 38)

| Setting | Default | Notes |
|---|---|---|
| `SelfHostedSource` | `Evd` | `Evd` or `Direct`. The source not in use keeps its values when they are valid (D-270), so switching back needs nothing typed again; with Self-Hosted off, both are cleared. |
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
| `Safes/{safeUrlId}/Members?filter=includePredefinedUsers eq true` | `stg_sh_owners` | The 22 named permissions map to the EVD's `CAO*` columns (table below). Predefined members (Administrator, Auditors and the like) are read for `stg_shd_safe_members` only; `stg_sh_owners` leaves them out, as it always did. |
| `Users` and `Users/{id}` | `stg_sh_users` | `userType` maps to the EVD's `CAUUserTypeID` (Built-InAdmins 10, ENE 11, CPM 31, PVWA 32, AppProvider 33, EPVUser 34, AIMAccount 35, PSM 36, DRUser 504; any other stays NULL). `source` CyberArk is Internal (1), anything else External (2). Email from the user's details, asked only for people (built-in and component users have none, and Master's details call returns a 500). The plain list, not `ExtendedDetails=true`, which leaves out Master while its Total counts it (D-249); a Total that still exceeds the list is a warning. |
| `UserGroups?includeMembers=true` | `stg_sh_groups`, `stg_sh_groupmembers` | A member whose id is a group's is a nested group (`MemberIsGroup = YES`); users and groups share one id space. |
| `Platforms` | `stg_sh_platforms` (new) | Name, description, active, type. `usp_Load_DimPlatform` names and describes the Self-Hosted platforms from it and adds platforms no account uses. |

Locations: PVWA gives a path, the EVD a number. A known location name keeps its number from `dim_location`; a new one takes the next free number.

**Permission mapping** (compared with the EVD for 88 Safe members; all agreed): `listAccounts` List, `retrieveAccounts` Retrieve, `addAccounts` CreateObject, `updateAccountContent` UpdateObject, `updateAccountProperties` UpdateObjectProperties, `renameAccounts` RenameObject, `deleteAccounts` Delete, `viewAuditLog` ViewAudit, `viewSafeMembers` ViewOwners, `useAccounts` UsePassword, `initiateCPMAccountManagementOperations` InitiateCPMChange, `specifyNextAccountContent` InitiateCPMChangeWithManualPassword, `createFolders` CreateFolder, `deleteFolders` DeleteFolder, `unlockAccounts` UnlockObject, `moveAccountsAndFolders` MoveFrom and MoveInto, `manageSafe` ManageSafe, `manageSafeMembers` ManageSafeOwners, `backupSafe` Backup, `accessWithoutConfirmation` NoConfirmRequired, `requestsAuthorizationLevel1` Confirm. Left NULL (no REST counterpart): ValidateSafeContent, EventsList, EventsAdd.

**Not read:** discovered (pending) accounts (a separate pool that can be cleared in bulk, so it would distort the change guard); requests and confirmations (dual-control workflow; the tables are emptied); `DeviceType`, `CreationMethod` and the linked logon and reconcile account properties, which REST doesn't return; dependent accounts (14.6 and later only); users' addresses and phone numbers (the user's choice, D-270).

### The `stg_shd_*` tables (D-270, script 41)

The same pull is also written in REST's own shape, in the same transaction and under the same `ImportBatchId`, to keep everything REST returns, including what the EVD-shaped tables have no column for. Column names are the REST field names in PascalCase; times are UTC `DATETIME2`. **Nothing reads them yet**: the Load reads `stg_sh_*`, so its output is unchanged, and an EVD import leaves them alone. They are the first step of `Planning_Self-Hosted-Direct-And-EVD-Retirement.md`.

| Table | From |
|---|---|
| `stg_shd_platforms`, `stg_shd_platform_properties` | `Platforms` (general, credential, session and workflow policy; required and optional properties, linked account types) and `Platforms/Targets` (reason for access, verify and change on add, reset mode). A refused `Targets` call is a warning. |
| `stg_shd_safes` | `Safes?extendedDetails=true`, every field (`lastModificationTime` is sent in microseconds). |
| `stg_shd_accounts`, `stg_shd_account_properties` | `Accounts`: `secretType`, `categoryModificationTime`, all of `secretManagement` (`status` as sent, `lastReconciledTime`), `remoteMachinesAccess`; every platform property, with no length limit on the name. |
| `stg_shd_account_cpm_activity` | The classic `WebServices/PIMServices.svc/Accounts/{id}/Activities/` call, only for accounts with a `secretManagement.status` (the CPM has acted on them), 8 at a time. Kept: the latest of each CPM code (22 verify, 38 verify failure, 24 change, 57 change failure, 31 reconcile, 60 reconcile failure, 414/415/419 SSH key). A failed call is a warning. |
| `stg_shd_safe_members` | Safe members including predefined ones, with `isPredefinedUser`, `isReadOnly` and a `BIT` per permission named as in `stg_pc_entitlements`. |
| `stg_shd_users`, `stg_shd_user_authorizations` | The user list and each person's details: name parts, organization, department, title, emails, DN, description, expiry, last logon, password flags (`DetailsRead` says whether details were read); Vault authorizations, unauthorized interfaces and authentication methods. |
| `stg_shd_groups`, `stg_shd_group_members` | `UserGroups?includeMembers=true`, with `directory` and `dn`, and members' names. |

## Lab comparison (2026-10-19, same Vault read through REST and the EVD)

Users 43 of 43 matched on id, name, type, source, names, location and enabled flag; groups 21 of 21; Safe members 88 of 88; 2,451 of 2,452 account names, with `UserName`, `Address`, `PolicyID` and `CPMDisabled` identical and timestamps identical (both UTC). Differences:

1. **Visibility.** The service user saw 40 of 57 Safes and 43 of 51 users: 17 internal Vault Safes (System, PVWA\*, PasswordManager\*, PSM\*, AppProviderConf and others), plus `Test_Radius_00`, were hidden; two Safes created after the EVD copy were visible. Internal Safes are not business data; the change guard compares counts with the last pull, never with a fixed number.
2. **Retention.** The EVD reports `CASYearlyVersions` 0 and a days value of 7 even for Safes that retain by versions. REST reports the real mode: versions or days, the other null. Direct therefore shows the real retention; the EVD's is a default.
3. **Properties missing from REST.** `LogonDomain` for two PSM accounts, and the properties listed under "Not read". The docs say internal properties aren't returned.
4. **Email** needs one call per user (the list has none).
5. **Times.** Account and Safe creation times are epoch seconds; a Safe's `lastModificationTime` is epoch microseconds.
6. **CPM fields** (checked 2026-10-20 on the lab's PVWA 14.6.0, after the CPM changed, verified and reconciled four accounts): `status` comes back only for accounts the CPM has acted on, in lowercase (`success`, `failure`; the docs spell it three other ways); `lastReconciledTime` comes back; **`lastVerifiedTime` never does**, even minutes after a successful verify. The verify time is in the account's activity (code 22). The classic activity call's times are text; they matched the epoch times of the same events in UTC.

## Constraints

- PVWA returns only what the service user can see. It needs **List Accounts** and **View Safe Members** in each Safe to read, and the **Audit Users** Vault authorization for the user list ("users on the same level or lower" in the hierarchy). For the `stg_shd_*` extras (D-270), per the user (2026-10-20): membership of the Vault's **Auditors** group for Safes and accounts (the CPM activity call), and of **Vault Admins** for platform details (`Platforms/Targets`); the lab's service user is in both. Without them the pull warns and goes on. A user that isn't a member of a business Safe shows as an accounts drop: the change guard stops it, and an admin decides.
- A pull that returns **no Safes** is a failure, not a change.
- Accounts the pull no longer returns are marked deleted at the next load, as after an EVD import.
- The pull needs PAM Self-Hosted **14.2 or later** (the 2nd gen REST API).
- Run time was about 8 seconds for 40 Safes and 2,452 accounts; it grows with Safes (one call each) and users (one call each).

## Where the code is

`App/Api/CyberArkDirect/`: `PvwaClient` (REST), `PvwaModels`, `SelfHostedStagingMapper` (PVWA data to `stg_sh_*` and `stg_shd_*` rows), `PullChangeGuard`, `SelfHostedDirectRepository` (pull log, staging write), `SelfHostedDirectPullService`, `SelfHostedDirectRunner` and `SelfHostedDirectBackgroundService`. The settings, Test, pull log and Run now are in `CyberArkSourcesController` (`/api/admin/cyberark-sources`, `ManageDataSources`). Test (on unsaved values) fetches the credential, logs on, counts what it can see and compares the counts with the last pull, writing nothing. The page is `CyberArkSourcesEdit.vue` and `DataSources.vue`. Tests: `PvwaClientTests`, `SelfHostedStagingMapperTests`, `PullChangeGuardTests`, `CyberArkSourceValidationDirectTests` (unit), `SelfHostedDirectTests` (integration: the SQL gate, the staging write, the pull log), `CyberArkSourcesControllerTests` (contract) and the two page tests.

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
| Extras | Platforms, Safe details, Safe members, user details (email) and group members, with nested groups. Linked accounts were asked for too, and REST doesn't return them. Since D-270 everything REST returns is kept in `stg_shd_*` (above). |
