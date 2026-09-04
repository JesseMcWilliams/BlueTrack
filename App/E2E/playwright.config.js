import { defineConfig, devices } from '@playwright/test'

// Layer 4 (Design_Testing_Strategy.md): full browser-driven tests against
// a real running API + built SPA + disposable database (BlueTrackTest),
// signed in via DevFakeAuth's dev-only test sign-in endpoint
// (App/Api/Controllers/DevTestAuthController.cs) as different simulated
// roles -- see tests/auth.js for why that endpoint exists instead of a
// real Negotiate login.
//
// CI (Design_Testing_Strategy.md's own "What CI needs, mechanically")
// builds BlueTrackTest and runs `npm run build` itself before this; the
// webServer entries below exist so a developer can also just run
// `npm test` locally from a clean checkout, per that same document's
// Admin/Developer Requirements ("a README/CONTRIBUTING-level note on how
// to run each test layer locally").
const apiConnectionString =
  process.env.BLUETRACK_TEST_CONNECTION ??
  'Server=WIN-K5POLANERI5.Company.com;Database=BlueTrackTest;Integrated Security=true;TrustServerCertificate=true'

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  // Capped rather than left at Playwright's CPU-count default (6+ on this
  // box): confirmed directly that once the suite grew to 25 tests, the
  // default worker count made the API/SQL Server on this single shared
  // dev host (also this project's self-hosted CI runner -- D-88) collapse
  // under concurrent load, timing out even the trivial dev sign-in call
  // for most of the suite. 2 workers ran the full suite cleanly and
  // repeatably; a higher CPU-count default is fine for a suite this size
  // but not on this shared box.
  workers: 2,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:4173',
    trace: 'retain-on-failure'
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } }
  ],
  webServer: [
    {
      name: 'api',
      command: 'dotnet run --no-launch-profile',
      cwd: '../Api',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: 'https://localhost:7033',
        ConnectionStrings__BlueTrackDb: apiConnectionString
      },
      url: 'https://localhost:7033/api/auth/providers',
      ignoreHTTPSErrors: true,
      reuseExistingServer: !process.env.CI,
      timeout: 60_000
    },
    {
      name: 'web',
      command: 'npm run build && npm run preview -- --port 4173',
      cwd: '../Web',
      url: 'http://localhost:4173',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    }
  ]
})
