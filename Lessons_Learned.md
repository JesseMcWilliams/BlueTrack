# BlueTrack — Lessons Learned

A running log of things discovered during development that aren't obvious from reading the code/schema alone — kept so they don't have to be rediscovered later. Add to this as new lessons come up; don't let it go stale.

## 2026-08-27 — Self-Hosted staging tables don't fully mirror the CyberArk reference DDL (resolved same day — see D-55)

`01_BlueTrack_CreateDatabase_Schema.sql`'s header comment states the `stg_sh_*` tables "mirror [`CyberArk_EVD_Self-Hosted_CreateDB.sql`]'s table shapes." A column-by-column comparison against `Reference/PrivilegedCloud/CyberArk_EVD_Self-Hosted_CreateDB.sql` on 2026-08-27 found that claim isn't fully true. Logged as **Q-27** in `Design Documents/Design_Decision_Register.md`; details here for whoever picks it up.

**By contrast, the Privilege Cloud side (`stg_pc_*`) was checked against the actual CSV export headers in `Reference/PrivilegedCloud/*.csv` and matches exactly, column-for-column, on every one of the six exports (Platforms, Users, Groups, Safes, Accounts, Entitlements) plus the Local Group Members export.** The gap is isolated to the Self-Hosted side.

### Missing columns on tables that do exist

| Staging table | Missing from reference | Likely relevance |
|---|---|---|
| `stg_sh_users` | `CAUFromHour`, `CAUToHour`, `CAULogRetentionPeriod` | Low — per-user access-hour restriction and log retention, not account lifecycle data |
| `stg_sh_owners` | `CAOViewAudit`, `CAOViewOwners`, `CAOUsePassword` | Worth checking — these are safe-owner *permission* flags, similar in kind to the permission columns already captured in `stg_pc_entitlements` for Privilege Cloud |
| `stg_sh_safes` | `CASVirusFree`, `CASTextOnly`, `CASAccessLocation`, `CASDelay`, `CASFromHour`, `CASToHour`, `CASShareOptions`, `CASDefaultAccessMarks`, `CASDefaultFileCompression`, `CASDefaultReadOnly`, `CASQuotaOwner`, `CASUseFileCategories` | Low — these read as legacy/deprecated CyberArk vault safe settings |
| `stg_sh_files` | `CAFLockDate`, `CAFLockBy`, `CAFLockByID`, `CAFAccessed`, `CAFNew`, `CAFRetrieved`, `CAFModified`, `CAFIsRequestNeeded` | Worth checking — `CAFAccessed`/`CAFRetrieved`/`CAFModified` track whether a password object has actually been touched, which could matter for reconciliation/progress state |
| `stg_sh_objectproperties` | A VaultID column (named `CACVaultID` in the reference — likely a naming quirk in CyberArk's own script, but the column itself is real) | Low for a single-vault deployment |

### Reference tables with no staging mirror at all

`CAEvents`, `CAITALog`, `CALog`, `CALocations`, `CAMasterPolicySettings` have no `stg_sh_*` counterpart. (`CATextCodes` is the exception — it *is* correctly represented, loaded verbatim as `dim_selfhosted_code`, and was checked value-for-value against the reference's `INSERT` statements.)

- `CALocations` likely doesn't need its own mirror — `dim_location` already exists as a shared dimension fed from other sources.
- `CAEvents`, `CAITALog`, and `CALog` are the vault's own internal audit/event logs. Whether these matter depends on whether Blueprint Progress Tracking ever needs vault-level audit data (as opposed to the new *application-level* audit log being designed in `Design_Audit_Logging.md`, which is a different thing entirely — that one logs actions taken in this web app, not actions taken in the CyberArk vault itself). Don't conflate the two when this comes up again.
- `CAMasterPolicySettings` holds password-policy configuration, not per-account data — plausibly out of scope, but not confirmed.

**Resolution (2026-08-27, same day, D-55):** went through every gap column-by-column instead of guessing. Added to the schema: `stg_sh_owners.CAOViewAudit`/`CAOViewOwners`/`CAOUsePassword`, and `stg_sh_files.CAFLockDate`/`CAFLockBy`/`CAFLockByID`/`CAFAccessed`/`CAFNew`/`CAFRetrieved`/`CAFModified`/`CAFIsRequestNeeded`. Confirmed as genuinely not needed (not added): `stg_sh_users`'s hour/retention fields, `stg_sh_safes`'s 12 legacy fields, `stg_sh_objectproperties`'s VaultID, and staging mirrors for `CAEvents`/`CAITALog`/`CALog`/`CALocations`/`CAMasterPolicySettings`. Lesson stands regardless: "looks irrelevant" isn't the same as "confirmed irrelevant" — this project's working agreement is to ask rather than assume on exactly this kind of call.

## Process notes

- **Check the Decision Register first.** `Design Documents/Design_Decision_Register.md` is the index of every design decision made so far, resolved and open. Don't re-derive or re-litigate something it already answers.
- **This project's schemas are hand-verified against real exports, not assumed.** The existing `stg_pc_*` tables and their inline comments (e.g. the `stg_pc_groupmembers` note about ~2% of rows being built-in system users) reflect careful comparison against actual sample data — the Self-Hosted gap above shows what happens when that same rigor hasn't yet been applied to a source. Treat "matches the source" as something to verify, not assume, whenever a new export or reference schema shows up.
- **Ask before assuming, especially on this project.** PAM/security engagements make wrong assumptions costly (wrong schema shape, wrong auth behavior, wrong scope). When a design decision or schema question comes up without a clear answer already on record, surface it as a question rather than picking a plausible-sounding default.
- **Governed data gets a dimension table, not free text or an ad hoc field** — reinforced repeatedly across this project's design docs (see `Design_Interface_Extensibility.md`'s D-08, and `dim_selfhosted_code`/`dim_account_type`/`dim_source_of_record` as existing examples).

## Tooling note

- This dev environment has no `pandoc` and no Python interpreter available, which matters if `.docx` files ever need reading/converting again (as happened when the Design Documents were converted to Markdown on 2026-08-27). A `.docx` is a zip archive — `unzip` is available and can extract `word/document.xml`, which can then be stripped of tags with `sed` (replace `</w:tc>` with a tab, `</w:tr>`/`</w:p>` with newlines, then strip remaining tags) to get readable text with table structure preserved well enough to reconstruct as Markdown.
- `Reference/PrivilegedCloud/CyberArk_EVD_Self-Hosted_CreateDB.sql` is UTF-16-encoded (a SQL Server Management Studio export default) — it reads as space-separated characters if opened as plain ASCII/UTF-8; account for that before assuming a read tool is showing garbage.
- SSMS expects CRLF line endings for `.sql` files and will warn about mixed/non-CRLF endings; files written by this dev environment's editing tools default to LF. When checking or converting line endings in this environment's Bash, `grep -c $'\r'` and `awk '/\r$/'` are unreliable here — they silently normalize `\r` away on read and report false negatives even after a real conversion. Use `file <path>` (reports "with CRLF line terminators" when correct) or `od -c` for a trustworthy check. `unix2dos`/`dos2unix` are available in this environment for the actual conversion.
