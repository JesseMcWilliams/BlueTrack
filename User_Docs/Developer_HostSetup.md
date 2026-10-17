# Development Host Setup

How to set up a Windows machine to develop BlueTrack: the ASP.NET Core API (`App/Api`), the Vue frontend (`App/Web`), the database scripts (`Database/`) and the three test suites. For installing BlueTrack on a server, see `Admin_Installation.md` instead.

## Prerequisites

- **Windows** (the API uses Windows authentication and DPAPI).
- **.NET 10 SDK**: `App/Api`, `App/Migrator` and the tests target .NET 10.
- **Node.js and npm**: to run and build `App/Web` (Vue 3, Vite 5) and to run the end-to-end tests in `App/E2E` (Playwright).
- **Git**, to clone the repository.
- **SQL Server** (2017 or later) that you can reach with Windows authentication, with permission to create databases.
- **Optional: the CyberArk Application Password SDK** (Credential Provider), installed at `C:\Program Files\CyberArk\ApplicationPasswordSdk`. Without it the API still builds and runs; the CyberArk CP secrets backend is just unavailable, and the build says so. To use an SDK installed elsewhere, set `CyberArkSdkPath` in `App/Api/BlueTrack.Api.csproj.user` (git-ignored).

## Step 1: Clone the repository

```powershell
git clone https://github.com/JesseMcWilliams/BlueTrack.git
cd BlueTrack
```

## Step 2: Create a database

The Migrator creates the database if it's missing and applies every script in `Database/` in order:

```powershell
dotnet run --project App/Migrator -- "Server=<sql-server>;Database=BlueTrack;Integrated Security=true;TrustServerCertificate=true" "Database"
```

The baseline maps the local `BUILTIN\Administrators` group to the Admin role, so a local administrator can sign in as Admin straight away.

For a local **test** database (used by the API tests and the end-to-end tests), create `BlueTrackTest_Local` the same way and then add the test-only seed data (`BlueTrackTest` belongs to the CI runner, which rebuilds it on every run). Never run `Database/Test` against a real environment. The script `Tools/Reset-BlueTrackLocalTestDb.ps1` does all of it, dropping and rebuilding the database:

```powershell
.\Tools\Reset-BlueTrackLocalTestDb.ps1 -SqlServerInstance <sql-server>
```

Or by hand:

```powershell
dotnet run --project App/Migrator -- "Server=<sql-server>;Database=BlueTrackTest_Local;Integrated Security=true;TrustServerCertificate=true" "Database"
dotnet run --project App/Migrator -- "Server=<sql-server>;Database=BlueTrackTest_Local;Integrated Security=true;TrustServerCertificate=true" "Database/Test"
```

You don't need real CyberArk export data to develop; most pages work on an empty database. Sample exports for a first load are in `Reference/` (git-ignored; ask for a copy), and the "First Data Load" section of `Admin_DeploymentRunbook.md` shows how to load them.

## Step 3: Run the API

Point the API at your database with `ConnectionStrings:BlueTrackDb`, either in `App/Api/appsettings.Development.json` or as the environment variable `ConnectionStrings__BlueTrackDb`. Then:

```powershell
dotnet run --project App/Api
```

It listens on `https://localhost:7033` and `http://localhost:5106` (`App/Api/Properties/launchSettings.json`). To check it, sign in with Windows:

```powershell
Invoke-RestMethod -UseDefaultCredentials https://localhost:7033/api/me
```

## Step 4: Run the frontend

```powershell
cd App/Web
npm install
npm run dev
```

Vite prints the address it serves on (5173 by default). It forwards `/api` requests to the API at `https://localhost:7033`, so start the API first.

## Step 5: Run the tests

| Suite | Command | Needs |
|---|---|---|
| API (unit, integration, contract) | `dotnet test App/Api.Tests` | `BlueTrackTest_Local` (the default; set `BLUETRACK_TEST_CONNECTION` to use another) |
| Web (Vitest) | `npm run test` in `App/Web` | nothing else |
| End to end (Playwright) | `npm test` in `App/E2E` | `BlueTrackTest_Local` (the default; same variable); Playwright builds the frontend and starts the API itself, signed in through a development-only test sign-in |

To run one API test class: `dotnet test App/Api.Tests --filter "FullyQualifiedName~<ClassName>"`. One Vitest file: `npx vitest run <path>`. One Playwright spec: `npx playwright test tests/<file>.spec.js`.

The integration and end-to-end tests change data in the test database, so don't run two test runs against the same database at once.

## Solution layout

- `App/BlueTrack.slnx`: the .NET solution.
- `App/Api`: the ASP.NET Core Web API (Dapper, no ORM).
- `App/Api.Tests`: xUnit unit, integration and contract tests.
- `App/Web`: the Vue 3 + Vite frontend, with Vitest tests.
- `App/E2E`: Playwright end-to-end tests.
- `App/Migrator`: applies `Database/*.sql` in filename order.
- `Database/`: the numbered SQL scripts (see `Database/README.md`); `Database/Test/` holds test-only seed data; `Database/Manual/` holds scripts run by hand.
- `Deploy/`: the installer, update, backup and restore scripts.
- `User_Docs/`: these guides. Design notes and decisions are in `Claude_Docs/`, starting with `Design_Architecture.md` and `Design_Decision-Register.md`.
