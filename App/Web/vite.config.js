import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// D-21/D-37: Vue, kept light and fast, no forced build complexity beyond
// what Vite needs. Dev-server proxy points at the ASP.NET Core API so the
// two projects can run side by side without a CORS dance during development.
export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/api': {
        target: 'https://localhost:7033', // matches App/Api/Properties/launchSettings.json's https profile
        changeOrigin: true,
        secure: false
      }
    }
  }
})
