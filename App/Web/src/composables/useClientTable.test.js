import { describe, it, expect } from 'vitest'
import { ref } from 'vue'
import { useClientTable } from './useClientTable'

const rows = ref([
  { name: 'beta', safe: 'Zeta', days: 3 },
  { name: 'Alpha', safe: 'Eta', days: 10 },
  { name: 'gamma', safe: null, days: 1 },
  { name: 'delta', safe: 'Eta', days: null }
])

function table(extra = {}) {
  return useClientTable({
    rows,
    columns: { name: r => r.name, safe: r => r.safe, days: r => r.days },
    searchText: r => `${r.name} ${r.safe ?? ''}`,
    pageSize: () => 2,
    ...extra
  })
}

describe('useClientTable', () => {
  it('searches by contains, ignoring case, and counts what matches', () => {
    const t = table()
    t.search.value = 'ETA'
    expect(t.filtered.value.map(r => r.name)).toEqual(['beta', 'Alpha', 'delta']) // names or Safes containing "eta"
    expect(t.filteredCount.value).toBe(3)
  })

  it('sorts ascending then descending, ignoring case, with a missing value always last', () => {
    const t = table()
    t.toggleSort('name')
    expect(t.filtered.value.map(r => r.name)).toEqual(['Alpha', 'beta', 'delta', 'gamma'])
    t.toggleSort('name')
    expect(t.filtered.value.map(r => r.name)).toEqual(['gamma', 'delta', 'beta', 'Alpha'])

    t.toggleSort('days')
    expect(t.filtered.value.map(r => r.days)).toEqual([1, 3, 10, null])
    t.toggleSort('days')
    expect(t.filtered.value.map(r => r.days)).toEqual([10, 3, 1, null])
    expect(t.ariaSort('days')).toBe('descending')
    expect(t.ariaSort('name')).toBe('none')
    expect(t.indicator('days')).toBe('▼')
  })

  it('pages, keeps the page in range, and a new sort goes back to page 1', () => {
    const t = table()
    expect(t.pageCount.value).toBe(2)
    t.page.value = 2
    expect(t.pageRows.value).toHaveLength(2)
    t.page.value = 9
    expect(t.pageRows.value).toHaveLength(2) // clamped to the last page
    t.toggleSort('name')
    expect(t.page.value).toBe(1)
  })

  it('applies the extra filter, and reads and writes the sort as text', () => {
    const t = table({ extraFilter: r => r.safe === 'Eta' })
    expect(t.filtered.value.map(r => r.name)).toEqual(['Alpha', 'delta'])
    t.applySortText('days:desc')
    expect(t.sortText.value).toBe('days:desc')
    t.applySortText('nonsense:asc')
    expect(t.sortText.value).toBe('')
  })
})
