# BlueTrack: Claude Code project notes

CyberArk PAM Blueprint progress tracking: a SQL Server warehouse (ETL from CyberArk exports) plus an ASP.NET Core 10 API (Dapper, no ORM) and a Vue 3 + Vite SPA, hosted single-server on IIS. Deploy scripts are PowerShell with `Set-StrictMode -Version Latest`.

## Folder map
- `App/Api/`: the Web API. `App/Api.Tests/`: xUnit Unit/Integration/Contract, one project. `App/Web/`: Vue SPA + Vitest. `App/E2E/`: Playwright. `App/Migrator/`: DbUp console app that applies `Database/*.sql` in filename order.
- `Database/`: numbered SQL scripts; `Database/Test/` holds test-only seeds (never run against a real environment). Script order and purpose: `Database/README.md`.
- `Database/01_BlueTrack_CoreSchema.sql` is over 1,000 lines: grep for the object and read a line range; don't read the whole file.
- `Deploy/`: `Install-`/`Backup-`/`Restore-BlueTrack.ps1` plus `Modules/`. Details: `Deploy/README.md`.
- Don't read gitignored runtime output: `Logs/`, `Deploy/Logs/`, `App/**/bin|obj/`, `node_modules/`, `App/Web/dist/`, `App/E2E/test-results|playwright-report|blob-report/`. `Reference/` is gitignored sample CyberArk exports; read only a named file.
- External references are in `C:\Code\References\`. Check there before guessing at API behavior.

## Tests
- API: `dotnet test App/Api.Tests` (integration tests need the `BlueTrackTest` DB). One class: add `--filter "FullyQualifiedName~<ClassName>"`.
- Web: `npm run test` in `App/Web` (Vitest). One file: `npx vitest run <path>`.
- E2E: `npm test` in `App/E2E` (Playwright, `workers: 1`). One spec: `npx playwright test tests/<file>.spec.js`.
- CI (`.github/workflows/ci.yml`) runs all three on a self-hosted runner on this host. Strategy: `Claude_Docs/Design_Testing-Strategy.md`.
- E2E failing in a contiguous block usually means host contention or a wedged Vite proxy socket, not a code bug: `Claude_Docs/Reference_Lessons-Learned.md`. Kill only PIDs you started; never `taskkill /IM`.
- Redirect test output to a file and read only the summary or failures. Don't stream full test output into the conversation.
- The suite must stay at 100% pass. Run the single test file while iterating and the full suite before you commit.

## Code rules (details in the linked sections, not repeated here)
- Check `Claude_Docs/Design_Decision-Register.md` before designing anything; it's the append-only `D-n` log. New decisions get the next number.
- New SQL goes in the next numbered `Database/NN_BlueTrack_*.sql`; follow the header and re-run conventions in `Database/README.md` ("Build order"). Scripts `14_` and `40_` (Agent jobs) are run by hand, never by the Migrator.
- After touching `App/Api/Auth/AuthenticationExtensions.cs`, regression-test Windows Integrated auth: `Claude_Docs/Design_Authentication-Architecture.md`.
- List/grid, breadcrumb and delete-confirm UI patterns: `Claude_Docs/Design_Application-Structure.md` ("Cross-Cutting UI Conventions").
- PowerShell in `Deploy/` must pass `Deploy/PSScriptAnalyzerSettings.psd1`.

## Documentation layout
- `README.md` (root): an **overview only**. It covers purpose, requirements, a quick start and a short feature list, and links to `User_Docs/` and `Claude_Docs/` for everything else. Put detail in a doc and link to it rather than adding it to the README.
- `Claude_Docs/` holds every doc Claude creates or works from, named `<Stage>_<Topic-With-Hyphens>.md`:
  - `Planning_`: proposals and backlogs that aren't built yet. Once built, the doc becomes `Design_` or is renamed `Archive_Planning_...`.
  - `Design_`: how the current system works. Keep it current. Archive it only when the feature is removed or replaced.
  - `Testing_`: test plans, open findings and known issues. Closed findings move to `Archive_Testing_...`.
  - `Reference_`: rules that apply at every stage (lessons learned, conventions, interface contracts).
  - `Archive_<OriginalStage>_<Topic>.md`: finished or superseded material. **Don't read `Archive_*` unless the user asks or the task needs history.**
- `User_Docs/`: end-user documentation, usually written near the end of the project from `Claude_Docs/Planning_User-Docs-Backlog.md`. It's output, not a source of facts. Take facts from the code and `Claude_Docs/`.
- `Published_Docs/`: `.docx` deliverables (Installation, Dev Host Setup), refreshed from `User_Docs/` and `Claude_Docs/` when a release is created. Don't read them for facts, and don't edit them between releases.
- When you make a user-visible change, add one line for it to `Planning_User-Docs-Backlog.md`.
- Keep each doc to about 500 lines. Past that, move closed or old content into an `Archive_` file. Don't keep revision logs, because git has the history. Put dates in file names only for point-in-time snapshots, such as reviews.
- Rename docs with `git mv`, and update every link to them in the same change.
- If a doc is large, find the target with grep and read a narrow range. Keep table rows to one or two sentences.

## Docs: what to update for each kind of change
| Change | Update |
|---|---|
| Design decision | New `D-n` row in `Claude_Docs/Design_Decision-Register.md`; cite `D-n` in the commit subject. |
| New feature / changed behavior | The subsystem's `Claude_Docs/Design_*.md`; new SQL script → `Database/README.md`. |
| Cross-cutting or folder-structure change | `Claude_Docs/Design_Architecture.md`. |
| New work item or deferral | `Claude_Docs/Planning_Backlog.md`; delete the item when it ships. |
| Deploy/installer change | `Deploy/README.md` and `Claude_Docs/Design_Deployment-Methodology.md`. |
| New dependency or external system | `Claude_Docs/Design_Dependencies-And-External-Integrations.md`. |
| Bug fix / new gotcha | `Claude_Docs/Reference_Lessons-Learned.md` if it's a lesson others would hit. |
| Audit/test finding | `Claude_Docs/Testing_Audit-Findings.md`; closed items → `Archive_Testing_Audit-Findings.md`. |
| User-visible change | One line in `Claude_Docs/Planning_User-Docs-Backlog.md`. |

- For "verify the docs are updated", use a subagent to diff the branch against this checklist and report the gaps only.

## Git
- Don't work directly on `main`. Create a topic branch named `YYYY-MM-DD-<topic>` and open a PR into `main` with `gh`.
- Commit, push, open a PR or merge only when asked. "Commit and push" means both.

## Live testing
- Lab environment details are in `Live-Testing.local.md` in the project root. That file is gitignored. **Read it only when a task involves live testing.** Never copy its contents into tracked files, commit messages or PR descriptions.
- If `Live-Testing.local.md` is missing, ask for the details. Don't guess.
- Never write secrets into any file, log or commit message, including `Live-Testing.local.md`. That file names *where* the credentials live, not the credentials themselves.
- When an example, doc or test needs a password placeholder, use `ThisIsMy_FAKE_Password6!`. It's obviously fake, and it satisfies typical complexity rules.
