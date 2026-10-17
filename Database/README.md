# Database

`App/Migrator` runs the scripts at the top level of this folder through
DbUp, in file-name order, recording each one in `dbo.SchemaVersions`. It
never reads the subfolders: `Manual/` holds scripts run by hand, and `Test/`
holds test-only seeds that CI applies to `BlueTrackTest` in a second run.

Every Migrator script uses DbUp's `$DatabaseName$` token for the target
database name, never a literal (D-89): the name comes from the
`Initial Catalog` of the connection string the Migrator is given. The
manual scripts that run through `sqlcmd` use its `$(DatabaseName)` variable
instead.

## Build order

| # | File | Purpose |
|---|---|---|
| 01 | `01_BlueTrack_Baseline_CoreSchema.sql` | The `dbo` schema: every dimension, staging, fact and bridge table for the CyberArk-mirroring/ETL side, with its reference rows. |
| 02 | `02_BlueTrack_Baseline_EtlLoads.sql` | The dbo load procedures (`usp_Load_*`), the name-pattern functions (`fn_MatchesNamePattern`, `fn_RegexSupported`, D-186), account reconciliation, `dim_date`, and the reporting and Power BI views. |
| 03 | `03_BlueTrack_Baseline_SourceImport.sql` | Operational: the `usp_Import_*` procedures that fill the `stg_*` tables from real exports, and `usp_Import_All` (source selection, D-176). |
| 04 | `04_BlueTrack_Baseline_WebSchema.sql` | The `web` schema (D-64): authentication, authorization, risk exceptions, audit, settings, secrets store, session cache, preferences, credentials/LDAP, notifications, risk scoring, AD account discovery, data feeds and account deletion, with confirmed reference rows; plus the two dbo columns that point into it. |
| 05 | `05_BlueTrack_Baseline_WebSeed.sql` | Starting data: identity providers (WindowsIntegrated on; DevFakeAuth, OIDC and SAML off), the bootstrap Admin role with every permission mapped to `BUILTIN\Administrators` (S-1-5-32-544), the default roles, Account Progress field metadata, and the data feeds' system user. |
| 06 | `06_BlueTrack_Baseline_WebLogic.sql` | Risk scoring procedures and functions, the audit-log purge procedure, the decommissioning views, Targets and links from account addresses (D-123, D-193), and **`usp_RunFullLoad`**, the nightly orchestrator. |

| 07 | `07_BlueTrack_ScheduledBackups.sql` | D-200: `web.app_config.ScheduledBackupTime` / `ScheduledBackupRetentionDays` and the `web.backup_run` history table. |
| 08 | `08_BlueTrack_LockTimeoutDefault.sql` | D-201: `LockTimeoutMinutes` defaults to 30 (new installs only; an install where someone has signed in keeps its value). |
| 09 | `09_BlueTrack_DefaultSignInProvider.sql` | D-205: `web.app_config.DefaultSignInProvider`, the explicit default sign-in method (set to the old implied default on first run). |
| 10 | `10_BlueTrack_AnalystRiskReport.sql` | D-208: the Analyst role gets `ViewRiskReport` (the Risk Score report), on existing installs too. |
| 11 | `11_BlueTrack_ConnectionTestAudit.sql` | D-210: the `ConnectionTest` audit event type, logged by the Secrets Store and Credentials Test buttons. |
| 12 | `12_BlueTrack_CyberArkSourceConfig.sql` | D-211: `web.cyberark_source_config` (the CyberArk sources' settings, edited on Admin > Data Sources), `fn_ExpandDateTokens` and `usp_Import_Configured`, the nightly job's Import step. |
| 13 | `13_BlueTrack_AccountInventoryFeed.sql` | D-212: `web.data_feed.SourceSystemKey` and the `AccountInventory` feed type; auto-advance now only for CyberArk (`PRIVCLOUD`/`SELFHOSTED`) accounts. |
| 14 | `14_BlueTrack_FeedAccountLinking.sql` | D-212: `vw_linked_feed_account` and `usp_Link_FeedAccounts` (a feed account CyberArk also has is linked, moved to Onboarded to Vault and audit-logged); the migration views ignore feed links. |
| 15 | `15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql` | D-214: `vw_export_account_progress`, `vw_powerbi_accounts` and the daily progress snapshot skip linked feed accounts, so they count an account once, as the app does. |
| 16 | `16_BlueTrack_BreakGlassAccounts.sql` | D-216: `web.break_glass_account`, and the `BreakGlassLogon` audit event and notification types. |
| 17 | `17_BlueTrack_ReportExport.sql` | D-220: `web.app_config.ExportMaxRows` (report export row limit, default 100000) and the `ReportExport` audit event type. |
| 18 | `18_BlueTrack_TrendsHistoryIndex.sql` | D-221: an index on `fact_account_progress_history (SnapshotDateKey)`, covering the Trends report's columns, so a date range reads only those days. |
| 19 | `19_BlueTrack_MoreNotifications.sql` | D-222: `dbo.etl_run_log` (one row per Import and Load run; `usp_Import_Configured` and `usp_RunFullLoad` redefined to write it -- later changes to them must keep it), `dim_notification_type.IsEnabled`, `notification_log.DedupKey`, the digest schedule columns on `notification_config`, and the `NightlyImportFailed`, `OverdueRiskExceptions` and `WeeklySummary` types. |
| 20 | `20_BlueTrack_SmtpOAuth2.sql` | D-223: OAuth2 (XOAUTH2) SMTP sign-in. `notification_config` gains `OAuthTenantId`, `OAuthClientId`, `OAuthClientCredentialType`, `OAuthTokenUrl`, `OAuthScope`, `OAuthAudience`, `OAuthClientAuthStyle` and `OAuthUsername` (none secret) and a check on `AuthMethod` (an unknown value becomes `None` first); `credential.ProtectedPassword` widens to `NVARCHAR(MAX)` for key files. |
| 21 | `21_BlueTrack_SnapshotAndSiteUrl.sql` | D-224: the daily snapshot leaves `usp_RunFullLoad`'s transaction. `usp_RunDailySnapshot` (logged in `etl_run_log` as `Snapshot`, which the step check now allows) and `usp_RunNightlyImportLoad` (Import, Load if the Import succeeded, then the snapshot whatever happened; the nightly job's one step) are new; `usp_RunFullLoad` gains `@TakeSnapshot` (default 1, so a run by hand or an old job still snapshots). `app_config.SiteUrl` (the address emails link to). |
| 22 | `22_BlueTrack_PcImportByHeader.sql` | D-228: the seven Privilege Cloud imports read columns by header name (`usp_Import_PC_ReadCsvByHeader`), so a changed export layout no longer fails with Msg 7301; a missing required column fails naming the file and column. `web.cyberark_import_column` lists each file's columns (read by the import and the Data Sources Test). New `Enabled`, `Suspended`, `Location` on `stg_pc_users`/`dim_user` and `Directory` on `stg_pc_groups`/`dim_group`; `usp_Load_DimUser`/`usp_Load_DimGroup` redefined. |
| 23 | `23_BlueTrack_TargetLinking.sql` | D-229: linked targets. `web.target_link` and `web.vw_linked_target`; `target_match_review` stores the imported row (`TargetName`, `TargetTypeKey`, `RiskScore`, `Description`, `DiscoverySource`, `ResolvedTargetKey`, the `NameMatchKey`/`SourceMatchKey` match keys) and gets the `Linked` resolution and a filtered unique index on pending rows (pending duplicates deleted, oldest kept); `web.target_match_review_identifier` holds the row's identifiers. `ufn_ReachableRiskValues` and `ufn_ReachableRiskValues_ForAccessGroupSet` redefined so a linked target counts as its primary -- later changes to them must keep that. |
| 24 | `24_BlueTrack_EntitlementMemberResolution.sql` | D-232: `fact_safe_entitlement.MemberName` and `ResolutionMethod` (checks replaced so a resolved row can keep `UnresolvedMemberId`), `web.entitlement_member_resolution` (manual resolutions), `usp_Resolve_EntitlementMembers` (manual, then ID, then unique exact name), `usp_Apply_EntitlementMemberResolution`, the `EntitlementMemberResolution` audit event type; `usp_Load_FactSafeEntitlement` and `vw_unresolved_entitlement_members` redefined -- later changes to the load must keep the resolution order. |
| 25 | `25_BlueTrack_AccountClassification.sql` | D-233: account classification on Discovered Accounts. `web.account_classification_config` (one row, off by default), `web.account_classification_rule` (Attribute / Name / OU rules; none seeded); `web.discovered_account` gains the attributes discovery reads (`UserPrincipalName`, `EmployeeId`, `EmployeeNumber`, `DirectoryAttributes` JSON), the result (`AccountClass`, `ClassSource`, `ClassRuleKey`, `OwnerSamAccountName`, `OwnerDisplayName`, `OwnerSource`) and the manual override (`ClassOverride`, `OwnerOverrideSamAccountName`, `OverrideBy`, `OverrideDate`); `web.ad_owner_lookup` (owners read from AD only as owners); the `AccountClassification` audit event type. |
| 26 | `26_BlueTrack_SiemForwarding.sql` | D-234: SIEM forwarding. `web.audit_event.WriteVersion` (ROWVERSION, unique index; the forwarder's watermark, read only below `MIN_ACTIVE_ROWVERSION()` so a late-committing row is never skipped); `web.siem_forwarding_config` (one row, off by default), `web.siem_event_type_setting` (forwarded and CEF severity per audit event type, seeded for every existing type), `web.siem_forwarding_state` (watermark, last send/error, the single-sender lease); `usp_PurgeAuditLog` redefined to keep rows not yet forwarded while forwarding is on -- later changes to it must keep that. |
| 27 | `27_BlueTrack_AccountIdentityLength.sql` | D-236: `fact_account.SourceAccountId` widened from NVARCHAR(100) to NVARCHAR(400) (the unique key `UQ_fact_account` is dropped and re-added around it), so a feed account's `username@address` can be up to 400 characters. |
| 28 | `28_BlueTrack_FeedSkipGroupRows.sql` | D-237: `web.data_feed.SkipGroupRows` (BIT, default 0): an account inventory feed with it on skips rows whose `UserName` is a group. |
| 29 | `29_BlueTrack_AccountApplication.sql` | D-238: `fact_account_progress.ApplicationKey` (an account's own Application; FK, index), the Application dropdown on the Account Progress edit form (`account_progress_field_metadata`), and `web.vw_account_application_exception` rebuilt to cover an account through its own application and its Safe's. |
| 30 | `30_BlueTrack_AccountAssignmentsFeed.sql` | D-241: the `AccountAssignments` value in `web.data_feed`'s FeedType CHECK constraint (a scheduled Account → application assignments import). |

## The baseline (D-195)

Before the first release, the numbered scripts `01`–`51` were consolidated
into the six baseline scripts above. They build exactly what the old
scripts built, schema and seed data alike, but in final form: later
columns and constraints are part of each `CREATE TABLE`, each procedure,
view and function appears once (its last version), and data fixes that did
nothing on a new database are gone. Proof: a database built from each set
compared with `Deploy/Compare-BlueTrackSchema.ps1 -IncludeData` shows no
differences. The old scripts are in git history, at the tag
`pre-sql-baseline`.

The baseline scripts run only against an empty database. DbUp records
scripts by file name only, so on a database built from the old scripts it
would treat the baseline as new and run it, wiping the data. `App/Migrator`
prevents that: if the journal shows the old scripts up to
`51_BlueTrack_TargetsFromCyberArkAddress.sql`, it records the six baseline
scripts as applied without running them. A database built from the old
scripts but not up to `51` is refused; upgrade it first from the
`pre-sql-baseline` tag, then again from the current code.

## Adding a script

1. Name it `NN_BlueTrack_<Topic>.sql`, with `NN` the next number (`30` is next).
2. Start with a header comment: the file name, `RUN THIS AFTER 01-NN.`, what
   it changes and why (with the `D-n` decision), and whether it's safe to
   re-run.
3. Make it safe to re-run, because it will run against databases holding
   real data: guard every schema change (`IF COL_LENGTH(...) IS NULL`,
   `IF OBJECT_ID(...) IS NULL`, `IF NOT EXISTS (SELECT 1 FROM sys...)`), use
   `CREATE OR ALTER` for procedures, views and functions, and guard seed
   rows with `NOT EXISTS`. Never drop and recreate a table that holds data.
4. Begin with `USE $DatabaseName$;` and `GO`.
5. Never edit a script that has already run anywhere, including the
   baseline: change things with a new script.
6. A new permission needs its own grant to the Admin role, since 05's
   "every permission" grant ran only once.
7. To apply it by hand instead of through the Migrator, use `sqlcmd -I`
   (QUOTED_IDENTIFIER on); see `Claude_Docs/Reference_Lessons-Learned.md`.

## Building a new environment

1. Optional: run `Manual/01_BlueTrack_CreateDatabase.sql` against `master`. The
   Migrator also creates the database if it's missing.
2. `dotnet run --project App/Migrator -- "<connection string>" "Database"`.
3. Test database only: `dotnet run --project App/Migrator -- "<connection string>" "Database/Test"`.
4. Load real data: `EXEC usp_Import_All ...;` then `EXEC usp_RunFullLoad;`.
5. Run the manual scripts that apply (below). `Deploy/Install-BlueTrack.ps1`
   runs step 2 and can also install the nightly job and the App Pool's grants.

## Manual scripts (`Manual/`)

Numbered in the order they're run; run only the ones that apply.

| File | When and how |
|---|---|
| `01_BlueTrack_CreateDatabase.sql` | Optional, before the first Migrator run. Connects to `master`, which DbUp can't. Replace `$DatabaseName$` by hand first. |
| `02_BlueTrack_GrantAppServiceAccountAccess.sql` | Gives the App Pool's Windows account its database permissions (D-30, D-171). The installer's `Db.AppPoolAccess` step runs a filled-in copy; by hand, replace `__TARGET_ACCOUNT__` first. |
| `03_BlueTrack_GrantBackupStatusReaderRole.sql` | When the Deployment page's backup-status check should work: lets the app's account read `msdb` backup history (D-107). Replace `__TARGET_ACCOUNT__`, or download a filled-in copy from the Group / Role Mapping page. |
| `04_BlueTrack_ScheduleImportLoadJob.sql` | Real environments, once Import and Load have each been run by hand and work: creates the nightly 2:00 AM Import + Load SQL Agent job in `msdb` (one step since D-224: `EXEC usp_RunNightlyImportLoad;`, which also takes the daily snapshot when the Import or Load fails; re-run it after script 21 to get that). `sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i Database/Manual/04_BlueTrack_ScheduleImportLoadJob.sql`, on a copy with its four `__TOKENS__` replaced. The job reads its source settings from `web.cyberark_source_config` (Admin > Data Sources); the script writes that row only when none exists (D-211). The installer's nightly-job option runs a filled-in copy. |
| `05_BlueTrack_ScheduleAuditLogPurgeJob.sql` | Real environments, once `RetentionDays` is set on Global Application Configuration: the 3:00 AM job calling `usp_PurgeAuditLog`. Same `sqlcmd` form. |
| `06_BlueTrack_DevFakeAuthUserMapping.sql` | Development only: maps your Windows username to Admin through DevFakeAuth. Replace `@DevFakeAuthUsername` first. |

The SQL Agent and backup-status scripts can't run through DbUp: they
`USE msdb;` and never switch back, so DbUp's journal write after them
would fail against `msdb`.

## Drift check

To find what a live database has that the scripts don't (or the reverse),
build a reference database from this folder on the same SQL Server and
compare:

```powershell
dotnet run --project App/Migrator -- "Server=<server>;Database=BlueTrackReference;Integrated Security=true;TrustServerCertificate=true" "Database"
.\Deploy\Compare-BlueTrackSchema.ps1 -SqlServerInstance <server> -ReferenceDatabase BlueTrackReference -DifferenceDatabase BlueTrack -IgnoreComments
```

Drop `BlueTrackReference` afterwards. `-IgnoreComments` hides comment-only
differences in procedures and views (a script's comment edited after the
database was built). Without `-IncludeData`, data is not compared.

## See also

- `User_Docs/Admin_DeploymentRunbook.md`: standing up or rebuilding an environment.
- `Import_Load_Process_Guide.docx`: the Import/Load cadence and the nightly job.
- `Claude_Docs/Design_Decision-Register.md`: every `D-n` referenced in the scripts.
- `Claude_Docs/Reference_Lessons-Learned.md`: incidents encoded in these
  scripts (the BULK INSERT `ROWTERMINATOR` fix, the `$DatabaseName$` rule,
  `sqlcmd -I`).
