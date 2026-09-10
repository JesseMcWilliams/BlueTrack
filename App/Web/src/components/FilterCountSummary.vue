<script setup>
// D-121: the shared rendering half of the app-wide "Showing N of M total"
// filter-match-count summary -- paired with the useTotalCount composable
// (src/composables/useTotalCount.js), which captures the backend's
// X-Total-Count header. Kept as one component so the wording/markup isn't
// copy-pasted six times across Targets/AccessGroups/AccountProgressList/
// RiskExceptionsList/AuditLogViewer/RiskScoreReport with six chances to drift.
//
// D-124 Phase 3: extended for pagination. A page's JSON body is now only
// ever one page's worth of rows, so `shown` (items.length) can no longer
// stand in for "how many rows match the current filter" the way it used
// to -- that's now `filteredCount` (from the new X-Filtered-Count header,
// ignoring paging), distinct from `total` (X-Total-Count's unfiltered
// grand total, unchanged in meaning). `page`/`pageSize` compute the
// current page's own 1-based row range.
defineProps({
  // Rows actually returned on the current page (typically the loaded
  // list's own .length) -- used only to compute the end of the row range,
  // since the last page is usually a partial page.
  shown: { type: Number, required: true },
  // How many rows match the current filter, ignoring paging (from
  // X-Filtered-Count); null before the first load resolves, or if the
  // backend didn't send the header for some reason.
  filteredCount: { type: Number, default: null },
  // The unfiltered grand total (from X-Total-Count); null before the first
  // load resolves, or if the backend didn't send the header for some reason.
  total: { type: Number, default: null },
  page: { type: Number, default: 1 },
  pageSize: { type: Number, default: 50 }
})

function rowRange(page, pageSize, shown, filteredCount) {
  if (!filteredCount) return { start: 0, end: 0 }
  const start = (page - 1) * pageSize + 1
  const end = start + shown - 1
  return { start, end }
}
</script>

<template>
  <p v-if="filteredCount !== null && total !== null" class="filter-count-summary" role="status">
    Showing {{ rowRange(page, pageSize, shown, filteredCount).start }}&ndash;{{ rowRange(page, pageSize, shown, filteredCount).end }}
    of {{ filteredCount }} matching ({{ total }} total)
  </p>
</template>

<style scoped>
.filter-count-summary {
  margin: 0 0 var(--space-3) 0;
  color: var(--color-text-secondary, inherit);
  font-size: 0.875rem;
}
</style>
