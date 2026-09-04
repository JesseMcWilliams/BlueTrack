import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

// Layer 1 (Design_Testing_Strategy.md): isolated component/store logic,
// no backend, no browser. Kept separate from vite.config.js so the real
// app build config stays untouched by test-only concerns.
export default defineConfig({
  plugins: [vue()],
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.js']
  }
})
