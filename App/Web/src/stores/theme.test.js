import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useThemeStore } from './theme'

function mockMatchMedia(prefersDark) {
  window.matchMedia = vi.fn().mockReturnValue({ matches: prefersDark })
}

// Node 26's own experimental global `localStorage` shadows happy-dom's
// window.localStorage (confirmed directly: both were undefined here without
// this, with Node logging "localStorage is not available because
// --localstorage-file was not provided") -- stub a plain in-memory Storage
// so theme.js's calls have something real to read/write during a test.
function stubLocalStorage() {
  const store = new Map()
  vi.stubGlobal('localStorage', {
    getItem: (key) => (store.has(key) ? store.get(key) : null),
    setItem: (key, value) => store.set(key, String(value)),
    removeItem: (key) => store.delete(key),
    clear: () => store.clear()
  })
}

describe('theme store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    stubLocalStorage()
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true })
    mockMatchMedia(false)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    document.documentElement.removeAttribute('data-theme')
  })

  describe('initBeforeServerLoad', () => {
    it('applies the localStorage value when present', () => {
      localStorage.setItem('bluetrack:theme', 'Dark')
      const store = useThemeStore()

      store.initBeforeServerLoad()

      expect(store.current).toBe('Dark')
      expect(document.documentElement.getAttribute('data-theme')).toBe('Dark')
    })

    it('ignores an invalid localStorage value and falls back to system default', () => {
      localStorage.setItem('bluetrack:theme', 'NotARealTheme')
      mockMatchMedia(true)
      const store = useThemeStore()

      store.initBeforeServerLoad()

      expect(store.current).toBe('Dark')
    })

    it('falls back to prefers-color-scheme when nothing is cached', () => {
      mockMatchMedia(false)
      const store = useThemeStore()

      store.initBeforeServerLoad()

      expect(store.current).toBe('Light')
    })
  })

  describe('loadFromServer', () => {
    it('applies a valid server-provided theme', () => {
      const store = useThemeStore()

      store.loadFromServer({ Theme: 'HighVisibility' })

      expect(store.current).toBe('HighVisibility')
      expect(document.documentElement.getAttribute('data-theme')).toBe('HighVisibility')
      expect(localStorage.getItem('bluetrack:theme')).toBe('HighVisibility')
    })

    it('does nothing when preferences are absent (no explicit choice made yet)', () => {
      const store = useThemeStore()
      store.current = 'Light'

      store.loadFromServer(undefined)

      expect(store.current).toBe('Light')
    })

    it('ignores an unrecognized theme value', () => {
      const store = useThemeStore()
      store.current = 'Light'

      store.loadFromServer({ Theme: 'Sepia' })

      expect(store.current).toBe('Light')
    })
  })

  describe('setTheme', () => {
    it('applies the theme, persists it locally, and PUTs it to the server', async () => {
      const store = useThemeStore()

      await store.setTheme('Dark')

      expect(store.current).toBe('Dark')
      expect(document.documentElement.getAttribute('data-theme')).toBe('Dark')
      expect(localStorage.getItem('bluetrack:theme')).toBe('Dark')
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/Theme', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: 'Dark' })
      })
    })

    it('ignores an unrecognized theme value', async () => {
      const store = useThemeStore()
      store.current = 'Light'

      await store.setTheme('Sepia')

      expect(store.current).toBe('Light')
      expect(globalThis.fetch).not.toHaveBeenCalled()
    })
  })
})
