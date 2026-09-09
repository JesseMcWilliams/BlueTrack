import { ref } from 'vue'

// D-121: the shared "Showing N of M total" filter-match-count summary,
// established fresh across all six pages that have filter UI (the two new
// Targets/Access Groups pages, plus the four existing D-42 pages
// retrofitted with it: AccountProgressList, RiskExceptionsList,
// AuditLogViewer, RiskScoreReport). The backend sets an X-Total-Count
// response header carrying the grand total row count under the same base
// "active" WHERE clause the list query already uses, computed via a
// separate lightweight COUNT(*) query that does NOT apply the caller's
// filter params -- the existing JSON body shape (a bare array) is
// unchanged. Call readTotalCount(response) right after each fetch,
// alongside the existing response.json() parse, and render the
// FilterCountSummary component (same directory tree, ../components) near
// the page's filter row using this composable's `totalCount` plus the
// page's own items.length.
export function useTotalCount() {
  const totalCount = ref(null)

  function readTotalCount(response) {
    const header = response.headers.get('X-Total-Count')
    totalCount.value = header === null ? null : Number(header)
  }

  return { totalCount, readTotalCount }
}
