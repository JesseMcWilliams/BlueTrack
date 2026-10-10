import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './assets/themes.css'
import App from './App.vue'
import router from './router'
import { useThemeStore } from './stores/theme'
import { installApiFetch } from './utils/apiBase'

// D-196: in production every fetch('/api/...') goes to /BlueTrack/api/...
installApiFetch()

const pinia = createPinia()

// Applied before mount, from localStorage/prefers-color-scheme, so the
// correct theme paints immediately -- rights.js's load() (App.vue's
// onMounted) overrides this with the server value once /api/me resolves.
useThemeStore(pinia).initBeforeServerLoad()

createApp(App)
  .use(pinia)
  .use(router)
  .mount('#app')
