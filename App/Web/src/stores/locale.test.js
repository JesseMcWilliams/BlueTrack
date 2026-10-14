import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useLocaleStore, matchLocale, formatLocaleFor, languageName } from './locale'
import { useRightsStore } from './rights'
import { i18n } from '../i18n'
import { getFormatLocale, setFormatLocale, formatPercent, formatShortDate } from '../utils/formatLocale'
import { formatCount } from '../utils/formatCount'
import MyProfile from '../views/MyProfile.vue'

// D-227: UI language -- browser default, server-side preference (Language),
// <html lang>, and the format locale for counts/dates/percentages.

// See theme.test.js: Node's own global localStorage shadows happy-dom's.
function stubLocalStorage() {
  const store = new Map()
  vi.stubGlobal('localStorage', {
    getItem: (key) => (store.has(key) ? store.get(key) : null),
    setItem: (key, value) => store.set(key, String(value)),
    removeItem: (key) => store.delete(key),
    clear: () => store.clear()
  })
}

function stubBrowserLanguages(languages) {
  vi.stubGlobal('navigator', { ...navigator, language: languages[0], languages })
}

describe('locale store (D-227)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    stubLocalStorage()
    stubBrowserLanguages(['en-US', 'en'])
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true })
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  describe('matchLocale', () => {
    it('takes the first browser language that has messages, by exact tag or primary language', () => {
      expect(matchLocale(['fr-CA', 'de-DE', 'en-GB'], ['en', 'de'])).toBe('de')
      expect(matchLocale(['pt-BR'], ['en', 'pt-BR'])).toBe('pt-BR')
    })

    it('falls back to English', () => {
      expect(matchLocale(['ja-JP', 'ko'], ['en'])).toBe('en')
      expect(matchLocale([], ['en'])).toBe('en')
    })
  })

  describe('initBeforeServerLoad', () => {
    it('uses the browser language and sets <html lang>', () => {
      document.documentElement.setAttribute('lang', 'xx')
      const store = useLocaleStore()
      store.initBeforeServerLoad()
      expect(store.current).toBe('en')
      expect(store.explicit).toBe(false)
      expect(i18n.global.locale.value).toBe('en')
      expect(document.documentElement.getAttribute('lang')).toBe('en')
    })

    it('falls back to English for a browser language with no messages, keeping its number format', () => {
      stubBrowserLanguages(['de-DE'])
      const store = useLocaleStore()
      store.initBeforeServerLoad()
      expect(store.current).toBe('en')
      // No explicit choice: counts keep the browser's own format (D-218).
      expect(getFormatLocale()).toBeUndefined()
    })

    it('applies a cached explicit choice', () => {
      localStorage.setItem('bluetrack:language', 'en')
      const store = useLocaleStore()
      store.initBeforeServerLoad()
      expect(store.explicit).toBe(true)
    })

    it('ignores a cached language that is not registered', () => {
      localStorage.setItem('bluetrack:language', 'xx')
      const store = useLocaleStore()
      store.initBeforeServerLoad()
      expect(store.current).toBe('en')
      expect(store.explicit).toBe(false)
    })
  })

  describe('server preference', () => {
    it('applies a registered Language preference from /api/me', () => {
      const store = useLocaleStore()
      store.loadFromServer({ Theme: 'Dark', Language: 'en' })
      expect(store.current).toBe('en')
      expect(store.explicit).toBe(true)
      expect(localStorage.getItem('bluetrack:language')).toBe('en')
    })

    it('ignores a missing or unregistered Language preference', () => {
      const store = useLocaleStore()
      store.loadFromServer({ Language: 'xx' })
      store.loadFromServer({})
      store.loadFromServer(undefined)
      expect(store.explicit).toBe(false)
    })

    it('is applied by the rights store when /api/me loads', async () => {
      globalThis.fetch = vi.fn(async (url) => url === '/api/auth/session'
        ? { ok: true, json: async () => ({ authenticated: true }) }
        : { ok: true, status: 200, json: async () => ({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [], preferences: { Language: 'en' } }) })
      await useRightsStore().load()
      expect(useLocaleStore().explicit).toBe(true)
    })

    it('setLanguage saves the choice through the generalized preference endpoint', async () => {
      const store = useLocaleStore()
      await store.setLanguage('en')
      expect(fetch).toHaveBeenCalledWith('/api/me/preferences/Language', expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ value: 'en' })
      }))
      expect(document.documentElement.getAttribute('lang')).toBe('en')
    })

    it('setLanguage ignores an unregistered language', async () => {
      const store = useLocaleStore()
      await store.setLanguage('xx')
      expect(fetch).not.toHaveBeenCalled()
      expect(store.current).toBe('en')
    })
  })

  describe('format locale', () => {
    it('stays the browser default unless the user chose a different language', () => {
      stubBrowserLanguages(['de-DE'])
      expect(formatLocaleFor('en', false)).toBeUndefined()
      expect(formatLocaleFor('en', true)).toBe('en')
      expect(formatLocaleFor('de', true)).toBeUndefined()
      stubBrowserLanguages(['en-GB'])
      expect(formatLocaleFor('en', true)).toBeUndefined()
    })

    it('formatCount, formatPercent and formatShortDate use it', () => {
      setFormatLocale('de-DE')
      expect(formatCount(1234567)).toBe('1.234.567')
      expect(formatPercent(0.125)).toBe(new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 1 }).format(0.125))
      expect(formatShortDate(new Date(2026, 9, 13))).toBe(new Intl.DateTimeFormat('de-DE', { year: 'numeric', month: 'short', day: 'numeric' }).format(new Date(2026, 9, 13)))
      setFormatLocale('en-US')
      expect(formatCount(1234567)).toBe('1,234,567')
      setFormatLocale(undefined)
      expect(formatCount(1234567)).toBe(new Intl.NumberFormat().format(1234567))
    })
  })

  it('names a language in its own language', () => {
    expect(languageName('en')).toBe('English')
    expect(languageName('de')).toBe('Deutsch')
  })
})

describe('Profile & Preferences language picker (D-227)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    stubLocalStorage()
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true })
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('lists only the registered languages (English) and saves a choice', async () => {
    useRightsStore().$patch({ loaded: true, authenticated: true, displayName: 'A', roleNames: [], permissionNames: [] })
    const wrapper = mount(MyProfile)
    await flushPromises()

    const fieldset = wrapper.findAll('fieldset').find(f => f.find('legend').text() === 'Choose a display language')
    expect(fieldset).toBeTruthy()
    const radios = fieldset.findAll('input[type="radio"]')
    expect(radios.map(r => r.element.value)).toEqual(['en'])
    expect(fieldset.text()).toContain('English')
    expect(radios[0].element.checked).toBe(true)

    await radios[0].trigger('change')
    expect(fetch).toHaveBeenCalledWith('/api/me/preferences/Language', expect.objectContaining({ method: 'PUT' }))
  })
})
