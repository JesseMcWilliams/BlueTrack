import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// Layer 1 (Design_Testing-Strategy.md): isolated component/store logic,
// no backend, no browser. Kept separate from vite.config.js so the real
// app build config stays untouched by test-only concerns.
export default defineConfig({
  plugins: [vue()],
  // D-227: vue-i18n's esm-bundler feature flags (boolean literals). Composition
  // API only; the JIT message compiler stays in (no precompile plugin), and it
  // never uses eval, so the site's CSP (script-src 'self') is unchanged.
  define: {
    __VUE_I18N_FULL_INSTALL__: true,
    __VUE_I18N_LEGACY_API__: false,
    __INTLIFY_DROP_MESSAGE_COMPILER__: false,
    __INTLIFY_PROD_DEVTOOLS__: false
  },
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.js'],
    // D-227: installs the app's i18n plugin on every mount (src/test/i18n.js).
    setupFiles: ['src/test/setup.js']
  }
})
