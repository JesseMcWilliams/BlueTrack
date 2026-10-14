// D-218: counts shown on the pages use the number format of the viewer's
// browser language (1,234 / 1.234 / 1 234). Intl.NumberFormat with no locale
// uses the browser's own. Blank for null/undefined/'' so an unknown count
// stays blank rather than showing 0.
const formatter = new Intl.NumberFormat()

export function formatCount(value) {
  if (value === null || value === undefined || value === '') return ''
  const number = Number(value)
  return Number.isFinite(number) ? formatter.format(number) : String(value)
}
