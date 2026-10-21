# Upgrade Guide

For the administrator who upgrades a BlueTrack site. It says which version to install from the one you run now, what to do before and after, and how to go back if an upgrade breaks something. Each version's own changes are in [Release_Notes.md](Release_Notes.md); how the installer works is in [Admin_DeploymentRunbook.md](Admin_DeploymentRunbook.md) and `Deploy\README.md`.

## 1. Find the version you run

Open **Admin > Deployment**: a release shows its plain number, such as `1.3.3`. A build made between releases shows a longer development version; treat it as the release before it.

## 2. Which version to install

Go down the table and stop at the first row that matches your site.

| Your site | Install | Then |
|---|---|---|
| The database was built before 1.0.0 (the database step stops with a message naming the commit tagged `pre-sql-baseline`) | First the commit tagged `pre-sql-baseline` (download that tag's source from GitHub and run its installer), so the database reaches the last old-style script. `Update-BlueTrack.ps1 -Release` takes only signed releases, and this tag isn't one | Come back to this table |
| Self-Hosted is imported from the **EVD** (Admin > Data Sources, CyberArk sources, says "from the ... database") | **1.3.4**, and no later version | Move to Self-Hosted Direct, following **1.3.4's** Deployment Runbook (in its download folder, or the `v1.3.4` release on GitHub), "Moving Self-Hosted from the EVD to Direct" (shadow comparison, 7 clean nights in a row, switch, first-night checks, sign-off). Then come back to this table |
| Self-Hosted is off, or already read from PVWA (Self-Hosted Direct) | **The latest release** | Nothing more |

**Why the EVD stop:** 1.4.0 and later have no EVD source. Their installer checks first and stops, changing nothing, on a site that still imports Self-Hosted from the EVD. 1.3.4 is the last version that has both the EVD and everything needed to move off it.

**Skipping versions is fine** (for example 1.0.7 straight to 1.3.4): the installer applies every database script you're missing, in order. But do the hand-made steps of each version you skip, listed in section 5.

## 3. Before you upgrade

1. **Read the release notes** of every version between yours and the one you install ("Upgrading" in each).
2. **Back up.** The installer backs the database up by itself before changing it, when the database already exists (into the **BackupFolder** answer, as `BlueTrack_<yyyyMMdd-HHmmss>.bak`; the installer's summary names the folder). To keep a copy of your own as well, from the `Deploy` folder of the version you run now:
   ```powershell
   .\Backup-BlueTrack.ps1 -SqlServerInstance <server> -DatabaseName BlueTrack -BackupFolder <folder> -IncludeMsdb
   ```
   It verifies the backup and writes a `.manifest.json` beside it with the database, the time and the version's commit. `-IncludeMsdb` also saves the SQL Agent jobs.
3. **Keep the version you run now.** `Update-BlueTrack.ps1` downloads each version into its own folder and keeps the earlier ones; don't delete the one you're leaving until the new one has run a night.
4. **Note the App Pool's account** if it isn't the default (a gMSA or a service account), and don't run the installer with `-Force` on an upgrade: it recreates the App Pool with the default identity (`Reference_Lessons-Learned.md`, 2026-10-21).
5. **Pick a quiet time**: after the nightly job (02:00 by default) has finished and well before the next night's pull and job.

## 4. Upgrade

From the `Deploy` folder of the version you run now:

```powershell
.\Update-BlueTrack.ps1 -Release v1.4.0      # a specific release, or no -Release for the latest
```

It downloads the signed release, copies your saved installer answers into it and offers to run its installer, which backs up, migrates the database, publishes the site and runs a health check (`Smoke`). Afterwards:

1. **Admin > Deployment** shows the new version, and every health check is healthy.
2. Do the "Upgrading" steps of the release notes, and the hand-made steps in section 5 for each version you skipped.
3. The next morning, check **Admin > Data Sources**: the nightly import and load succeeded (and, with Self-Hosted Direct, the night's pull).

## 5. Hand-made steps by version

Most database changes need nothing from you; these do.

| Version | Step |
|---|---|
| 1.0.5 | Install the .NET 10.0.13 (or later) Hosting Bundle by hand; the installer doesn't update an older one. If the Secrets Store uses Azure Key Vault, AWS Secrets Manager or CyberArk Conjur, enter the stored secret again and save. Add break-glass accounts and give the BreakGlassLogon notification a role. If you use the nightly job, run the installer once with `-Step Db.NightlyJob`. |
| 1.0.6 | If `Update-BlueTrack.ps1` warns about the download folder's access, run it once with `-LockDownFolder` on a folder that holds only downloaded versions. |
| 1.1.0 | Optional: enable the **ApiKeyExpiring** and **ApiKeyExpired** notifications. |
| 1.3.1 | With Self-Hosted Direct: check the first pull's **Details** on Data Sources (warnings mean the PVWA user lacks the Auditors or Vault Admins group). |
| 1.3.2 | The new **Pull time limit** starts at 45 minutes; raise it on Edit CyberArk sources if your Self-Hosted Direct pulls take longer. |
| 1.3.3 | None. A site on Self-Hosted Direct sees more account and entitlement detail after its next nightly load. |
| 1.3.4 | None. |
| 1.4.0 | Only on a site that no longer imports Self-Hosted from the EVD (the installer stops otherwise, changing nothing). The shadow pull and its comparison are gone; their stored history stays in the database. The installer no longer asks for an EVD database. Going back to 1.3.4 needs the database restore in section 6: 1.4.0 drops columns 1.3.4 reads. |

## 6. If something breaks: going back

Going back means **restoring the database** from the backup taken before the upgrade **and** running the **previous version's** installer, so the site and the database match again. Restoring only one of them leaves the site broken: a newer database may have tables or columns the older site doesn't expect, and the other way round.

1. Find the backup taken just before the upgrade: in the **BackupFolder** (the newest `BlueTrack_<yyyyMMdd-HHmmss>.bak` from before the upgrade; its `.manifest.json` names the version's commit), or the one you took yourself.
2. From the `Deploy` folder of the **previous** version (its download folder, kept in section 3), restore the database:
   ```powershell
   .\Restore-BlueTrack.ps1 -SqlServerInstance <server> -DatabaseName BlueTrack -BackupFilePath <path to the .bak>
   ```
   It asks you to type `YES`: it replaces the database's contents with the backup. Leave `-RestoreMsdbFilePath` out unless the SQL Agent jobs themselves are broken; restoring `msdb` affects every database on the instance. To put the nightly job back as the previous version had it, use step 3's `-Step Db.NightlyJob` instead.
3. Still in the previous version's `Deploy` folder, run its installer with the saved answers:
   ```powershell
   .\Install-BlueTrack.ps1 -ConfigFile .\State\answers.<SiteName>.json
   ```
   Its database step finds nothing new to apply (the restored database is already at that version), publishes the previous site and runs the health check. It also takes a fresh backup first, which is harmless.
4. Check **Admin > Deployment** (the previous version, all healthy) and the next night's import on **Admin > Data Sources**.

Anything recorded in BlueTrack between the upgrade and the restore (edits, imports) is lost with the restore; the next nightly import brings the CyberArk data back.

This procedure was rehearsed on 2026-10-21 on a test database: backup, a change after it, restore; the change was gone and the database came back online with every script recorded.
