// D-227: the locale numbers, dates and percentages are formatted in.
// `undefined` (the default) means the browser's own language -- exactly what
// D-218's formatCount did before localization, so nothing changes for a user
// who never picks a language. The locale store (stores/locale.js) sets it to
// the chosen language only when the user explicitly picks one that differs
// from the browser's language (a British browser that picks English keeps
// en-GB conventions).
let formatLocale

export function getFormatLocale() {
  return formatLocale
}

export function setFormatLocale(locale) {
  formatLocale = locale || undefined
}

// Intl formatters are relatively costly to build; one per locale + options.
const cache = new Map()

function formatter(kind, options) {
  const key = `${kind}|${formatLocale ?? ''}|${JSON.stringify(options)}`
  let value = cache.get(key)
  if (!value) {
    value = kind === 'number'
      ? new Intl.NumberFormat(formatLocale, options)
      : new Intl.DateTimeFormat(formatLocale, options)
    cache.set(key, value)
  }
  return value
}

export function numberFormat(options = {}) {
  return formatter('number', options)
}

export function dateTimeFormat(options = {}) {
  return formatter('date', options)
}

// A fraction (0.125) as a percentage: "12.5%" (up to one decimal place).
export function formatPercent(value) {
  if (value === null || value === undefined || value === '') return ''
  const number = Number(value)
  return Number.isFinite(number)
    ? numberFormat({ style: 'percent', minimumFractionDigits: 0, maximumFractionDigits: 1 }).format(number)
    : String(value)
}

// A date with a short month name: "13 Oct 2026" / "Oct 13, 2026".
export function formatShortDate(value) {
  if (!value) return ''
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime())
    ? String(value)
    : dateTimeFormat({ year: 'numeric', month: 'short', day: 'numeric' }).format(date)
}

// A date and time the way Date.prototype.toLocaleString() shows it.
export function formatDateTime(value) {
  if (!value) return ''
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString(formatLocale)
}

// A time the API sends without a zone (SQL datetime2, always UTC), shown as a local date and time. A value that
// already has a zone is used as it is.
export function formatUtcDateTime(value) {
  if (!value) return ''
  const zoneless = typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d/.test(value) && !/[zZ]|[+-]\d\d:\d\d$/.test(value)
  const text = zoneless ? `${value}Z` : value
  return formatDateTime(text)
}
