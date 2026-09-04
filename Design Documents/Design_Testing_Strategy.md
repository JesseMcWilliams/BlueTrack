# Web Interface Design Document — Testing Strategy

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how BlueTrack gets automated test coverage — until now, every feature built this project (Reports, Risk Exceptions, all Admin pages, Account Progress editing, authentication/authorization, secrets backends) has been verified manually against live infrastructure (real SQL Server data, real CyberArk endpoints, PowerShell scripts) and then thrown away. That's caught real bugs no compile-time check would have (the missing `@AccountKey` parameter regression in `RiskExceptionRepository`, the Azure SDK's `AggregateException` wrapping, AWS's non-standard error status code — see `Design_Decision_Register.md` D-84), which is worth preserving: this strategy keeps that same "verify against the real thing" instinct, just makes it repeatable instead of one-off.

This document covers what gets tested, at what layer, with what tools, and what infrastructure that requires. It does not cover CI/CD deployment pipelines beyond what's needed to run tests (a separate concern), and it does not attempt to bring the CyberArk CP/CCP/Conjur, Azure Key Vault, AWS Secrets Manager, or real OIDC/SAML IdP integrations under automated test — those stay manually verified against real credentials, the same way they've been built so far (see Explicitly Not Automated, below).

## Design Principles

- **Real infrastructure over mocks, the same instinct this project already has.** This app has no ORM (D-67: Dapper, deliberately) — its SQL is strings until a real database executes them. Heavily mocking `IDbConnectionFactory` would hide exactly the class of bug this project has caught by hand all along. Prefer a real, disposable SQL Server over an in-memory or mocked substitute wherever the layer being tested touches the database.
- **`DevFakeAuth` is the test harness for authentication and permissions, not a new mechanism.** It already exists for exactly this purpose (Design_Authentication_Architecture.md's own framing: "not a separate authentication mechanism," a real Negotiate handshake with substituted group membership) — CI never needs a real domain, real Kerberos tickets, or real AD group SIDs to exercise `[Authorize(Policy = ...)]` checks.
- **Test data is synthetic, never a copy of real data.** Even though a disposable test database can live on the same SQL Server instance as real Dev/Prod data (see Infrastructure, below), seeding it by copying real `fact_account`/`dbo.import_log`-sourced rows is out of scope — this is real privileged-account data from an actual CyberArk environment. Seed data is built the same way Dev's original seed data was: dimension/reference rows plus synthetic accounts, never a snapshot of anything real.
- **Each layer earns its cost.** Unit tests are cheap and numerous; end-to-end tests are expensive and few. A bug that a unit test can catch shouldn't need a browser to catch it — Playwright coverage is for user-facing flows spanning multiple layers (locking + validation + audit logging together), not a substitute for testing `SortParser` in isolation.
- **The database is rebuilt fresh, not reused across runs.** Matches D-58's drop-and-recreate philosophy: every test run applies the DbUp migrator against a clean schema rather than assuming leftover state from a previous run, or another test, is safe to build on.

## Test Layers

### 1. Unit tests — fast, no database, no HTTP

**API (xUnit):** pure logic with no SQL or HTTP dependency — `SortParser` (including the SQL-injection guard, D-42), `ExceptionIdGenerator`'s pattern rendering, `CyberArkErrorClassifier`, `ProviderSettingsReader`'s JSON parsing (including malformed input), `GroupIdentifierExtractor`'s claim-based extraction, `ExternalIdentifierReader`'s NameIdentifier-vs-Name fallback logic.

**Web (Vitest):** isolated component/store logic — the `rights` Pinia store's permission-gating computed properties, the field-metadata-driven form's client-side validation helpers, `SortParser`'s frontend counterpart if one exists.

### 2. Integration tests — real SQL Server, real Dapper repositories, no HTTP layer

An xUnit project that, per test run: creates/migrates a throwaway database (DbUp, same scripts as any real environment), then calls repositories directly and asserts on the resulting database state. This is the layer that actually protects the raw-SQL business logic — every `RiskExceptionRepository`, `AccountProgressRepository`, `AuthorizationRepository` method that isn't trivially a passthrough deserves a test here, not just at the API layer, since a repository bug can be masked by a controller that happens to call it in a way that doesn't trigger it.

### 3. API/contract tests — real HTTP, real database, substituted auth

`WebApplicationFactory<Program>`-hosted xUnit tests calling real controllers in-process over real HTTP, backed by the same kind of disposable database as layer 2, authenticating via `DevFakeAuth`. This is where cross-cutting behavior actually gets exercised together: permission-policy enforcement per endpoint, D-42's multi-layer filter/sort, D-50's pessimistic locking, D-51's validation rules, and the audit trail those actions are supposed to produce (`web.audit_event`/`audit_field_change` rows matching what the action claims to have done).

**Needs:** a purpose-built DevFakeAuth seed — the current `18_BlueTrack_DevFakeAuthSeed.sql` only seeds one disabled placeholder row, not the role/permission matrix (Viewer/Analyst/Approver/Admin, at minimum) this layer needs to test each permission boundary.

### 4. End-to-end tests — Playwright, real browser

Full browser-driven tests against a real running API + built SPA + disposable database, signed in via DevFakeAuth as different simulated roles. Covers complete user-facing flows: Account Progress edit with locking and validation, Risk Exception create/approve/revoke, every Admin page, and the frontend's own permission-aware UI (D-78 — confirming a Viewer genuinely never sees controls a Viewer can't use, not just that the API rejects them).

### Explicitly not automated

CyberArk CP/CCP/Conjur, Azure Key Vault, AWS Secrets Manager, and real OIDC/SAML IdPs stay manually verified against real credentials — CI should never reach out to real external systems, and none of these can be meaningfully faked without either a real endpoint or a mock that stops testing anything real. The one thing worth a cheap unit test at layer 1: each provider throws a correctly-classified `SecretRetrievalException` when misconfigured (already effectively proven live during D-84's testing — this would just make that repeatable).

## Toolchain

| Layer | Tool | Notes |
|---|---|---|
| API unit | xUnit | .NET's current default; no reason to deviate |
| Web unit | Vitest | Native Vite companion, already the project's build tool |
| API integration | xUnit | Same project/tool as unit tests, different fixture (real DB) |
| API/contract | xUnit + `WebApplicationFactory<Program>` | In-process HTTP, no separate server process needed |
| End-to-end | Playwright | Drives a real browser against a real running instance |

## Infrastructure

**CI platform:** GitHub Actions (the repo already lives on GitHub) — **self-hosted runner on this dev host**, per the decision below, rather than GitHub's own cloud runners.

**Why self-hosted, here specifically:**
- This machine already *is* a real target environment for this app — `WindowsDpapiProtector`'s DPAPI encryption is genuinely machine-bound (D-79), so tests touching it behave exactly like production only when run on a real target machine, not a portable container.
- A disposable test database (`BlueTrackTest` or similar) can live on this host's existing SQL Server instance directly — no cross-environment data transfer, no separate database server to provision.
- Real Windows Integrated Authentication, real IIS-adjacent behavior, and anything else genuinely OS-specific to this app's deployment target are naturally available, rather than approximated.

**Two considerations this brings, carried forward from the decision to use it (not new problems introduced by this document, but worth restating where the actual setup happens):**
- A self-hosted runner executes whatever a workflow file says, on this real machine, with access to the real CyberArk CP install, the real SQL Server instance, and DPAPI-protected material — a materially different risk profile from GitHub's ephemeral cloud runners. Acceptable for a private repo with the same review discipline already applied to everything else here, not a decision to make casually.
- Registering and running the actual runner service is a persistent, host-level change (a listener process this host runs indefinitely, which GitHub can trigger jobs on) — treated as a separate, explicitly-confirmed action from writing this design document or the test projects themselves.

**What CI needs, mechanically:**
1. The runner checks out the repo.
2. A `BlueTrackTest` database is created (or recreated) on the local SQL Server instance.
3. `App/Migrator` runs against it non-interactively (already supports this — it's a console app taking a connection string and a scripts folder as arguments).
4. A DevFakeAuth permission-matrix seed script runs (new — see Test Layer 3, above).
5. `dotnet test` runs the xUnit projects (unit, integration, API/contract).
6. `npm run build` builds the SPA, then Playwright runs against the built app + the API pointed at `BlueTrackTest`.
7. `npm run test` (Vitest) runs independently of the above, no database needed.

## Admin/Developer Requirements

- A `README` or `CONTRIBUTING`-level note on how to run each test layer locally (not just in CI) — a developer working on this app needs the same disposable-database workflow available on their own machine, not just on the CI host.
- Test projects follow this project's existing numbered-script convention for any new seed data they need (e.g., a `test/` or `Database/Test/` seed script distinct from the real environment's numbered scripts, so it's obviously not part of the real deployment sequence).

## Implementation Status

All four layers exist and pass, against a real `BlueTrackTest` database on this host, driven by the self-hosted GitHub Actions runner already registered here (`.github/workflows/ci.yml`):

- **Layer 1-3 (`App/Api.Tests`, xUnit):** one project, `Unit/`/`Integration/`/`Contract/` folders — 57 tests. Contract tests authenticate via `TestAuthHandler`, a test-only `AuthenticationHandler` that stamps the same `bluetrack:provider_type=DevFakeAuth` marker claim OIDC/SAML use, so `WebApplicationFactory` exercises the real authorization pipeline without a real Negotiate handshake (which `TestServer` can't perform at all — discovered directly building this, see `BlueTrackWebApplicationFactory`'s own comment).
- **Layer 1 (`App/Web`, Vitest):** `src/stores/rights.test.js`, 7 tests against the real `rights` Pinia store.
- **Layer 4 (`App/E2E`, Playwright):** `permission-boundaries.spec.js`, 4 tests against a real running API + built SPA. Role-switching goes through a new dev-only endpoint, `App/Api/Controllers/DevTestAuthController.cs` (`GET /api/auth/dev/test-signin`, 404 outside Development) — a real Negotiate-authenticated browser can only ever be the one Windows account it runs as, so DevFakeAuth's normal per-username lookup can't switch roles per test on its own.
- **Test fixtures:** `Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql` seeds synthetic `TestUser.Viewer` / `.Analyst` / `.Approver` / `.Admin` identities (never a real person's account) with a test-fixture-only Viewer/Analyst/Approver permission matrix; `Admin` reuses the real bootstrap role from `07_BlueTrack_WebInterface_Seed.sql`.

**A real incident happened building this (2026-09-03):** pointing `App/Migrator` at `BlueTrackTest` for the first time revealed every numbered script hardcoded the literal database name `BlueTrack` (`USE BlueTrack;`, and 01's `DROP DATABASE BlueTrack` / `CREATE DATABASE BlueTrack`) — completely independent of what `Migrator`'s connection-string argument actually said. It silently dropped and recreated the real `BlueTrack` database instead. Fixed at the root: every script now uses DbUp's `$DatabaseName$` substitution token, `Migrator` derives the target name from the connection string's own `Initial Catalog` (the only source of truth), and 01's `DROP`/`CREATE DATABASE` preamble was removed entirely — `Migrator` now only ever *ensures* the target database exists (create-if-missing, never drop); a caller that wants a database genuinely dropped-and-recreated (CI, via an explicit `sqlcmd` step in `ci.yml`) does that itself, visibly, rather than a shared tool doing it silently on every run. See `App/Migrator/Program.cs`'s own comment for the full reasoning.

## Open Questions

1. ~~**Runner registration**~~ — done; the runner is registered and running as a service on this host (`actions.runner.JesseMcWilliams-BlueTrack.WIN-K5POLANERI5`).
2. **Coverage starting point** — permission enforcement (layers 1-4) is now covered. Account Progress editing (locking, validation, audit trail together) — the other half of the original recommendation — is still open.
3. **CI gating** — `ci.yml` runs on `push`/`pull_request` to `main` so results are visible, but nothing makes it a required check yet. Still not decided; that's a separate, explicit branch-protection setting.
4. ~~**Test database lifecycle**~~ — resolved as full recreate: `ci.yml` drops `BlueTrackTest` explicitly (if it exists) before every run, then `Migrator` rebuilds it from `Database/` (skipping 09, SQL Agent job scheduling — see `ci.yml`'s own comment) plus `Database/Test/`. Confirmed fast enough in practice to not matter (seconds, not minutes).
5. **`runs-on` labels** — `ci.yml` targets `[self-hosted, Windows, X64]`, the default GitHub gives a self-hosted runner registered with no custom labels. Unconfirmed against this specific runner's actual registered labels; adjust if it doesn't pick up jobs.

---
*New document added 2026-09-04, following the decision to bring automated test coverage to an app that has, until now, been verified entirely by hand against live infrastructure. Implementation status updated the same day.*
