# BlueTrack: Handoff, 2026-09-25

> **Stage: Planning.** Point-in-time snapshot for the next Claude session. Once every item below is closed, rename to `Archive_Planning_Handoff-2026-09-25.md` with `git mv`.

## What was done
- Ran the `ape-project-setup` skill: the scaffold script had already run earlier; this session filled `CLAUDE.md` (no FILL/TODO markers left) and migrated the docs.
- Doc migration (all `git mv`, history kept): `Design Documents/` → `Claude_Docs/Design_*`, `Guides/` → `User_Docs/`, `App Documents/*.docx` → `Published_Docs/`, `Lessons_Learned.md` → `Claude_Docs/Reference_Lessons-Learned.md`. Superseded Notification Framework → `Archive_Design_`.
- Audit tracker split into `Testing_Audit-Findings.md` (1 open item) and `Archive_Testing_Audit-Findings.md`. The 2026-09-16 survey was archived; its open items are in `Planning_Backlog.md`.
- README trimmed to an overview; its architecture sections moved to `Design_Architecture.md`.
- All references to moved docs were rewritten by script (152 files, comment/display text only in code). Leftover grep for old names is clean.
- aPeTemplate gained `Published_Docs/` (release-time `.docx` deliverables) in the CLAUDE template, README, README template, skill mapping and scaffold-script comment.

## State
| Repo | Branch | PR | Status |
|---|---|---|---|
| BlueTrack | `2026-09-25-claude-docs-migration` | #57 | Open. xUnit 441/441, Vitest 126/126, Playwright 57/57 passed before commit. |
| aPeTemplate | `2026-09-25-published-docs` | #4 | Open. No test suite in that repo. |

Neither PR is merged. Merge only when the user asks.

## Open items (numbering continues from the session)
10. User to confirm the two *(verify)* items in `Planning_Backlog.md` (installer on a blank server; CI runner labels) and the single-file test commands in `CLAUDE.md` ("Tests").
11. `.claude/settings.json` (a local `sqlcmd` permission) is untracked and was left out of the commit. User to decide: commit it or gitignore it.
12. `Database/40_BlueTrack_ScheduleAuditLogPurgeJob.sql`'s job description string now names the new doc paths. The real server keeps the old text until that script is re-run by hand. Cosmetic only.
13. External links or bookmarks to `Design Documents/`, `Guides/` or `App Documents/` (outside the repo) will break. Logged in `Planning_User-Docs-Backlog.md`.

## Gotchas for the next session
- Doc decisions made this session: Decision Register stays `Design_`; Testing Strategy stays `Design_` (it describes the test design).
- In Git Bash, `$TEMP` already ends in `\2`, so build the scratchpad path literally. With `VAR=x cmd | xargs perl`, the variable reaches `cmd`, not `perl`: put it before `xargs`.
- aPeTemplate files are a mix of LF and CRLF. Line-anchored `perl -pi` edits silently miss the CRLF files, so use the Edit tool there.
