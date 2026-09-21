# Operations Guide

**Blueprint Progress Tracking Web Interface**

## Scope

Task-oriented instructions for whoever deploys and operates BlueTrack (a DBA, or whoever holds elevated SQL Server access) — distinct from `Design_Deployment_Runbook.md`, which walks through building or rebuilding an environment from scratch. This guide covers the operational tasks introduced alongside the rollback mechanism and the `db_backupstatus_reader` script generator (added 2026-09-16): things a DBA runs occasionally, outside of a full install, that have no UI of their own inside the application.

**Honest scope note**: like `Design_User_Guide.md`, this currently covers only these two features — it is not a complete operations manual for every part of the deployment. `Design_Deployment_Runbook.md` and `Deploy/README.md` remain the authoritative references for the full install/rebuild procedure.

## Rollback (Backup and Restore)

BlueTrack's rollback mechanism is full-database backup/restore, not per-script "down" migrations (`Design_Deployment_Methodology.md`'s Option B) — there is no automated "undo this specific schema change" path. Back up before a risky change, and be ready to restore and redeploy the previous build if something goes wrong.

### Taking a backup

`Deploy/Backup-BlueTrack.ps1` backs up the target database, verifies the backup is actually restorable (`RESTORE VERIFYONLY`), and writes a `.manifest.json` alongside it recording the database name, timestamp, and the git commit the backup preceded.

```powershell
.\Backup-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFolder "D:\Backups\BlueTrack"
```

Add `-IncludeMsdb` only if you also need SQL Server Agent job definitions (the nightly Import+Load job) protected — this backs up `msdb` too, with its own manifest entry. Think carefully before adding this: `msdb` is shared by every database on the SQL Server instance, so restoring it later is an instance-wide action, not scoped to just BlueTrack.

**This also happens automatically** as part of `Install-BlueTrack.ps1`, right before it migrates an *existing* database (a fresh, empty database has nothing worth backing up, so this is skipped automatically in that case). Pass `-BackupFolder` to control where it lands, or `-SkipPreDeployBackup` to opt out entirely for that run.

### Restoring from a backup

`Deploy/Restore-BlueTrack.ps1` restores a specific backup file — there is no "restore the latest one" shortcut, since silently picking the wrong backup is worse than requiring you to name it explicitly.

```powershell
.\Restore-BlueTrack.ps1 -SqlServerInstance "SQLSERVER01" -DatabaseName BlueTrack -BackupFilePath "D:\Backups\BlueTrack\BlueTrack_20260916-020000.bak"
```

This is destructive (`RESTORE DATABASE ... WITH REPLACE`) — you'll be asked to type `YES` to confirm interactively, or pass `-Confirm:$false` for an unattended/scripted run. **This only restores the database.** Also redeploy the application build matching the backup's manifest `GitCommit` field (if one was recorded), so the running application and the restored database stay in sync — this script does not touch IIS, the published API, or the built SPA at all.

To also restore `msdb` (only if you've confirmed a SQL Agent job definition was actually lost as a result of the BlueTrack restore), pass `-RestoreMsdbFilePath` pointing at the corresponding `msdb` backup. You'll get a second, separate warning before that step runs, naming the instance-wide blast radius again.

### What this does not solve

There are still no down-scripts for the numbered `Database/*.sql` files — full-database restore remains the only rollback path. This is the deliberate scope of Option B, not an oversight; per-script rollback for the highest-risk schema changes remains a possible future investment, not something built here.

## Granting `db_backupstatus_reader`

The Deployment admin page's SQL Server backup-status check reads `msdb.dbo.backupset` (and related tables) — something BlueTrack's own least-privileged SQL account cannot do by default, and something the application can never grant to itself (a connection can't widen its own permissions from inside itself). `Database/38_BlueTrack_GrantBackupStatusReaderRole.sql` creates a dedicated `db_backupstatus_reader` role with exactly the `SELECT` grants needed, and adds a specific account or group to it.

**Never run this through `App/Migrator`** — like `14_BlueTrack_ScheduleImportLoadJob.sql`, it targets `msdb`, not the BlueTrack database, and Migrator always excludes it for that structural reason (see the script's own header and `Database/README.md`).

### Getting a filled-in copy

The easiest path: on the **Group / Role Mapping** admin page, resolve the AD group that should be able to read backup status, then click **Generate db_backupstatus_reader Script** (see `Design_User_Guide.md` for the exact UI steps) — this downloads a copy of the script with that group already substituted in.

Alternatively, edit `Database/38_BlueTrack_GrantBackupStatusReaderRole.sql` directly, replacing its `__TARGET_ACCOUNT__` placeholder by hand with the login/account that should be able to read backup status (e.g. `DOMAIN\BlueTrackAppPoolAccount`, or a resolved AD group's account name).

### Running it

```
sqlcmd -S <server> -C -i Grant-db_backupstatus_reader-<account>.sql
```

(or, running the tracked file directly by name instead of a generated download, substitute the account first). The role/grants are safe to re-run — only the final `ALTER ROLE ADD MEMBER` line actually changes anything on a second run, and SQL Server's own `ALTER ROLE ADD MEMBER` is itself a no-op if the member is already there.

**One real prerequisite**: the target account or group must already exist as a SQL Server login on this instance — granting `msdb` role membership doesn't create the login itself. If it doesn't exist yet, `ALTER ROLE` fails with a "principal ... does not exist" error; create the login first.

## Segregation of Duties toggle — nothing for a DBA to do

Unlike the two features above, `EnforceRiskExceptionSegregationOfDuties` (Global Application Configuration) is a plain application setting, defaulting off — no schema grant, no manual script, no `msdb` permission involved. An application admin turns it on or off directly in the UI; see `Design_User_Guide.md`.

## See also

- `Design_Deployment_Runbook.md` — the full step-by-step procedure for building or rebuilding an environment.
- `Deploy/README.md` — everything `Install-BlueTrack.ps1` and the standalone `Backup-BlueTrack.ps1`/`Restore-BlueTrack.ps1` scripts do, including parameters not repeated here.
- `Design_Deployment_Methodology.md` — the rollback mechanism's own design rationale (Option B) and the deployment steps this guide's rollback section fits into.
- `Design_Admin_Deployment_Management.md` — the Deployment page and the original design of the `db_backupstatus_reader` role (D-107).
- `Design_User_Guide.md` — the in-app admin's side of the script generator, and the Risk Exception SoD toggle/report.
