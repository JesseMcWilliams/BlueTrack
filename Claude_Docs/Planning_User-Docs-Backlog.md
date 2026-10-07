# BlueTrack: User Docs Backlog

> **Stage: Planning.** Add one line per user-visible change when it's made. At the end of the project, `User_Docs/` is written from this list instead of re-reading the project history. Once an entry is covered in `User_Docs/`, delete it.

| Date | Change | User doc / section it affects |
|---|---|---|
| 2026-10-07 | Windows sign-in now lands on the dashboard (or the page you asked for) instead of staying on the sign-in page; the sign-in page has a **Continue** button for Windows Integrated (D-173). The installer enables Windows auth at the IIS site root so browser sign-in works (D-172). | `User_Docs/Admin_Installation.md` (first sign-in); `User_Guide.md` (signing in); Installation `.docx` at next release |
| 2026-10-07 | Correction (D-171): with SQL Server on the same machine, `ApplicationPoolIdentity` needs a login for `IIS APPPOOL\<pool>`, not the computer account named in SQL Server's error message. `Admin_Installation.md` section 3 and `Admin_OperationsGuide.md` already updated. | Installation `.docx` at next release |
| 2026-10-07 | Installer: new `Db.AppPoolAccess` step grants the App Pool's account its SQL login (`-GrantAppPoolSqlAccess`, `-AppPoolSqlLogin`); smoke-test failures now explained by HTTP status. `Admin_Installation.md` section 3 already updated. | Installation `.docx` at next release; `Admin_OperationsGuide.md` ("Granting the app's own SQL Server access") |
| 2026-10-07 | The API builds without the CyberArk Application Password SDK; the CyberArk CP secrets backend is then unavailable on that server. | `User_Docs/Admin_Installation.md` (prerequisites); `User_Docs/Admin_Configuration.md` (Secrets Store); Dev Host Setup `.docx` at next release |
| 2026-10-05 | Installer: `answers.sample.json`; `-ListSteps`/`-Step`/`-StartAt`/`-Resume` with saved answers in `Deploy/State/`; offline/winget/download prerequisite sources (`-PrerequisiteSource`, `-InstallerSourcePath`); `-WhatIf` now a true preview. | `User_Docs/Admin_Installation.md`; Installation `.docx` at next release |
| 2026-09-25 | Guides moved from `Guides/` to `User_Docs/`; the Installation and Dev Host Setup `.docx` files moved to `Published_Docs/`. | `User_Docs/README.md`; any external links to the old paths |
