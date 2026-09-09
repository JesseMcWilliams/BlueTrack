<script setup>
// D-121: the shared rendering half of the app-wide "Showing N of M total"
// filter-match-count summary -- paired with the useTotalCount composable
// (src/composables/useTotalCount.js), which captures the backend's
// X-Total-Count header. Kept as one component so the wording/markup isn't
// copy-pasted six times across Targets/AccessGroups/AccountProgressList/
// RiskExceptionsList/AuditLogViewer/RiskScoreReport with six chances to drift.
defineProps({
  // Rows currently matching the active filter(s) -- typically the loaded
  // list's own .length.
  shown: { type: Number, required: true },
  // The unfiltered grand total (from X-Total-Count); null before the first
  // load resolves, or if the backend didn't send the header for some reason.
  total: { type: Number, default: null }
})
</script>

<template>
  <p v-if="total !== null" class="filter-count-summary" role="status">Showing {{ shown }} of {{ total }} total</p>
</template>

<style scoped>
.filter-count-summary {
  margin: 0 0 var(--space-3) 0;
  color: var(--color-text-secondary, inherit);
  font-size: 0.875rem;
}
</style>
