import { ref, computed } from 'vue'

/**
 * UI review W6-2 and W7-4: search, sort and paging for a report whose API sends every row in one go (the decommission
 * reports and the Overdue/At-Risk worklist), done in the page. The rows are not many enough to be worth a paged API.
 *
 * - `rows`: a ref (or getter) of the rows.
 * - `columns`: { field: row => value to sort by } (strings sort without regard to case, numbers as numbers, a missing
 *   value last).
 * - `searchText`: row => the text a search matches ("contains", ignoring case).
 * - `extraFilter`: optional row => boolean, for filters beyond the search box.
 * - `pageSize`: a getter for the current page size.
 */
export function useClientTable({ rows, columns, searchText, extraFilter = () => true, pageSize }) {
  const search = ref('')
  const sortField = ref('')
  const sortDescending = ref(false)
  const page = ref(1)

  const rowList = () => (typeof rows === 'function' ? rows() : rows.value)

  const filtered = computed(() => {
    const text = search.value.trim().toLowerCase()
    const kept = rowList().filter(row => extraFilter(row) && (!text || searchText(row).toLowerCase().includes(text)))
    const valueOf = columns[sortField.value]
    if (!valueOf) return kept
    const direction = sortDescending.value ? -1 : 1
    return [...kept].sort((a, b) => {
      const x = valueOf(a)
      const y = valueOf(b)
      const xMissing = isMissing(x)
      const yMissing = isMissing(y)
      if (xMissing || yMissing) return xMissing === yMissing ? 0 : xMissing ? 1 : -1 // a missing value is last either way
      return direction * compare(x, y)
    })
  })

  const filteredCount = computed(() => filtered.value.length)
  const pageCount = computed(() => Math.max(1, Math.ceil(filteredCount.value / pageSize())))
  const pageRows = computed(() => {
    const size = pageSize()
    const current = Math.min(page.value, pageCount.value)
    return filtered.value.slice((current - 1) * size, current * size)
  })

  /** Click on a column: sorts by it ascending, then descending. A new sort goes back to page 1. */
  function toggleSort(field) {
    if (sortField.value === field) {
      sortDescending.value = !sortDescending.value
    } else {
      sortField.value = field
      sortDescending.value = false
    }
    page.value = 1
  }

  const ariaSort = field => (sortField.value !== field ? 'none' : sortDescending.value ? 'descending' : 'ascending')
  const indicator = field => (sortField.value !== field ? '' : sortDescending.value ? '▼' : '▲')

  /** "field:asc" for the page address, and back. */
  const sortText = computed(() => (sortField.value ? `${sortField.value}:${sortDescending.value ? 'desc' : 'asc'}` : ''))
  function applySortText(text) {
    const [field, direction] = String(text ?? '').split(':')
    if (columns[field]) {
      sortField.value = field
      sortDescending.value = direction === 'desc'
    } else {
      sortField.value = ''
      sortDescending.value = false
    }
  }

  return { search, sortField, sortDescending, page, filtered, filteredCount, pageCount, pageRows, toggleSort, ariaSort, indicator, sortText, applySortText }
}

function isMissing(value) {
  return value === null || value === undefined || value === ''
}

function compare(a, b) {
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return String(a).localeCompare(String(b), undefined, { sensitivity: 'base', numeric: true })
}
