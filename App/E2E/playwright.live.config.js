import { defineConfig, devices } from '@playwright/test'

// Read-only checks against a deployed site, after an install or upgrade (`npm run test:live`). Not part of
// `npm test` or CI: this config's testDir is ./live, the normal suite's is ./tests.
//
//   $env:BLUETRACK_LIVE_URL = 'https://<your-BlueTrack-hostname>'
//   npm run test:live
//
// It signs in with Windows (`/login/windows`) as the user running it, so that user needs a BlueTrack role on the
// site (for example through Group -> Role Mapping); the browser is allowed to answer the site's Windows challenge.
// Besides reading pages it runs the audit log's Verify once, which adds one AuditChainVerified entry to the audit log.
const liveUrl = process.env.BLUETRACK_LIVE_URL
if (!liveUrl) throw new Error('Set BLUETRACK_LIVE_URL to the deployed site, for example https://bluetrack.example.com')
const host = new URL(liveUrl).hostname

export default defineConfig({
  testDir: './live',
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: liveUrl,
    trace: 'retain-on-failure',
    locale: 'en-US',
    extraHTTPHeaders: { 'X-Requested-With': 'XMLHttpRequest' },
    launchOptions: { args: [`--auth-server-allowlist=${host}`] }
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } }
  ]
})
