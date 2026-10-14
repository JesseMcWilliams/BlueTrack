// D-227: the app's one vue-i18n instance (Composition API mode). Conventions
// for keys, message files and syntax: Claude_Docs/Design_Localization.md.
//
// Messages live in src/locales/<locale>/<area>.json -- one file per area
// (common, shell, admin, reports, pages), each holding only that area's keys
// without the area prefix. They're merged here as { <area>: {...} }, so the
// key in code is always '<area>.<page-or-component>.<element>'.
//
// CSP (Deploy/Templates/site-web.config.template: script-src 'self', no
// 'unsafe-eval'): vue-i18n 10+ compiles messages "JIT" -- to an AST that is
// interpreted, never eval/new Function -- so the full build works under the
// site's CSP with no build-time precompile plugin. The feature flags are set
// in vite.config.js / vitest.config.js.
import { createI18n } from 'vue-i18n'

export const DEFAULT_LOCALE = 'en'

// Registered languages. A locale folder becomes selectable only once listed
// here -- after every area file is complete (the key-coverage test checks
// that) and a native speaker has reviewed it.
export const SUPPORTED_LOCALES = ['en']

const modules = import.meta.glob('../locales/*/*.json', { eager: true, import: 'default' })

/** { '../locales/en/common.json': {...} } -> { en: { common: {...} } } */
export function buildMessages(files) {
  const messages = {}
  for (const [path, content] of Object.entries(files)) {
    const match = /\/locales\/([^/]+)\/([^/]+)\.json$/.exec(path)
    if (!match) continue
    const [, locale, area] = match
    messages[locale] ??= {}
    messages[locale][area] = content
  }
  return messages
}

export const allMessages = buildMessages(modules)

const messages = Object.fromEntries(SUPPORTED_LOCALES.map(code => [code, allMessages[code] ?? {}]))

export const i18n = createI18n({
  legacy: false,
  locale: DEFAULT_LOCALE,
  fallbackLocale: DEFAULT_LOCALE,
  messages
})

/** Shorthand for code outside components (stores, composables, utils). */
export function t(...args) {
  return i18n.global.t(...args)
}
