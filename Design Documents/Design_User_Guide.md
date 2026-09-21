# User Guide

**Blueprint Progress Tracking Web Interface**

## Scope

This is a task-oriented, "how do I use this" guide for BlueTrack administrators working in the web UI — distinct from the `Design_*.md` documents elsewhere in this folder, which record *why* something was built a particular way, not how to operate it day to day.

**Honest scope note**: this guide currently covers only the two newest UI-facing capabilities (added 2026-09-16, alongside the rollback deployment scripts covered in `Design_Operations_Guide.md`). It is not yet a comprehensive walkthrough of every page in the application — extend it feature by feature rather than treating this note as a promise the rest will appear later on its own.

## Enforcing Segregation of Duties on Risk Exception Approval

**Where**: Admin > Configuration (the Global Application Configuration page).

A new checkbox, **"Enforce segregation of duties on Risk Exception approval"**, controls whether the same person who approved a Risk Exception can also be the one who links that exception to an account.

- **Off by default.** Some organizations don't have separate staff available to both approve an exception and later link it to an account — leaving this off (the default) never blocks anyone from doing both.
- **When turned on**: if you approved a given Risk Exception, you personally can no longer be the one who sets an account's status to "Risk Accepted / Excluded" using that exception on the Account Progress edit screen. Attempting to do so shows a validation error explaining why, and nothing is saved — pick a different exception, or have a different person link it.
- This only checks the *specific pairing* of approver and linker for one exception — it does not restrict who can approve exceptions or who can edit Account Progress records in general.
- Saved from the same Global Application Configuration form as every other admin-wide setting (idle timeout, breadcrumb position, etc.) — click **Save** after checking the box.

### Reviewing past cases (regardless of whether enforcement is on)

**Where**: Reports > Risk Exception SoD (requires the `ViewRiskExceptionSodReport` permission).

This report lists every historical case where the same person both approved a Risk Exception and later linked it to an account — whether or not the enforcement checkbox above was turned on at the time. Use it to spot-check past activity even in an organization that keeps enforcement off, or to confirm enforcement is doing its job once turned on. Each row shows the Exception ID, the account, the person, and when the link happened.

## Generating a `db_backupstatus_reader` Grant Script

**Where**: Admin > Group / Role Mapping, in the existing "Lookup / Test Tool" section.

The Deployment page's SQL Server backup-status check needs a SQL Server account to be a member of a `db_backupstatus_reader` role in `msdb` before it can report anything — a permission this application can never grant itself (see `Design_Operations_Guide.md` for why). This page now has a button to generate the exact script a DBA needs to run to grant that role to a specific AD group, instead of hand-writing it.

**Steps**:
1. Type the group's name into the **Group name** field under "Lookup / Test Tool" and click **Resolve** (same lookup this page already offered — it resolves the friendly name to the account BlueTrack actually matches on).
2. Once resolved, a **Generate db_backupstatus_reader Script** button appears below the resolved result.
3. Click it — your browser downloads a `.sql` file (named after the resolved account) already targeted at that group.
4. Send the downloaded file to whoever administers the SQL Server instance. **This application never runs the script itself** — it must be run manually via `sqlcmd` against `msdb`, the same way the file's own header explains. See `Design_Operations_Guide.md` for the exact command.

If the group name can't be resolved, the button never appears — resolve it successfully first, same as the existing lookup tool already required.

## See also

- `Design_Operations_Guide.md` — the DBA/operator-facing side of the two features above (running the generated grant script, and the separate rollback backup/restore scripts, which have no UI of their own at all).
- `Design_Risk_Exception_Tracking.md` — the full Risk Exception approval workflow this segregation-of-duties check sits inside.
- `Design_Admin_Deployment_Management.md` — the Deployment page and its backup-status check that `db_backupstatus_reader` exists to support.
