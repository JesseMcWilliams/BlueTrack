import https from 'node:https'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// D-21/D-37: Vue, kept light and fast, no forced build complexity beyond
// what Vite needs. Dev-server proxy points at the ASP.NET Core API so the
// two projects can run side by side without a CORS dance during development.
//
// `server` (vite dev) and `preview` (vite preview, serving the built dist/
// output) are separate Vite config sections that don't share proxy config
// automatically -- both need the same /api proxy, but (D-155) no longer
// the same *agent*, see below.
//
// `agent` with maxSockets: 1 (confirmed necessary, 2026-09-04): Windows
// Integrated Auth (Negotiate/NTLM) binds its handshake to one specific
// underlying TCP connection between client and server -- it isn't a
// per-request credential. Vite's proxy (http-proxy under the hood) pools
// backend connections without guaranteeing a client's requests land on the
// same one, so a request landing on a different pooled connection than the
// one that completed the handshake gets rejected by Kestrel outright (an
// empty-bodied 400, not a clean 401) -- reproduced directly: every
// Negotiate-gated endpoint failed this way through the dev proxy while the
// same request against the API's own port succeeded. Pinning the proxy to
// a single persistent socket forces every proxied request through the one
// connection that actually completed the handshake. Fine for local dev
// (one developer, low concurrency) -- real deployments serve the built SPA
// and the API from the same origin/IIS site, no proxy involved, so this
// isn't a production concern.
const devApiProxy = {
  '/api': {
    target: 'https://localhost:7033', // matches App/Api/Properties/launchSettings.json's https profile
    changeOrigin: true,
    secure: false,
    agent: new https.Agent({ keepAlive: true, maxSockets: 1 })
  }
}

// D-155 (2026-09-23): `preview` (what Playwright's webServer actually runs,
// App/E2E/playwright.config.js) never needs the maxSockets: 1 pin above --
// every E2E test signs in via DevFakeAuth's Cookie scheme (auth.js's
// signInAs), never a real Negotiate handshake, and D-149 made the cookie
// always win when present regardless of what Negotiate did. Found via live
// SQL Server monitoring during a failing CI run: sys.dm_exec_requests never
// showed a single active BlueTrackTest query at any point, including the
// exact failure window, ruling out a slow/blocked query for an endpoint
// that barely touches the database -- pointing at the single pinned
// keep-alive socket itself occasionally going stale (a dead connection the
// client-side Agent doesn't detect until a request hangs on it) rather
// than genuine backend contention. A normal pooled agent can simply open a
// fresh connection instead of being stuck reusing a potentially-dead one.
const e2eApiProxy = {
  '/api': {
    target: 'https://localhost:7033',
    changeOrigin: true,
    secure: false,
    agent: new https.Agent({ keepAlive: true })
  }
}

export default defineConfig({
  plugins: [vue()],
  server: { proxy: devApiProxy },
  preview: { proxy: e2eApiProxy }
})
