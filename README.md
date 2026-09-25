# BlueTrack

BlueTrack is a **CyberArk PAM Blueprint progress-tracking system**: a SQL Server data warehouse fed by CyberArk Privileged Cloud/Self-Hosted exports, plus a Windows-hosted ASP.NET Core + Vue web application on top of it, that tracks every privileged account's remediation progress against the CyberArk PAM Blueprint (discovery → onboarding → management), records risk exceptions, computes a per-account risk score from Target/Access Group inventory, and gives analysts, approvers, and admins a governed, permission-gated interface to work the backlog.

## Features
- **Account Progress**: Blueprint stage/status per account, with layered filtering, pagination and locked, permission-gated editing.
- **Risk Exceptions**: account- or application-scoped exceptions with an approval workflow and overdue-review tracking.
- **Risk Scoring**: a Target/Access Group inventory (manual or CSV import) and a computed 0–1000 risk score per account, with a drill-down report.
- **Audit Logging**: who changed what, with field-level before/after values.
- **Admin console**: identity providers, roles/permissions, secrets backend, notifications, credentials & LDAP, and deployment/health info.
- **Authentication**: Windows Integrated by default; SAML2 and OIDC available. Authorization is permission-based.

## Requirements
- Windows, .NET 10 SDK, Node.js/npm, and a SQL Server instance you can create databases on.
- Only if CyberArk CP is the active secrets backend: CyberArk's Application Password SDK (see the comment in `App/Api/BlueTrack.Api.csproj`).
- Production: a single Windows server running SQL Server and IIS. See [Deploy/README.md](Deploy/README.md).

## Quick start
```
dotnet run --project App/Migrator -- "<connection string>" "Database"   # add "Database/Test" for a local test DB only
dotnet run --project App/Api
cd App/Web && npm install && npm run dev
```
Sample CyberArk exports for a first data load are in `Reference/`. See the "First Data Load" section of [Admin_DeploymentRunbook.md](User_Docs/Admin_DeploymentRunbook.md).

Tests: `dotnet test App/Api.Tests`, `npm run test` in `App/Web`, `npm test` in `App/E2E`. CI runs all three (`.github/workflows/ci.yml`).

## Documentation
- **Using and administering it:** [User_Docs/README.md](User_Docs/README.md). Released `.docx` guides are in [Published_Docs/](Published_Docs/).
- **How it works:** [Claude_Docs/Design_Architecture.md](Claude_Docs/Design_Architecture.md), then the subsystem `Claude_Docs/Design_*.md` docs.
- **Why it works that way:** [Claude_Docs/Design_Decision-Register.md](Claude_Docs/Design_Decision-Register.md), the numbered `D-n` decision log.
- **Database scripts:** [Database/README.md](Database/README.md). **Deployment:** [Deploy/README.md](Deploy/README.md).
- **Testing:** [Claude_Docs/Design_Testing-Strategy.md](Claude_Docs/Design_Testing-Strategy.md). Open findings: [Claude_Docs/Testing_Audit-Findings.md](Claude_Docs/Testing_Audit-Findings.md).
- **Backlog:** [Claude_Docs/Planning_Backlog.md](Claude_Docs/Planning_Backlog.md).
- **Gotchas:** [Claude_Docs/Reference_Lessons-Learned.md](Claude_Docs/Reference_Lessons-Learned.md).

## License
MIT. See `LICENSE`.
