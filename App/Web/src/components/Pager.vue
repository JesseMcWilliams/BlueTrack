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
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

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
  <!-- D-330: nothing to page through on a single page (each list shows it above and below the table); shown again if
       the page number is past the end, so Prev still gets back. The page size sits on the same line as its label. -->
  <p v-if="pageCount > 1 || page > 1" class="pager">
    <button type="button" :disabled="isFirstPage" @click="goPrev">{{ t('shell.pager.prev') }}</button>
    <span>{{ t('shell.pager.page', { page: formatCount(page), pageCount: formatCount(pageCount) }) }}</span>
    <button type="button" :disabled="isLastPage" @click="goNext">{{ t('shell.pager.next') }}</button>
    <label class="pager-size">
      <span>{{ t('shell.pager.rowsPerPage') }}</span>
      <select :value="pageSizeStore.current" @change="onPageSizeChange">
        <option v-for="size in PAGE_SIZE_OPTIONS" :key="size" :value="size">{{ formatCount(size) }}</option>
      </select>
    </label>
  </p>
</template>

<style scoped>
.pager {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem;
  margin: 0 0 var(--space-3, 0.75rem) 0;
}
.pager-size {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}
</style>
