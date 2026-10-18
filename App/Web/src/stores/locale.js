import { defineStore } from 'pinia'
import { i18n, DEFAULT_LOCALE, SUPPORTED_LOCALES } from '../i18n'
import { setFormatLocale } from '../utils/formatLocale'

// D-227: the UI language. Mirrors the theme store (D-93): the server value
// (web.user_preference, key 'Language') is the source of truth once /api/me
// returns; localStorage caches the explicit choice so it applies before that
// round trip. With no choice, the language is the first of the browser's
// languages (navigator.languages) that has messages, else English.
const STORAGE_KEY = 'bluetrack:language'
export const PREFERENCE_KEY = 'Language'

function browserLanguages() {
  const list = navigator.languages?.length ? navigator.languages : [navigator.language]
  return list.filter(Boolean)
}

function primary(tag) {
  return String(tag).split('-')[0].toLowerCase()
}

/** The best supported locale for a list of BCP 47 tags, else English. */
export function matchLocale(tags, supported = SUPPORTED_LOCALES) {
  for (const tag of tags) {
    if (supported.includes(tag)) return tag
    const base = primary(tag)
    if (supported.includes(base)) return base
  }
  return DEFAULT_LOCALE
}

/**
 * Numbers and dates keep the browser's own format (D-218: Intl's default)
 * unless the user explicitly chose a language other than the browser's.
 */
export function formatLocaleFor(code, explicit) {
  if (!explicit) return undefined
  const browser = browserLanguages()[0]
  return browser && primary(browser) === primary(code) ? undefined : code
}

function apply(code, explicit) {
  i18n.global.locale.value = code
  document.documentElement.setAttribute('lang', code)
  setFormatLocale(formatLocaleFor(code, explicit))
}

function readCache() {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

function writeCache(code) {
  try {
    localStorage.setItem(STORAGE_KEY, code)
  } catch {
    // Storage blocked: the server value still applies on the next load.
  }
}

export const useLocaleStore = defineStore('locale', {
  state: () => ({
    current: DEFAULT_LOCALE,
    // true once the user has picked a language (here or on another device).
    explicit: false
  }),
  getters: {
    available: () => SUPPORTED_LOCALES
  },
  actions: {
    // main.js, before mount: the cached choice, else the browser's language.
    initBeforeServerLoad() {
      const cached = readCache()
      if (SUPPORTED_LOCALES.includes(cached)) {
        this.current = cached
        this.explicit = true
      } else {
        this.current = matchLocale(browserLanguages())
        this.explicit = false
      }
      apply(this.current, this.explicit)
    },
    // rights store, once /api/me's preferences are known.
    loadFromServer(preferences) {
      const serverValue = preferences?.[PREFERENCE_KEY]
      if (!SUPPORTED_LOCALES.includes(serverValue)) {
        // The server is the record: a choice cached here that the server no longer has (cleared on another device)
        // goes back to the browser's language.
        if (preferences && this.explicit) this.followBrowser(true)
        return
      }
      this.current = serverValue
      this.explicit = true
      apply(serverValue, true)
      writeCache(serverValue)
    },
    // "Use browser default": drop the saved choice here and on the server, and follow the browser's language again.
    async useBrowserDefault() {
      this.followBrowser(true)
      await fetch(`/api/me/preferences/${PREFERENCE_KEY}`, { method: 'DELETE' })
    },
    followBrowser(clearCache) {
      if (clearCache) {
        try {
          localStorage.removeItem(STORAGE_KEY)
        } catch {
          // Storage blocked: the server value is cleared anyway.
        }
      }
      this.current = matchLocale(browserLanguages())
      this.explicit = false
      apply(this.current, false)
    },
    async setLanguage(code) {
      if (!SUPPORTED_LOCALES.includes(code)) return
      this.current = code
      this.explicit = true
      apply(code, true)
      writeCache(code)

      await fetch(`/api/me/preferences/${PREFERENCE_KEY}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: code })
      })
    }
  }
})

/** The language's own name for itself ("English", "Deutsch"). */
export function languageName(code) {
  try {
    const name = new Intl.DisplayNames([code], { type: 'language' }).of(code)
    return name ? name.charAt(0).toLocaleUpperCase(code) + name.slice(1) : code
  } catch {
    return code
  }
}
