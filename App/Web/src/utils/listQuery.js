// UI review W2-2, W7-2: list and report state (filters, sort, page) lives in the page address, so a filtered list
// survives opening a row and coming back, a reload, and a pasted link. These are the small pure helpers every page
// shares for reading it from, and writing it to, `route.query`.

/** The first value of a query key as text ('' when absent), since a repeated key arrives as an array. */
export function readText(query, key, fallback = '') {
  const value = Array.isArray(query?.[key]) ? query[key][0] : query?.[key]
  return typeof value === 'string' ? value : fallback
}

/** The value if it is one of `allowed`, otherwise `fallback`: an address someone edited by hand can't push an unknown value in. */
export function readOneOf(query, key, allowed, fallback) {
  const value = readText(query, key, null)
  return value !== null && allowed.includes(value) ? value : fallback
}

/** A whole number from `min` up, otherwise `fallback`. */
export function readInt(query, key, fallback, min = 1) {
  const text = readText(query, key, '')
  if (!/^\d{1,9}$/.test(text)) return fallback
  const value = Number(text)
  return value >= min ? value : fallback
}

/** A page number: 1 unless the address names a later one. */
export function readPage(query) {
  return readInt(query, 'page', 1)
}

/** A date as YYYY-MM-DD, otherwise `fallback`. */
export function readDate(query, key, fallback = '') {
  const text = readText(query, key, '')
  return /^\d{4}-\d\d-\d\d$/.test(text) && !Number.isNaN(Date.parse(text)) ? text : fallback
}

/** "field:asc,other:desc" (the sort the API takes) as [{ field, descending }], keeping only fields the page really has. */
export function parseSort(text, allowedFields) {
  if (!text) return []
  const seen = new Set()
  const columns = []
  for (const part of text.split(',')) {
    const [field, direction] = part.split(':')
    if (!allowedFields.includes(field) || seen.has(field)) continue
    seen.add(field)
    columns.push({ field, descending: direction === 'desc' })
  }
  return columns
}

export function formatSort(columns) {
  return columns.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(',')
}

/**
 * The address's query for the values the page holds: a value that is empty, or the page's default, is left out, so an
 * untouched page has a clean address.
 */
export function compactQuery(values, defaults = {}) {
  const query = {}
  for (const [key, value] of Object.entries(values)) {
    if (value === null || value === undefined || value === '') continue
    if (key in defaults && String(defaults[key]) === String(value)) continue
    query[key] = String(value)
  }
  return query
}

/** Whether two query objects say the same thing (key order and array-or-single don't matter). */
export function sameQuery(a, b) {
  const normalize = (query) => Object.fromEntries(
    Object.entries(query ?? {})
      .map(([key, value]) => [key, Array.isArray(value) ? String(value[0]) : String(value)])
      .filter(([, value]) => value !== '')
      .sort(([x], [y]) => x.localeCompare(y)))
  return JSON.stringify(normalize(a)) === JSON.stringify(normalize(b))
}
