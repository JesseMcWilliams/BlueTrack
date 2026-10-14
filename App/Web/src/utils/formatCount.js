// D-218: counts shown on the pages use the number format of the viewer's
// browser language (1,234 / 1.234 / 1 234). D-227: the locale comes from
// utils/formatLocale.js -- the browser's own (Intl's default) unless the user
// explicitly picked a different language in Profile & Preferences. Blank for
// null/undefined/'' so an unknown count stays blank rather than showing 0.
import { numberFormat } from './formatLocale'

export function formatCount(value) {
  if (value === null || value === undefined || value === '') return ''
  const number = Number(value)
  return Number.isFinite(number) ? numberFormat().format(number) : String(value)
}
