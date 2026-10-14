// D-227: the shared i18n test helper (Claude_Docs/Design_Localization.md).
//
// src/test/setup.js (vitest.config.js setupFiles) already installs the app's
// real i18n instance on every mount(), so a component test needs nothing
// extra -- mount(Component, { global: { plugins: [router] } }) works as is
// (don't add i18n to plugins again), and assertions use the real English
// text. Import from here only to:
//   - read a message in an assertion:             tt('common.save')
//   - reset the language after a test changes it: resetI18n()
import { i18n, DEFAULT_LOCALE } from '../i18n'
import { setFormatLocale } from '../utils/formatLocale'

export { i18n }

/** The English (current-locale) text of a key, for assertions. */
export function tt(key, ...args) {
  return i18n.global.t(key, ...args)
}

/** Back to English and the browser's number/date format. */
export function resetI18n() {
  i18n.global.locale.value = DEFAULT_LOCALE
  setFormatLocale(undefined)
  document.documentElement.setAttribute('lang', DEFAULT_LOCALE)
}
