<script setup>
// D-124 Phase 3: the shared Prev/Next + page-size control for the six
// paginated list pages (Targets/AccessGroups/AccountProgressList/
// RiskExceptionsList/AuditLogViewer/RiskScoreReport). The current page
// number is owned by the parent page (a plain ref, reset to 1 whenever a
// filter/sort changes) -- this component only renders/enables the
// Prev/Next buttons and the page-size <select>, which is bound to the
// shared pageSize store (src/stores/pageSize.js) so a change here persists
// server-side (same web.user_preference mechanism Theme already uses) and
// applies identically the next time any of the six pages loads.
import { computed } from 'vue'
import { usePageSizeStore, PAGE_SIZE_OPTIONS } from '../stores/pageSize'

const props = defineProps({
  page: { type: Number, required: true },
  // At least 1 even when there are zero matching rows, so "Page 1 of 1"
  // reads sensibly instead of "Page 1 of 0".
  pageCount: { type: Number, default: 1 }
})
const emit = defineEmits(['update:page', 'pageSizeChange'])

const pageSizeStore = usePageSizeStore()

const isFirstPage = computed(() => props.page <= 1)
const isLastPage = computed(() => props.page >= props.pageCount)

function goPrev() {
  if (!isFirstPage.value) emit('update:page', props.page - 1)
}
function goNext() {
  if (!isLastPage.value) emit('update:page', props.page + 1)
}

// Changing the page size resets to page 1 (D-124 brief) -- the parent
// listens for pageSizeChange and does that reset itself, alongside
// reloading with the new pageSize query param.
async function onPageSizeChange(event) {
  await pageSizeStore.setPageSize(event.target.value)
  emit('pageSizeChange')
}
</script>

<template>
  <p class="pager">
    <button type="button" :disabled="isFirstPage" @click="goPrev">&laquo; Prev</button>
    <span>Page {{ page }} of {{ pageCount }}</span>
    <button type="button" :disabled="isLastPage" @click="goNext">Next &raquo;</button>
    <label class="field-label">
      <span class="field-label-text">Rows per page:</span>
      <select :value="pageSizeStore.current" @change="onPageSizeChange">
        <option v-for="size in PAGE_SIZE_OPTIONS" :key="size" :value="size">{{ size }}</option>
      </select>
    </label>
  </p>
</template>

<style scoped>
.pager {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0 0 var(--space-3, 0.75rem) 0;
}
</style>
