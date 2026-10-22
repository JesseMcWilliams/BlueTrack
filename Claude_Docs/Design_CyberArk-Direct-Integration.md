# BlueTrack: CyberArk Direct Integration

> **Stage: Design.** How Self-Hosted Direct works: the API reads a Self-Hosted PVWA's REST API and fills the staging tables the Load reads. Requested 2026-10-19 and built the same day (D-248, script 38); since D-278 (script 47) it is the only Self-Hosted source, the EVD having been removed. Privilege Cloud Direct is planned (`Planning_Backlog.md`). Facts about PVWA's REST API were checked against the lab PVWA on 2026-10-19 with the Idira reference docs (PAM Self-Hosted 14.2).

## What it does

Each CyberArk platform has a **source** chosen on Admin > Data Sources > Edit CyberArk sources:

| Option | How data arrives | Status |
|---|---|---|
| Self-Hosted: EVD database | The nightly job's Import step copied the EVD database into `stg_sh_*` (`usp_Import_SelfHosted_EVD`). | **removed (D-278)** |
| **Self-Hosted Direct** | The API reads PVWA's REST API into `stg_sh_*` each night at the **pull time** (default 01:30), before the Import job. | **built (D-248)** |
| Privilege Cloud: export folder | The Import step bulk-loads the seven CSV exports (`usp_Import_PC_*`). | existing |
| Privilege Cloud Direct | The API would read Privilege Cloud's API into `stg_pc_*`. | planned, shown as unavailable |

The Load (`usp_RunFullLoad`) and everything after it are unchanged: they read `stg_sh_*` whichever way it was filled. A new platform or source is added by writing a provider that fills that platform's staging tables.

## How a Self-Hosted Direct night works

1. **01:30 (the pull time):** `SelfHostedDirectBackgroundService` (wakes every minute, re-reads the setting, so a change applies without a restart) calls `SelfHostedDirectRunner`, which runs `SelfHostedDirectPullService`.
2. The service fetches the **named credential** (`web.credential`, D-116) at that moment: a CyberArk **CP** or CCP credential is read from the Vault (`CredentialRepository.ResolveForUseAsync`), so no PVWA password is stored in BlueTrack. It logs on with `POST /PasswordVault/API/auth/{CyberArk|LDAP}/Logon/`, reads everything, and logs off.
   With a nightly or Pull now pull (not Test) it also reads, with the same session, the classic account details of new or changed accounts (D-280) and the CyberArk Applications (D-282); see "Beyond REST" below.
3. The **change guard** (`PullChangeGuard`) compares the Safe and account counts with the last *successful* pull. A move of more than the allowed percentage (default 20%, either direction, never less than 5 items) is **rejected**: nothing is written, and the pull is logged as Rejected.
4. Otherwise the pull reads from AD the users of each group that is a Safe member (D-281), then `SelfHostedDirectRepository.StageAsync` replaces the `stg_sh_*` tables in **one transaction** (`DELETE`, then `SqlBulkCopy`; the app's database account has no ALTER, so no `TRUNCATE`). A failure rolls back and leaves the previous staging.
5. **02:00 (the Import job):** `usp_Import_Configured`, with Self-Hosted on, checks the **latest** row of `dbo.cyberark_direct_pull`. It must be Succeeded and less than 20 hours old. A Failed, Rejected, never-finished or stale pull **fails the night**: the Load doesn't run, and `NightlyImportFailed` mails the reason (`Design_Notifications.md`). `usp_Import_All` takes `@SelfHostedStaged = 1`, so the Import leaves the pulled staging alone instead of clearing it.
6. The Load runs as after any import.

**Setting it up (D-249).** Edit CyberArk sources has **Create a credential** (opens Admin > Credentials with a new CyberArk CP credential and comes back with it chosen; unsaved values are kept for an hour), and every credential has **Use for Self-Hosted Direct**. A CyberArk CP or CCP credential with a blank folder uses `Root`.

**Seeing a problem (D-250).** A failed or rejected pull shows as a banner on Data Sources, as a warning on the admin menu's Data Sources entry, and, once the `SelfHostedPullFailed` notification type is switched on (it starts disabled) and has recipients, as an email when it happens rather than at the 02:00 import. A rejected pull's message names the Safes that are no longer returned (and new ones), by number against the Safes the last good pull staged, and the log shows before and after counts.

**Credential checks (D-250).** The credential list on the sources page says whether a CP or CCP credential's backend has its settings (CP: an AppID; CCP: a base URL and an AppID) and links to Admin > Secrets Store when it doesn't; Test says so first. Admin > Credentials shows what uses each credential and won't delete one in use; **Cancel and return to CyberArk sources** appears when it was opened from there.

**Pull details.** Each pull in the Data Sources log has a **Details** button: a modal with the outcome, who started it and how long it took, every message on its own line, all the counts, what the outcome means and what to do about it.

**Pull now** (Admin > Data Sources, `ManageDataSources`; the Privilege Cloud folder has no Run now) starts a pull in the background and the page polls the log. After a **Rejected** pull the page offers **Accept the change and pull**, which pulls again and accepts whatever PVWA returns, after a confirmation (always, not only in business hours). Both are audit-logged. The nightly pull skips quietly if one is running.

## Settings (`web.cyberark_source_config`, script 38)

| Setting | Default | Notes |
|---|---|---|
| `SelfHostedSource` | `Direct` | Only `Direct` since D-278 (script 47 changed the default and the CHECK); the page no longer offers a choice. |
| `PvwaBaseUrl` | none | `https://host`, with or without `/PasswordVault`. https only: the credential goes over the connection. |
| `PvwaCredentialKey` | none | A `web.credential` row. The Credentials page needs its own permission, so Data Sources has its own names-only list (`GET .../credentials`). |
| `PvwaLogonMethod` | `CyberArk` | `CyberArk` or `LDAP`; both take a user name and password. Windows and SAML logon aren't supported. |
| `DirectPullTime` | `01:30` | `HH:mm`, server time. Must come before the Import job. |
| `DirectMaxChangePercent` | 20 | 0 to 100. |
| `DirectPullTimeLimitMinutes` | 45 | D-273, script 44. 5 to 720. A pull (nightly or Pull now) still running after this long is stopped and logged as Failed. Staging is replaced in one transaction, so a stopped real pull leaves what the Load reads as it was. Test isn't limited. |

**Cancel pull** (D-275) on Data Sources stops the running pull, nightly or Pull now; it is logged Failed, saying it was cancelled, and staging is left as it was.

`dbo.cyberark_direct_pull` logs each pull (start, end, outcome, who started it, whether a change was accepted, counts, the counts it was compared with, message). Rows older than 30 days are purged after the nightly pull; the latest is always kept. A pull still "Started" after two hours (the app stopped) is marked Failed by the next one.

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

Locations: PVWA gives a path and no id; since D-277 the Load keys Self-Hosted locations by path, so the mapper stages none.

**Permission mapping** (compared with the EVD for 88 Safe members; all agreed): `listAccounts` List, `retrieveAccounts` Retrieve, `addAccounts` CreateObject, `updateAccountContent` UpdateObject, `updateAccountProperties` UpdateObjectProperties, `renameAccounts` RenameObject, `deleteAccounts` Delete, `viewAuditLog` ViewAudit, `viewSafeMembers` ViewOwners, `useAccounts` UsePassword, `initiateCPMAccountManagementOperations` InitiateCPMChange, `specifyNextAccountContent` InitiateCPMChangeWithManualPassword, `createFolders` CreateFolder, `deleteFolders` DeleteFolder, `unlockAccounts` UnlockObject, `moveAccountsAndFolders` MoveFrom and MoveInto, `manageSafe` ManageSafe, `manageSafeMembers` ManageSafeOwners, `backupSafe` Backup, `accessWithoutConfirmation` NoConfirmRequired, `requestsAuthorizationLevel1` Confirm. Left NULL (no REST counterpart): ValidateSafeContent, EventsList, EventsAdd.

**Not read:** discovered (pending) accounts (a separate pool that can be cleared in bulk, so it would distort the change guard); requests and confirmations (dual-control workflow; the tables are emptied); dependent accounts (14.6 and later only); users' addresses and phone numbers (the user's choice, D-270). `DeviceType`, `CreationMethod` and the linked accounts, which REST doesn't return, come from the classic API since D-280 ("Beyond REST").

### The `stg_shd_*` tables (D-270, script 41)

The same pull is also written in REST's own shape, in the same transaction and under the same `ImportBatchId`, to keep everything REST returns, including what the EVD-shaped tables have no column for. Column names are the REST field names in PascalCase; times are UTC `DATETIME2`. **Nothing reads them yet**: the Load reads `stg_sh_*`, so its output is unchanged, and an EVD import leaves them alone. They are the first step of `Archive_Planning_Self-Hosted-Direct-And-EVD-Retirement.md`.

| Table | From |
|---|---|
| `stg_shd_platforms`, `stg_shd_platform_properties` | `Platforms` (general, credential, session and workflow policy; required and optional properties, linked account types) and `Platforms/Targets` (reason for access, verify and change on add, reset mode). A refused `Targets` call is a warning. |
| `stg_shd_safes` | `Safes?extendedDetails=true`, every field (`lastModificationTime` is sent in microseconds). |
| `stg_shd_accounts`, `stg_shd_account_properties` | `Accounts`: `secretType`, `categoryModificationTime`, all of `secretManagement` (`status` as sent, `lastReconciledTime`), `remoteMachinesAccess`; every platform property, with no length limit on the name. |
| `stg_shd_account_cpm_activity` | The classic `WebServices/PIMServices.svc/Accounts/{id}/Activities/` call, only for accounts with a `secretManagement.status` (the CPM has acted on them), 8 at a time. Kept: the latest of each CPM code (22 verify, 38 verify failure, 24 change, 57 change failure, 31 reconcile, 60 reconcile failure, 414/415/419 SSH key). A failed call is a warning. |
| `stg_shd_safe_members` | Safe members including predefined ones, with `isPredefinedUser`, `isReadOnly` and a `BIT` per permission named as in `stg_pc_entitlements`. |
| `stg_shd_users`, `stg_shd_user_authorizations` | The user list and each person's details: name parts, organization, department, title, emails, DN, description, expiry, last logon, password flags (`DetailsRead` says whether details were read); Vault authorizations, unauthorized interfaces and authentication methods. |
| `stg_shd_groups`, `stg_shd_group_members` | `UserGroups?includeMembers=true`, with `directory` and `dn`, and members' names. |

## What the Load reads with Direct (D-277, script 45)

When tonight's Self-Hosted staging came from a real Direct pull (`dbo.fn_SelfHostedStagedByDirect()`: `stg_sh_safes` and `stg_shd_safes` share a batch), the Load also reads `stg_shd_*`. With EVD staging every step is as before, so switching back to the EVD is the rollback.

| Load step | With Direct staging |
|---|---|
| `usp_Load_FactAccount` | The MERGE from `stg_sh_*` as before (inserts, deletes, ignored Safes), then from `stg_shd_accounts`: `AutoManaged` (true and false), `ManualReason`, `CPMStatus` (trimmed, lowercased), `SecretType`, `RemoteAccessRestricted`, `LastCPMModifiedDate` (`lastModifiedTime`), `LastReconciledDate`, `LastVerifiedDate` (REST, else the latest verify in the CPM activity), and the new `CreatedTime`, `LastCPMAction`, `LastCPMActionResult`, `LastCPMActionTime` (latest of codes 22/38 Verify, 24/57 Change, 31/60 Reconcile). `PlatformLogonDomain` keeps its value where REST has none. Times are UTC. The account detail page shows the creation time and last CPM action; `vw_powerbi_accounts` has all four (script 46). |
| `usp_Load_FactSafeEntitlement` | From `stg_shd_safe_members`, resolved by `usp_Resolve_EntitlementMembers` (manual, ID, name) like Privilege Cloud: predefined members included, a member with no user or group kept as unresolved (R3). Every REST permission, by its REST name (`permission_alias`); `MoveAccountsAndFolders` counts as MoveAccountsFrom and MoveAccountsInto. |
| `usp_Load_DimGroup` | `DirectoryType` from REST `directory`. |
| `usp_Load_DimLocation` and the Safe, user and group loads | Both sources: a Self-Hosted location is keyed by its path (`SourceLocationId` = `LocationName`, R6); script 45 moved existing rows over keeping their `LocationKey`. The mapper stages no location id. |

Dependent accounts aren't returned by REST, so they drop out (deleted in CyberArk) at the first Direct load. So do accounts in CyberArk's internal Safes; `dbo.vw_selfhosted_internal_safe` (marked Internal on the shadow comparison, a built-in name, or named after a CPM, user type 31) lets the accounts list and detail say "internal Safe" for them. The Unresolved report lists Self-Hosted members with their source, and Resolve links one to a Self-Hosted user or group or labels it External (D-279).

**Change guard baseline** (`SelfHostedDirectPullService.BaselineAsync`): the last pull that Succeeded. (D-277 compared the first Direct pull with the EVD's staged counts, or the last shadow pull; D-278 removed both with the EVD.) Each pull stores the counts it was compared with (`BaselineSafeCount`, `BaselineAccountCount`). The "Safes that disappeared" list leaves internal Safes out.

## Beyond REST (script 48)

**Classic account details (D-280).** REST leaves out internal properties, so `DeviceType` (in `Properties`), `CreationMethod` and the linked logon (`ExtraPass1Safe/Folder/Name`) and reconcile (`ExtraPass3*`) accounts (in `InternalProperties`) come from the classic search `WebServices/PIMServices.svc/Accounts?Keywords=<userName address>&Safe=<safe>`, one call per account, 8 at a time. It returns up to 10 accounts; the one whose `AccountID` is the REST id is used. On the lab (2026-10-21) the user name and address always found it (20 to 45 ms) and the account's name never did, so the client tries "user address", then the user name, then the address; PVWA refuses `+ & %` even encoded, so such a value isn't sent. What was read is kept in `dbo.cyberark_direct_account_detail` (not staging) with the REST `categoryModificationTime` it was read at; an account is read again only when that time changes (on the lab it moved with property edits that left `lastModifiedTime`, the password time, alone) or is missing. Every pull stages the details of every account it knows in `stg_shd_account_details` (`ReadThisPull` says which were read tonight). Fresh reads are saved even when the pull is stopped or cancelled, so a first pull too long for the time limit gets further each night; the message says how many were kept. A failed call is tried once more, one at a time, after the others (D-288: the lab's PVWA answered about 1 in 70 with a 302 under load); an account still not found or refused keeps its earlier details and is tried again next pull; failures are one warning. Accounts PVWA no longer returns are dropped from the cache after a successful pull. Test reads none. `usp_Load_FactAccountClassicDetail` (after `usp_Load_FactAccount`) fills `fact_account.DeviceType`, `CreationMethod`, `LinkedLogonAccount`/`LinkedReconcileAccount` (`Safe\Name`, or `Safe\Folder\Name` outside Root) and `LinkedLogonAccountKey`/`LinkedReconcileAccountKey` (that account by Safe and name among Self-Hosted accounts, one not deleted first). The account page shows them, a linked account as a link; `vw_powerbi_accounts` has the four text columns at the end.

**CyberArk Applications (D-282).** `WebServices/PIMServices.svc/Applications/` (every application; Audit Users needed) and `Applications/{AppID}/Authentications/` for each, 8 at a time, into `stg_shd_applications` and `stg_shd_application_authentications`. `AuthValue` is kept for `machineAddress`, `osUser`, `path` and `certificateSerialNumber` and left out for a hash and any other type (the user's choice); a certificate attribute method keeps `Subject`, `Issuer` and `SubjectAlternativeName`. A refused list is a warning and stages nothing, so the Load keeps what it has; a refused or unaskable (`+ & %`) application keeps its old methods (`AuthenticationsRead = 0`). `usp_Load_DimCyberArkApplication` (after the groups) fills `dim_cyberark_application` (deleted ones flagged `IsDeletedInSource`, kept) and replaces `dim_cyberark_application_authentication` per application read. `vw_cyberark_application_account_access`: an application can retrieve an account when its Vault user (type 35, AIMAccount, named by the AppID) has `RetrieveAccounts` on the account's Safe directly or through a Vault group. Named "CyberArk Application" everywhere, as `web.dim_application` is BlueTrack's business applications (D-238). Shown on Reports > CyberArk Applications and on each account's page.

**People behind a Safe's groups (D-281).** `usp_Load_GroupMembership` now loads Self-Hosted Vault groups' member lists (`stg_shd_group_members`, by id) into `bridge_group_membership`, as it does Privilege Cloud's. After the PVWA read, `SafeGroupAdExpansionService` reads from AD the users of every group that is a Safe member, through the enabled LDAP domains of Admin > Credentials & LDAP and AD Account Discovery's directory code (`IAdDirectory`): a Self-Hosted directory group (it has a DN) by its DN in the domain its `DC=` parts name (every enabled domain when none matches), a Privilege Cloud group by its name in every domain (best effort: the export has no DN; groups as of the last load). Nested groups are flattened (D-116). Results go to `stg_shd_ad_group_reads` (Found, NotFound or Failed per group) and `stg_shd_ad_group_members`; `usp_Load_AdGroupMembers` replaces `dbo.ad_group_member` per group read, keeps the members of a group whose read Failed, and drops groups no longer read; `dbo.ad_group_read` keeps each group's last attempt and when it was last found. No enabled domain stages nothing and changes nothing. AD never fails the pull: a problem is a warning. With Self-Hosted off there is no pull, so at the pull time `SelfHostedDirectBackgroundService` runs `SafeGroupAdExpansionService.RunStandaloneAsync` instead (when Privilege Cloud is on): Privilege Cloud's Safe groups only, staged and applied at once in one transaction (`usp_Apply_AdGroupMembers`), with the outcome in the application log. `usp_Load_AdGroupMembers` applies staged reads only with Direct staging, so the Load never reapplies the standalone ones. `vw_safe_access_people` lists who reaches each Safe: Direct, Via Vault group, or Via AD group (for a group found in AD, its Vault member list is left out). The account page's "Who can use this account" (`GET /api/account-progress/{key}/safe-access`) shows each person and way in with Use, Retrieve and List, and the applications that can retrieve the account.

## Shadow pull and comparison (D-271, D-272): removed (D-278, script 47)

While the EVD was the source, a nightly shadow pull read PVWA into `stg_shd_*` only and `vw_shd_comparison` compared it with the EVD import, night by night, until the site had 7 clean nights in a row; admins marked expected differences, and script 43 marked CyberArk's internal Safes and the PVWA gateway members by itself. It was how a site proved Direct read the same Vault before switching (`Archive_Planning_Self-Hosted-Direct-And-EVD-Retirement.md`, phase 2). Script 47 removed it with the EVD: the settings, the pull log's `IsShadow` and its shadow pulls, the view and the comparison procedures, the API endpoints and the Data Sources panel. Kept: `dbo.shd_comparison_run` and `dbo.shd_comparison_difference` (the last comparisons, as history) and `web.shd_comparison_mark`, whose Internal marks `dbo.vw_selfhosted_internal_safe` still reads (no page edits them now).

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
- Run time was about 8 seconds for 40 Safes and 2,452 accounts; it grows with Safes (one call each) and users (one call each). The classic account details add one call per new or changed account (D-280): the first pull reads every account (on the lab 20 to 45 ms each, 8 at a time, so roughly 10 seconds per 2,000 accounts), later pulls only the changed ones. On a large Vault the first pull may need a higher time limit, or a few nights.
- The CyberArk Applications calls need **Audit Users** (D-282); the AD read needs an enabled LDAP domain whose bind account can read the groups (D-281).

## Where the code is

`App/Api/CyberArkDirect/`: `PvwaClient` (REST and the classic calls), `PvwaModels`, `SafeGroupAdExpansion` (D-281), `SelfHostedStagingMapper` (PVWA data to `stg_sh_*` and `stg_shd_*` rows), `PullChangeGuard`, `SelfHostedDirectRepository` (pull log, staging write), `SelfHostedDirectPullService`, `SelfHostedDirectRunner` and `SelfHostedDirectBackgroundService`. The settings, Test, pull log, Run now and Cancel are in `CyberArkSourcesController` (`/api/admin/cyberark-sources`, `ManageDataSources`). Test (on unsaved values) fetches the credential, logs on, counts what it can see and compares the counts with the last pull, writing nothing. The page is `CyberArkSourcesEdit.vue` and `DataSources.vue`. Who can use an account and the applications: `SafeAccessRepository`, `GET /api/account-progress/{key}/safe-access`, `CyberArkApplicationsController` (`/api/reports/cyberark-applications`), `SafeAccessSection.vue`, `CyberArkApplications.vue`. Tests: `PvwaClientTests`, `SafeGroupAdExpansionTests`, `SafeAccessTests`, `SelfHostedStagingMapperTests`, `PullChangeGuardTests`, `CyberArkSourceValidationDirectTests` (unit), `SelfHostedDirectTests`, `SelfHostedDirectPullServiceTests` and `SelfHostedDirectLoadTests` (integration: the SQL gate, the staging write, the pull log, the Load), `CyberArkSourcesControllerTests` (contract) and the page tests.

## Decisions made on 2026-10-19

| Question | Answer |
|---|---|
| Scope | Self-Hosted first; Privilege Cloud Direct later behind the same idea. |
| Versions | 14.2 and later. |
| Relation to the EVD | Replaces it when chosen; the EVD stays an option. (D-278, 2026-10-21: the EVD was removed; Direct is the only Self-Hosted source.) |
| Credentials | A named credential, so CP or CCP. CyberArk logon (LDAP too). |
| Where it runs | In the API, ahead of the nightly job. |
| Change threshold | A setting (percent); a Safe disappearing may be a deletion, so only counts are compared. Pending accounts are not counted or read. |
| A tripped guard | Fails the night (mail), until an admin accepts the change. |
| Extras | Platforms, Safe details, Safe members, user details (email) and group members, with nested groups. Linked accounts were asked for too; REST doesn't return them, so since D-280 they come from the classic API. Since D-270 everything REST returns is kept in `stg_shd_*` (above). |
