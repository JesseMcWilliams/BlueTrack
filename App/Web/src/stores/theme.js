import { defineStore } from 'pinia'

// Theme preference (Design_Accessibility_And_Theming.md, D-93): 'Light',
// 'Dark', or 'HighVisibility' -- these exact strings are used as both the
// <html data-theme> attribute value (themes.css) and the server-side
// web.user_preference.PreferenceValue, so no mapping is needed between them.
const STORAGE_KEY = 'bluetrack:theme'
const VALID_THEMES = ['Light', 'Dark', 'HighVisibility']

function systemDefault() {
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'Dark' : 'Light'
}

function apply(theme) {
  document.documentElement.setAttribute('data-theme', theme)
}

export const useThemeStore = defineStore('theme', {
  state: () => ({
    current: 'Light'
  }),
  actions: {
    // Called once, as early as possible (main.js) -- applies a best-guess
    // theme (localStorage cache, else the OS's prefers-color-scheme)
    // immediately, before the server round trip in loadFromServer()
    // completes, so the correct theme paints without a flash of the wrong one.
    initBeforeServerLoad() {
      const cached = localStorage.getItem(STORAGE_KEY)
      this.current = VALID_THEMES.includes(cached) ? cached : systemDefault()
      apply(this.current)
    },
    // Called once /api/me's preferences are available (rights store) --
    // the server value is the source of truth once known; localStorage is
    // only ever the instant-apply cache set in initBeforeServerLoad/setTheme.
    loadFromServer(preferences) {
      const serverTheme = preferences?.Theme
      if (VALID_THEMES.includes(serverTheme) && serverTheme !== this.current) {
        this.current = serverTheme
        apply(serverTheme)
        localStorage.setItem(STORAGE_KEY, serverTheme)
      }
    },
    async setTheme(theme) {
      if (!VALID_THEMES.includes(theme)) return
      this.current = theme
      apply(theme)
      localStorage.setItem(STORAGE_KEY, theme)

      await fetch('/api/me/preferences/Theme', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: theme })
      })
    }
  }
})
