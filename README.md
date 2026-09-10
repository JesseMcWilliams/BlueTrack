# BlueTrack

BlueTrack is a **CyberArk PAM Blueprint progress-tracking system**: a SQL Server data warehouse fed by CyberArk Privileged Cloud/Self-Hosted exports, plus a Windows-hosted ASP.NET Core + Vue web application on top of it, that tracks every privileged account's remediation progress against the CyberArk PAM Blueprint (discovery → onboarding → management), records risk exceptions, computes a per-account risk score from Target/Access Group inventory, and gives analysts, approvers, and admins a governed, permission-gated interface to work the backlog.

This README is the front door — it orients you to the codebase and points at the detailed design/operational docs. It intentionally doesn't restate what's already written elsewhere; see **Documentation map** below for where the authoritative detail actually lives.

## Repository layout

```
App/
  Api/          ASP.NET Core 10 Web API (C#), Dapper over SQL Server, no ORM
  Api.Tests/    xUnit -- unit, integration (against a real BlueTrackTest DB), and contract tests, one project
  Web/          Vue 3 + Vite single-page app (Composition API, Pinia, Vue Router)
  E2E/          Playwright browser tests, run against the built SPA + a real running API
  Migrator/     DbUp-based console app that applies Database/*.sql in filename order
  BlueTrack.slnx
Database/       Numbered SQL scripts (schema, seed data, stored procedures) -- see Database/README.md
Design Documents/  One design doc per subsystem, plus Design_Decision_Register.md (the authoritative,
                   append-only log of every numbered design decision, D-1 onward)
Reference/       Sample CyberArk Privileged Cloud/Self-Hosted export files used for local dev/first load
Lessons_Learned.md  Real incidents/gotchas found while building this
```

## How the pieces fit together

- **Data warehouse (`dbo` schema)**: CyberArk Privileged Cloud CSV exports and a same-instance Self-Hosted (EVD) database are loaded into staging tables, then transformed into `fact_account`/`fact_account_progress`/dimension tables via `usp_Import_All` and `usp_RunFullLoad`. This runs nightly via a SQL Server Agent job on a real environment.
- **Web application (`web` schema)**: everything the browser-facing app owns — authentication/authorization, risk exceptions, audit logging, notifications, secrets-store configuration, and the Risk Scoring inventory (Targets/Access Groups) — lives in its own schema, separate from the warehouse data it reports on.
- **API + SPA**: the API is a thin Dapper-over-SQL-Server layer (stored procedures for anything computation-heavy, e.g. risk scoring), authorization-gated per endpoint; the SPA is a conventional Vue Router + Pinia app that consumes it over `/api`.

## Key functionality

- **Account Progress** — list/detail tracking of every account's Blueprint stage and status, with multi-layer filtering/sorting, pagination, and permission-gated editing (with pessimistic locking so two people can't edit the same account at once).
- **Risk Exceptions** — account- or application-scoped exceptions with an approval workflow, overdue-review tracking, and audit trail.
- **Risk Scoring** — a governed Target/Access Group inventory (manual CRUD or bulk CSV import, with configurable import field-mapping and a match-review queue for ambiguous matches), a computed 0–1000 risk score per account (two selectable scoring algorithms), admin-configurable risk bands, and an analyst-facing Risk Score report with a per-account contributor drill-down.
- **Audit Logging** — a searchable/filterable log of who changed what, with field-level before/after values on every tracked write.
- **Admin console** — identity providers, role/permission management, secrets-store backend configuration, notification recipients/config, credentials & LDAP, deployment/health info, and more — each screen gated by its own permission.
- **Notifications** — a general-purpose "alert the admins" framework (SMTP via a configurable relay), currently wired to warn when the DevFakeAuth development-only auth provider has been left enabled too long.

## Authentication & authorization

Windows Integrated authentication (Negotiate/Kerberos) is the primary, fully-wired identity provider. SAML2 and OpenID Connect are built as real code paths for environments that need a non-Windows IdP, but are not the default and need real IdP configuration before use. A `DevFakeAuth` provider (Development environment only) lets a developer exercise every permission path against their own local Windows account without a real domain. Authorization is entirely **permission-based** (not raw role checks) — every gated endpoint and UI element checks a specific named permission, resolved from the signed-in user's role(s).

## Secrets storage

Individual privileged credentials this app itself needs (e.g. an SMTP or LDAP bind account) are stored via a pluggable secrets-backend abstraction — Windows DPAPI (the default, encrypt-in-place, no external dependency), CyberArk Central Credential Provider (CCP, a REST call to a configured PVWA host) and CyberArk Credential Provider (CP, a local in-process call via CyberArk's Application Password SDK), with Conjur, Azure Key Vault, and AWS Secrets Manager also present as backend implementations. Exactly one backend is active at a time, switched via the admin Secrets Store Configuration page, not by editing seed data.

## Getting started (local development)

**Prerequisites**: Windows, .NET 10 SDK, Node.js/npm, a reachable SQL Server instance you can create databases on. Building the API against CyberArk CP as the active secrets backend additionally requires CyberArk's Application Password SDK installed locally (see the comment in `App/Api/BlueTrack.Api.csproj`); every other backend builds without it.

1. **Stand up the database** (see `Database/README.md` and `Design Documents/Design_Deployment_Runbook.md` for full detail):
   ```
   dotnet run --project App/Migrator -- "<connection string>" "Database"
   ```
   For a local/test database, also run the `Database/Test` folder the same way (seeds DevFakeAuth test accounts — never run this against a real environment).
2. **Run the API**: `dotnet run --project App/Api` (Windows Integrated auth by default; enable `DevFakeAuth` in Development to log in as your own Windows account without a domain).
3. **Run the SPA**: `npm install && npm run dev` in `App/Web` (Vite's dev server proxies `/api` to the running API).
4. **Load sample data**: `Reference/PrivilegedCloud` and `Reference/SelfHosted` contain sample CyberArk exports you can run `usp_Import_All`/`usp_RunFullLoad` against for a non-empty local database — see the Deployment Runbook's "First Data Load" section for the exact call shape.

### Running the tests

```
dotnet test App/Api.Tests   # unit + integration (real BlueTrackTest DB) + contract tests
cd App/Web && npm run test  # Vitest — components/stores, no backend
cd App/E2E && npm test      # Playwright — real browser against the built SPA + a real running API
```

`.github/workflows/ci.yml` runs all three layers on a self-hosted Windows runner for every push/PR to `main` (test verification only — it doesn't build a release artifact or touch a real environment).

## Deployment & operation overview

BlueTrack targets a deliberately simple, **single-server topology**: SQL Server and IIS co-located on the same Windows box, for every environment (Dev/Test/Staging/Prod) — no web farm, no load balancer. The built SPA (`App/Web/dist/`) is served as static files at the IIS site root; the API runs as a nested IIS Application at `/api` via the ASP.NET Core Module, in-process. The app assumes a domain-joined server for its default Windows Integrated auth path; SAML/OIDC/DevFakeAuth exist for environments where that doesn't hold.

Operationally:
- A **nightly SQL Server Agent job** (installed once via `Database/14_BlueTrack_ScheduleImportLoadJob.sql`, run by hand — never through the Migrator) imports the latest CyberArk exports and re-runs the full transform/load, including risk-score recalculation for anything marked stale.
- The **Deployment admin page** surfaces environment name/version/build time, live health checks (SQL Server connectivity, secrets-backend reachability, identity-provider configuration completeness), SQL Server backup status, and an on-demand application backup — all permission-gated, not an open `/health` endpoint.
- A handful of items are **environment-specific and must be set deliberately on every new environment** — the bootstrap admin group mapping, real IdP configuration, the active secrets backend, and (for the scheduled job specifically) the export file paths — see the Deployment Runbook's own checklist rather than assuming a fresh environment is production-ready out of the box.
- The full build → publish → IIS-deploy procedure (as distinct from the schema/seed steps above) is documented as a **first-draft proposal**, not yet executed and verified end-to-end on a real host — see `Design Documents/Design_Deployment_Methodology.md`'s own Open Questions before treating it as a settled runbook.

For the complete, authoritative version of everything summarized above, see:
- `Design Documents/Design_Deployment_Runbook.md` — step-by-step environment stand-up/rebuild procedure
- `Design Documents/Design_Deployment_Methodology.md` — build/publish/IIS-deploy process
- `Design Documents/Design_Admin_Deployment_Management.md` — the Deployment admin page itself
- `Database/README.md` — every script, in order, with what it does

## Documentation map

- **`Design Documents/Design_Decision_Register.md`** — the single most important document in this repo: an append-only, numbered (`D-1`, `D-2`, ...) log of every real design decision made on this project, with the question asked and the exact decision reached (including live-verification results and test counts). When in doubt about *why* something is built the way it is, this is where the answer lives.
- **`Design Documents/Design_Dependencies_And_External_Integrations.md`** — every external system, API, and library this project depends on, and exactly what each one is used for.
- Every other `Design Documents/Design_*.md` file covers one subsystem in depth (authentication, authorization, risk scoring, secrets storage, notifications, testing strategy, accessibility/theming, etc.).
- **`Lessons_Learned.md`** — real incidents and gotchas encountered building this, worth reading before touching an area it covers.

## License

MIT — see `LICENSE`.
