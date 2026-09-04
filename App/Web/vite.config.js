import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// D-21/D-37: Vue, kept light and fast, no forced build complexity beyond
// what Vite needs. Dev-server proxy points at the ASP.NET Core API so the
// two projects can run side by side without a CORS dance during development.
//
// `server` (vite dev) and `preview` (vite preview, serving the built dist/
// output) are separate Vite config sections that don't share proxy config
// automatically -- both are set here, identically, since Playwright E2E
// (Design_Testing_Strategy.md layer 4, App/E2E) runs against the built app
// via `vite preview`, needing the same /api proxy dev already relies on.
const apiProxy = {
  '/api': {
    target: 'https://localhost:7033', // matches App/Api/Properties/launchSettings.json's https profile
    changeOrigin: true,
    secure: false
  }
}

export default defineConfig({
  plugins: [vue()],
  server: { proxy: apiProxy },
  preview: { proxy: apiProxy }
})
