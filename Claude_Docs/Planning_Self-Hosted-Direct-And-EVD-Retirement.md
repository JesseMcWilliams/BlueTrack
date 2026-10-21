# Planning: Self-Hosted Direct staging tables and retiring the EVD

> **Stage: Planning.** Phases 1 and 2 are built (D-270, D-271); the rest is not built or decided in the register yet. Agreed with the user on 2026-10-19: Self-Hosted Direct gets its own `stg_shd_*` staging tables that capture everything PVWA REST returns, and the EVD is to be removed as a Self-Hosted source. One production install runs on the EVD today; it must move to Direct **without breaking and without losing data**. Facts below were read from the code, the local Idira docs and the lab PVWA (14.6.0) on 2026-10-19/20. Once built, the design moves to `Design_CyberArk-Direct-Integration.md` and this file is archived.

## 1. What the user decided (2026-10-19)

| # | Decision |
|---|---|
| 1 | Two steps: new tables first (Load unchanged), then the Load reads them. **Capture all data REST makes available.** |
| 2 | Prefix `stg_shd_` (leaves `stg_pcd_` for Privilege Cloud Direct). |
| 3 | Step 1 writes **both** the new tables and the EVD-shaped `stg_sh_*` tables, so nothing that works today changes. |
| 4 | Safe member permissions shaped **like Privilege Cloud**: one `BIT` column per REST permission (`stg_pc_entitlements`, 01:385-415). |
| 5 | Read the account fields the pull doesn't read today (`secretType`, `remoteMachinesAccess`, and the rest). |
| 6 | Don't keep the raw JSON. |
| 7 | Plan to **remove the EVD**; the production install must transition safely. |

## 2. Facts that shape the plan

### What REST returns (lab 14.6.0, plus the 14.2-15.2 docs)

- **Accounts** (`GET /API/Accounts`): `id`, `name`, `address`, `userName`, `platformId`, `safeName`, `secretType`, `createdTime`, `categoryModificationTime`, `platformAccountProperties.*`, `secretManagement.{automaticManagementEnabled, manualManagementReason, status, lastModifiedTime, lastReconciledTime, lastVerifiedTime}`, `remoteMachinesAccess.{remoteMachines, accessRestrictedToRemoteMachines}` (documented; no lab account has it set), `deletionTime` (deleted accounts only, not in the normal list).
- **`lastVerifiedTime` was never returned**, even for accounts the CPM verified minutes earlier (`CyberArkSHRec`, `60_6`, verified 2026-10-20 00:12:47 UTC). The docs don't say when it is left out. `status` is only present once the CPM has acted on an account (13 of 2,454), and came back lowercase (`success`, `failure`); the docs give three different spellings. `lastReconciledTime` is returned (2 accounts). `lastModifiedTime` is the last password store, and equals `createdTime` for an account never changed.
- **CPM history**: the classic `GET /WebServices/PIMServices.svc/Accounts/{id}/Activities/` (documented 12.2-15.2) lists each CPM action with a time: activity codes 22 Verify, 38 Verify failure, 24 Change, 57 Change failure, 31 Reconcile, 60 Reconcile failure (`14.2\evd\action-codes.md`). `GET /API/Accounts/{id}/Activities` also answered on the lab's 14.6 but has no doc page in any version, so don't rely on it. One call per account.
- **Safes** (`?extendedDetails=true`): adds `description`, `managingCPM`, `olacEnabled`, `autoPurgeEnabled`, `lastModificationTime` (microseconds), `creator.id`, `isExpiredMember`. 15.2's details call adds `quota`/`usedquota`.
- **Safe members**: the 22 permissions, `memberType`, `membershipExpirationDate`, `isExpiredMembershipEnable`, `isPredefinedUser`, `isReadOnly`. **Predefined users are excluded unless `filter=includePredefinedUsers eq true`**; the pull doesn't pass it today, while the EVD lists every owner.
- **Users**: the list adds `suspended`, `componentUser`, `vaultAuthorization[]`, `allowedAuthenticationMethods[]` (14.4+); the details call (already made per user, for email) adds `expiryDate`, `lastSuccessfulLoginDate`, `passwordNeverExpires`, `changePassOnNextLogon`, `distinguishedName`, `description`, `unAuthorizedInterfaces[]`, `authenticationMethod[]`, `groupsMembership[]`, address, phones and the four email fields.
- **Groups** (`includeMembers=true`): adds `directory` and `dn` for directory groups.
- **Platforms**: `GET /API/Platforms` has `general.{systemType, platformBaseID}`, required/optional properties, linked account types, and the policy: `credentialsManagement.{performPeriodicVerification, requirePasswordVerificationEveryXDays, performPeriodicChange, requirePasswordChangeEveryXDays, automaticReconcileWhenUnsynched, ...}`, `sessionManagement.*`, `privilegedAccessWorkflows.*`. `GET /API/Platforms/Targets` adds `RequireUsersToSpecifyReasonForAccess` and exception flags.
- **Dependent accounts** (14.6+): `GET /API/dependentAccounts`. Not available on a 14.2 Vault.
- **No Vault-wide REST equivalent** for requests/confirmations (`MyRequests`/`IncomingRequests` are per caller) or the Vault activity log (only by creating a report task, which is a write).

### What the EVD path feeds today

- Keys are the same from both sources (lab-confirmed): Safe = `safeNumber`/`CASSafeID`; account = `SafeNumber_FileId`; user, group and member ids; platform = `PolicyID`/`platformId`. **Exception: `dim_location`**, whose ids Direct invents (`SelfHostedStagingMapper.cs:127-134`).
- Nothing loads `stg_sh_requests`, `stg_sh_confirmations` or `stg_sh_groupmembers` (Self-Hosted group membership was never built).
- EVD-only values that **are** used:
  - `CAOEventsList` becomes the Self-Hosted `ViewAuditLog` permission (best-guess alias, 01:789). Direct fills `CAOViewAudit`, which no alias reads, so those grants vanish under Direct.
  - `CAGLDAPDirectory` becomes `dim_group.DirectoryType`; Direct writes NULL over it.
  - `LogonDomain` for accounts where REST omits it (two PSM accounts in the lab). It's used by the AD Discovered Accounts match.
  - `CAFLastUsedDate` becomes `fact_account.LastVerifiedDate`. It is really the last retrieval by anyone (lab: an application's CP retrieval), so the column is mislabelled for Self-Hosted today.
- Deletion: only `fact_account` has a deleted flag. An account missing from staging gets `IsDeletedInSource = 1` (36:186-187); it comes back cleanly if it reappears. Dimensions are never deleted. Entitlements are rebuilt every load and keep no history. Progress, analyst edits, exceptions and audit rows are never deleted, but a deleted account leaves the daily snapshot, auto-advance and feed linking, and loses its `AddressDerived` target links.
- **The change guard has no baseline on the first Direct pull** (`PullChangeGuard.cs:18`): it is always accepted, and it is never compared with the EVD's counts.
- Saving the Direct source **clears `EvdDatabaseName`** (`CyberArkSourceValidation.cs:31`), so switching back means typing it in again.
- The installer only knows the EVD (`-EvdDatabaseName`); Direct is set up afterwards in the UI.

## 3. Risks to the production transition

| # | Risk | Consequence if ignored | Mitigation (phase) |
|---|---|---|---|
| R1 | The service user can't see some business Safes (not a member, or no View Safe Members) | Their accounts are soft-deleted at the first Direct load; their entitlements vanish | Shadow comparison before cutover (P2); first-pull guard against the EVD's counts (P3) |
| R2 | Internal Vault Safes are hidden from REST | Their accounts become "deleted in CyberArk" | Decide: exclude internal Safes from both sources, or accept and note it (P3). In the lab, 44 Safes were visible on 2026-10-20 (40 on 2026-10-19) |
| R3 | A Safe member that `/Users` doesn't return (above the service user in the hierarchy) | **Inferred:** breaks `CK_fact_safe_entitlement_*` and rolls back the whole nightly load (24:279-288) | Load an unresolved member as unresolved, the way Privilege Cloud does, instead of failing (P3); test it |
| R4 | Predefined members are left out of the pull | Fewer entitlements than under the EVD | Pass `includePredefinedUsers eq true` (P1); confirm in the comparison |
| R5 | `ViewAuditLog`, `DirectoryType`, some `LogonDomain` values | Values silently change or vanish | Alias REST `viewAuditLog` and fill `DirectoryType` from `directory` (P3); report the `LogonDomain` differences in the comparison |
| R6 | Invented location ids | A renamed location gets a new id; a switch back can collide | Key Self-Hosted locations by path, or keep the EVD ids by name and never reuse a number (P3); decision |
| R7 | Rollback after cutover | `EvdDatabaseName` is gone; the EVD copy may be stale | Keep `EvdDatabaseName` when switching (P1); runbook keeps the EVD available until sign-off (P4) |
| R8 | Meaning changes (`LastVerifiedDate`, `LastCPMModifiedDate`) | Reports change without explanation | Release notes; fill `LastVerifiedDate` from CPM activity (P3) |

## 4. Table set (`stg_shd_*`, script 41, phase 1: built, D-270)

Each table has `ImportBatchId` (the pull log's batch) and REST field names in PascalCase. Every pull replaces every table in one transaction, as today. REST times are stored as `DATETIME2` (UTC). The Load will reduce them to `DATE` where `fact_account` has `DATE` columns. As built, the column lists are in `Design_CyberArk-Direct-Integration.md` and script 41; this table was the proposal.

| Table | Columns (beyond the keys) |
|---|---|
| `stg_shd_platforms` | PlatformId, Name, SystemType, PlatformType, PlatformBaseId, Active, Description, AllowedSafes, PerformPeriodicVerification, VerifyEveryXDays, AllowManualVerification, PerformPeriodicChange, ChangeEveryXDays, AllowManualChange, AutomaticReconcileWhenUnsynched, AllowManualReconciliation, RequirePsm, RecordSessions, PsmServerId, RequireDualControl, EnforceExclusiveAccess, EnforceOneTimePassword, RequireReasonForAccess |
| `stg_shd_platform_properties` | PlatformId, Name, DisplayName, Kind (`Required`/`Optional`/`LinkedAccount`) |
| `stg_shd_safes` | SafeNumber, SafeUrlId, SafeName, Description, Location, CreatorId, CreatorName, OlacEnabled, ManagingCpm, NumberOfVersionsRetention, NumberOfDaysRetention, AutoPurgeEnabled, CreationTime, LastModificationTime, IsExpiredMember |
| `stg_shd_accounts` | AccountId, SafeNumber, FileId, SafeName, Name, Address, UserName, PlatformId, SecretType, CreatedTime, CategoryModificationTime, AutomaticManagementEnabled, ManualManagementReason, CpmStatus (raw), LastModifiedTime, LastReconciledTime, LastVerifiedTime, RemoteMachines, AccessRestrictedToRemoteMachines |
| `stg_shd_account_properties` | AccountId, Name (no 29-character limit), Value |
| `stg_shd_account_cpm_activity` | AccountId, ActivityCode, Activity, Time, UserName, Reason (CPM codes only; see Q3) |
| `stg_shd_safe_members` | SafeUrlId, SafeName, SafeNumber, MemberId, MemberName, MemberType, MembershipExpirationDate, IsExpiredMembershipEnable, IsPredefinedUser, IsReadOnly, the 22 permission `BIT`s named as in `stg_pc_entitlements` |
| `stg_shd_users` | UserId, UserName, Source, UserType, ComponentUser, Location, Enabled, Suspended, FirstName, MiddleName, LastName, Organization, Department, Title, BusinessEmail, HomeEmail, OtherEmail, UserDn, DistinguishedName, Description, ExpiryDate, LastSuccessfulLoginDate, PasswordNeverExpires, ChangePassOnNextLogon |
| `stg_shd_user_authorizations` | UserId, Kind (`VaultAuthorization`/`UnauthorizedInterface`/`AuthenticationMethod`/`AllowedAuthenticationMethod`), Value |
| `stg_shd_groups` | GroupId, GroupName, GroupType, Location, Description, Directory, Dn |
| `stg_shd_group_members` | GroupId, MemberId, MemberName |

Addresses and phone numbers in user details are left out unless wanted (Q4). Dependent accounts are left out because they need 14.6 (Q5).

## 5. Phases

Each phase is a separate PR and release; production upgrades through them in order.

### P1: capture (D-270, script 41) — built 2026-10-19, checked against the lab 2026-10-20

Lab check (pull 6, deployed build): same Safe, account, user, group and platform counts as the pull before; Safe members 150 → 517 (predefined members, `stg_shd_safe_members` only). `stg_sh_owners`, `_safes`, `_users`, `_groups`, `_groupmembers` and `_platforms` were identical; `stg_sh_files` and `_objectproperties` differed only by the CPM's work between the pulls (their mapping is unchanged). 13 accounts had a CPM status and 20 activity rows were kept; `lastVerifiedTime` stayed NULL, so verify dates come from activity code 22.

1. `PvwaClient` reads every field in section 4, passes `includePredefinedUsers eq true` for members, and reads CPM activity for accounts that have a `status`.
2. The repository writes `stg_shd_*` **and** `stg_sh_*` in the same transaction. The Load is unchanged.
3. Saving the Direct source keeps `EvdDatabaseName`.
4. Tests: mapper unit tests, a pull integration test against a fake PVWA, and a check that the Load output is unchanged.

### P2: shadow run and comparison (production stays on the EVD) — built 2026-10-20, D-271

As built (details in `Design_CyberArk-Direct-Integration.md`, "Shadow pull and comparison"): a **Shadow pull** checkbox and time on Edit CyberArk sources (after the import, default 04:00); the comparison as a SQL view, stored per night, shown on Data Sources with clean nights in a row (7 needed); an admin marks expected differences (Internal or Expected; item, Safe, or a member's user or group). Only unmarked missing-from-Direct and account field differences stop a clean night. The plan as first written:

1. A **shadow pull**: with the source still `Evd`, a configured Direct pull runs nightly and writes **only** `stg_shd_*`. It never touches `stg_sh_*` and never gates the import.
2. A **comparison** (a SQL view plus an Admin > Data Sources panel or a `Tools/` script) of `stg_shd_*` with the EVD-staged `stg_sh_*`, by key:
   - Safes, accounts, users, groups and Safe members only in one source;
   - per-account differences in platform, address, user name and `LogonDomain`;
   - entitlement permission differences.
3. Cutover criteria: no business Safe or account missing from Direct, or each one explained; member counts equal; differences only in the known items (R2, R5).

### P3: the Load reads `stg_shd_*` when the source is Direct
1. Self-Hosted Load branches read `stg_shd_*`. They fill `AutoManaged` (true and false), `ManualReason`, `CPMStatus` (case-insensitive and normalised), `LastCPMModifiedDate` (always from `lastModifiedTime`; Q6), new columns for the creation time, the last CPM action and its result (Q6), `LastReconciledDate`, `LastVerifiedDate` (from verify activity), `SecretType`, `RemoteAccessRestricted`, and `DirectoryType` from `directory`. `permission_alias` rows for the REST names fix `ViewAuditLog`.
2. Fixes that apply to both sources: unresolved Safe members (R3), location keys (R6).
3. **First-Direct-pull guard**: when no earlier Direct pull exists, compare with the counts the last EVD import staged. Over the threshold, the pull is rejected for an admin to accept, as today.
4. The EVD path still works, and is the rollback.

### P4: production cutover (runbook, no code)
1. Prerequisites:
   - PAM 14.2 or later;
   - a CP or CCP credential for a read-only service user that is a member of every business Safe with List Accounts and View Safe Members, and holds Audit Users;
   - membership of the Vault's **Auditors** group (Safes and accounts, including CPM activity) and **Vault Admins** (platform details). Vault Admins means the service user is no longer read-only; the user accepted that (question 10);
   - the user is high enough in the Vault hierarchy to see every user.
2. Run P2's shadow pull for at least N nights with a clean comparison (Q8).
3. `Backup-BlueTrack.ps1`, switch the source to Direct, run a manual pull, check the first load (counts, deleted-in-source accounts, entitlements), and sign off.
4. Rollback: switch the source back to EVD; the next import reloads from the EVD, and accounts reappear undeleted. Keep the EVD feed running until sign-off.

### P5: remove the EVD (a release after production has signed off)
1. **Upgrade guard**: `Install-BlueTrack.ps1` and the migration both refuse to install this release while `SelfHostedSource = 'Evd'`, naming the steps in P4. This keeps an unswitched install from losing its source.
2. Drop:
   - `usp_Import_SelfHosted_EVD` and the EVD branch of `usp_Import_All`/`usp_Import_Configured`;
   - `EvdDatabaseName`, the EVD option in the UI, `-EvdDatabaseName` in the installer, `Database/Manual/04` tokens;
   - the `stg_sh_*` tables and `CAO*` aliases, once nothing reads them;
   - the tests and docs in the inventory below.
3. Keep: every `dim_*`, `fact_*` and history row (the keys are the same), `import_log` rows naming the EVD, and the decision rows.

What P5 touches (from the 2026-10-19 inventory):

| Area | Files |
|---|---|
| SQL | 03 (`usp_Import_SelfHosted_EVD`), 12 (`EvdDatabaseName`), 38 (import procs, `SelfHostedSource`), `Database/Manual/04_*` |
| API | `CyberArkSourceSettings.cs`, `CyberArkSourceValidation.cs`, `CyberArkSourceConfigRepository.cs`, `CyberArkSourcesController.cs` (EVD Test), `NightlyImportFailedCheck.cs` |
| Web | `CyberArkSourcesEdit.vue`, `DataSources.vue`, `locales/en/admin.json` |
| Deploy/Tools | `Install-BlueTrack.ps1`, `Modules/BlueTrack.Database.psm1`, `Modules/BlueTrack.State.psm1`, `answers.sample.json`, `Tools/Test-BlueTrackIntegrations.ps1` |
| Tests | `CyberArkSourceValidation*Tests`, `SelfHostedDirect*Tests`, `ImportSkipDeletedAccountsTests`, `SelfHostedPlatformsTests`, `CyberArkSourcesControllerTests`, `CyberArkSourcesEdit.test.js`, `DataSources.test.js`, `admin-pages.spec.js` |
| Docs | `Admin_Installation`, `Admin_DeploymentRunbook`, `Admin_SettingsReference`, `Admin_OperationsGuide`, `Admin_Configuration`, `Design_Data-Sources`, `Design_CyberArk-Direct-Integration`, `Design_Architecture`, `Design_Dependencies-And-External-Integrations`, `Design_Deployment-Methodology` |

## 6. Data that leaves with the EVD

| Item | Used today? | Plan |
|---|---|---|
| Requests, confirmations | No | Accept the loss (no Vault-wide REST call) |
| `ValidateSafeContent`, `EventsAdd`, `Confirm` permissions | No alias | Accept |
| `EventsList` → `ViewAuditLog` | Yes | Replaced by REST `viewAuditLog` (P3) |
| `CAGLDAPDirectory` → `DirectoryType` | Yes | Replaced by REST `directory` (P3) |
| `LogonDomain` REST omits | Yes (AD match) | Listed in the comparison; accept or find a source (Q7) |
| `RemoteMachines` | Not read | Replaced by REST `remoteMachinesAccess` |
| `DeviceType`, `CreationMethod`, linked account properties (`ExtraPass*`) | Not read | Accept; `DeviceType` can come from platform settings on 15.2 only |
| Folder, file-type objects, user `LastLogonDate`/`CreationDate` | Not read | `lastSuccessfulLoginDate` replaces logon date |
| Internal Vault Safes | Their accounts are tracked | Q2 |

**Lab trial notes (shadow pull 7, 2026-10-20), confirmed by the user:**
- Internal Safes: AppProviderCacheSafe, AppProviderConf, the `PasswordManager*` Safes, Pictures (legacy), PSMLiveSessions, PSMNotifications, PSMSessions, PSMUnmanagedSessionAccounts, the `PVWA*` Safes and System. A Safe whose name starts with a CPM's name is also internal.
- A business Safe that PVWA can't see is usually missing the PVWAGWAccounts group ("Share this Safe with the following Gateway Accounts"). Test_Radius_00 was fixed that way.
- PVWAGWAccounts is CyberArk's internal group for PVWA connections, and each PVWA server's user (`PVWAGWUser` unless installed with a custom name) connects to the Vault. Neither is listed as a Safe member by REST. Test_Radius_02 is the one lab Safe with PVWAGWUser missing.
- The users missing from REST are directory mappings, not users.
- REST returns only an account's latest version; the EVD returns every version.
- Missing account 49_6 is a dependent account (Scheduled Task, `MasterPassName` set).

## 7. Questions for the user

1. **Production**: which BlueTrack and PAM versions, roughly how many Safes, accounts and users, and is a CP or CCP available there? *Partly answered 2026-10-20: it is upgraded to 1.3.1 on 2026-10-20, runs PAM 14.2, has a credential BlueTrack can use and reaches PVWA over HTTPS. Sizes still to come. The PVWA user is in Auditors and Vault Admins, but Safes with custom members may hide from it; the comparison's Safe list shows them.*
2. ~~Internal Safes~~ Answered 2026-10-20: let their accounts show as deleted after the switch, with a note that they are internal (from the comparison's Internal marks, D-271).
3. ~~CPM activity scope~~ Answered 2026-10-19: only accounts with a `status`, latest per action.
4. ~~User contact details~~ Answered 2026-10-19: leave addresses and phone numbers out.
5. ~~Dependent accounts~~ Answered 2026-10-20: leave them out.
6. ~~`LastCPMModifiedDate`~~ Answered 2026-10-20: always from `lastModifiedTime`. Also add columns for the creation time, the last CPM action and that action's result (P3).
7. **`LogonDomain`** where REST omits it: *2026-10-20: decide after the shadow comparison shows how many accounts have it and how many lose it* (its `LogonDomain` count).
8. ~~Shadow period~~ Answered 2026-10-20: 7 clean nights in a row.
9. ~~Release grouping~~ Answered 2026-10-19: separate.
10. ~~Service user roles~~ Answered 2026-10-20: the Auditors group for Safes and accounts, Vault Admins for platform details; Vault Admins is acceptable for the production service user.

## 8. Not in this plan
- Automatic Blueprint stages 4 and 5 (`Reference_Handoff-2026-10-19.md`, section 7). They need P3's CPM fields and the user's answers there.
- Privilege Cloud Direct (`Planning_Backlog.md`). It should reuse this table pattern as `stg_pcd_*`.
