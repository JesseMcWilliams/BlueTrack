<script setup>
// Calls GET /api/risk-exceptions (RiskExceptionsController) -- D-42:
// stacked filters (status/scope type) plus multi-column sort, same pattern
// as AccountProgressList.vue.
import { ref, computed, watch, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAddressState } from '../composables/useAddressState'
import { useListStateStore } from '../stores/listState'
import { readText, readOneOf, readPage, parseSort, compactQuery } from '../utils/listQuery'
import { useRightsStore } from '../stores/rights'
import { formatDate } from '../utils/formatDate'
import { useTotalCount } from '../composables/useTotalCount'
import { useLatestRequest } from '../composables/useLatestRequest'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'
import SelectionBar from '../components/SelectionBar.vue'
import BulkFieldEditor from '../components/BulkFieldEditor.vue'
import BulkReasonForm from '../components/BulkReasonForm.vue'
import BulkResultSummary from '../components/BulkResultSummary.vue'
import { useListSelectionStore } from '../stores/listSelection'
import { useBulkAction } from '../composables/useBulkAction'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()
const listState = useListStateStore()
const rights = useRightsStore()
const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const latestRequest = useLatestRequest()
const pageSizeStore = usePageSizeStore()
const exceptions = ref([])
const error = ref(null)
const loading = ref(true)
const statusFilter = ref('')
const scopeTypeFilter = ref('')

// D-124 Phase 3: pagination -- page is local to this page (not persisted);
// pageSize comes from the shared, server-persisted pageSize store.
const page = ref(1)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))

function onPageChange(newPage) {
  page.value = newPage
  load()
}

function onPageSizeChange() {
  page.value = 1
  load()
}

const sortColumns = ref([])

const columns = [
  { field: 'exceptionID', labelKey: 'pages.riskExceptionsList.colExceptionId' },
  { field: 'scopeName', labelKey: 'pages.riskExceptionsList.colScope' },
  { field: 'approvedByName', labelKey: 'pages.riskExceptionsList.colApprovedBy' },
  { field: 'approvalDate', labelKey: 'pages.riskExceptionsList.colApprovalDate' },
  { field: 'reviewDate', labelKey: 'pages.riskExceptionsList.colReviewDate' },
  { field: 'statusName', labelKey: 'pages.riskExceptionsList.colStatus' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

// D-92 (ARIA APG Sortable Table pattern) -- see AccountProgressList.vue's
// identical helper for why only the primary sort key is ever reflected here.
function ariaSortFor(field) {
  if (sortColumns.value.length === 0 || sortColumns.value[0].field !== field) return 'none'
  return sortColumns.value[0].descending ? 'descending' : 'ascending'
}

function toggleSort(field, event) {
  const existingIndex = sortColumns.value.findIndex(s => s.field === field)

  if (!event.shiftKey) {
    if (existingIndex === 0 && sortColumns.value.length === 1) {
      sortColumns.value = [{ field, descending: !sortColumns.value[0].descending }]
    } else {
      sortColumns.value = [{ field, descending: false }]
    }
    return
  }

  if (existingIndex === -1) {
    sortColumns.value = [...sortColumns.value, { field, descending: false }]
  } else {
    const updated = [...sortColumns.value]
    updated[existingIndex] = { ...updated[existingIndex], descending: !updated[existingIndex].descending }
    sortColumns.value = updated
  }
}

const sortQueryParam = computed(() =>
  sortColumns.value.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(','))

async function load() {
  const request = latestRequest.begin()
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (statusFilter.value) params.set('status', statusFilter.value)
    if (scopeTypeFilter.value) params.set('scopeType', scopeTypeFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const response = await fetch(`/api/risk-exceptions?${params.toString()}`)
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    const body = await response.json()
    if (!latestRequest.isCurrent(request)) return
    readTotalCount(response)
    exceptions.value = body
  } catch (err) {
    if (latestRequest.isCurrent(request)) error.value = err.message
  } finally {
    if (latestRequest.isCurrent(request)) loading.value = false
  }
}

// UI review W4 (D-252's pattern): the filters, sort and page are in the page address, so a filtered list survives
// opening an exception and coming back, a reload and a pasted link.
const { isApplying } = useAddressState({
  sources: () => [statusFilter.value, scopeTypeFilter.value, sortQueryParam.value, page.value],
  apply(query) {
    statusFilter.value = readOneOf(query, 'status', ['Active', 'Expired', 'Revoked'], '')
    scopeTypeFilter.value = readOneOf(query, 'scopeType', ['Account', 'Application'], '')
    sortColumns.value = parseSort(readText(query, 'sort'), columns.map(c => c.field))
    page.value = readPage(query)
  },
  current: () => compactQuery({ status: statusFilter.value, scopeType: scopeTypeFilter.value, sort: sortQueryParam.value, page: page.value }, { page: 1 }),
  remember: (query) => listState.remember('risk-exceptions-list', query),
  onApplied: load
})

onMounted(load)

// D-190: bulk re-approve (new review date) and bulk revoke (reason
// required) through the shared selection bar, for ApproveExceptions.
const canApprove = computed(() => rights.hasPermission('ApproveExceptions'))
const selection = useListSelectionStore('riskExceptions')
const { saving: bulkSaving, result: bulkResult, error: bulkError, run: runBulk } = useBulkAction(selection)
const bulkPanel = ref(null) // 'extend' | 'revoke'
const newReviewDate = ref('')
const pageKeys = computed(() => exceptions.value.map(e => e.exceptionKey))
const keysUrl = computed(() => {
  const params = new URLSearchParams()
  if (statusFilter.value) params.set('status', statusFilter.value)
  if (scopeTypeFilter.value) params.set('scopeType', scopeTypeFilter.value)
  return `/api/risk-exceptions/keys?${params.toString()}`
})

async function bulkExtend() {
  if (await runBulk('/api/risk-exceptions/bulk-extend-review', { newReviewDate: newReviewDate.value }, t('pages.riskExceptionsList.verbReapproved'))) {
    bulkPanel.value = null
    await load()
  }
}

async function bulkRevoke(reason) {
  if (await runBulk('/api/risk-exceptions/bulk-revoke', { reason }, t('pages.riskExceptionsList.verbRevoked'))) {
    bulkPanel.value = null
    await load()
  }
}
// D-124 Phase 3: a filter/sort change resets to page 1 -- see Targets.vue's
// identical comment for why page-size/Prev/Next changes are handled separately.
watch([statusFilter, scopeTypeFilter, sortQueryParam], () => {
  if (isApplying()) return
  page.value = 1
  load()
})
</script>

<template>
  <div>
    <h1>{{ t('pages.riskExceptionsList.title') }}</h1>
    <p v-if="rights.hasPermission('ApproveExceptions')">
      <button type="button" class="btn-primary" @click="router.push({ name: 'risk-exception-create' })">{{ t('pages.riskExceptionsList.newException') }}</button>
      <button type="button" @click="router.push({ name: 'risk-exceptions-bulk-import' })">{{ t('pages.riskExceptionsList.bulkActions') }}</button>
    </p>
    <!-- UI review W3-6: the two worklists are reachable from here, not only from the Dashboard. -->
    <p>
      <router-link :to="{ name: 'risk-exceptions-overdue-worklist' }">{{ t('pages.riskExceptionsList.overdueLink') }}</router-link>
      <template v-if="rights.hasPermission('ApproveExceptions')">
        |
        <router-link :to="{ name: 'risk-exceptions-approval-worklist' }">{{ t('pages.riskExceptionsList.approvalLink') }}</router-link>
      </template>
    </p>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionsList.status') }}</span>
        <select v-model="statusFilter">
          <option value="">{{ t('pages.riskExceptionsList.all') }}</option>
          <option value="Active">{{ t('common.active') }}</option>
          <option value="Expired">{{ t('pages.riskExceptionsList.expired') }}</option>
          <option value="Revoked">{{ t('pages.riskExceptionsList.revoked') }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionsList.scope') }}</span>
        <select v-model="scopeTypeFilter">
          <option value="">{{ t('pages.riskExceptionsList.all') }}</option>
          <option value="Account">{{ t('pages.riskExceptionsList.account') }}</option>
          <option value="Application">{{ t('pages.riskExceptionsList.application') }}</option>
        </select>
      </label>
    </p>
    <template v-if="canApprove">
      <SelectionBar :selection="selection" :page-keys="pageKeys" :filtered-count="filteredCount" :keys-url="keysUrl" :toggle-label="t('pages.riskExceptionsList.selectToggle')">
        <button type="button" class="btn-primary" :disabled="selection.count === 0" @click="bulkPanel = 'extend'">{{ t('pages.riskExceptionsList.extendReviewEllipsis') }}</button>
        <button type="button" :disabled="selection.count === 0" @click="bulkPanel = 'revoke'">{{ t('pages.riskExceptionsList.revokeEllipsis') }}</button>
      </SelectionBar>
      <form v-if="bulkPanel === 'extend'" class="bulk-panel" @submit.prevent="bulkExtend">
        <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionsList.newReviewDate', { count: formatCount(selection.count) }) }}</span> <input v-model="newReviewDate" type="date" required /></label>
        <button type="submit" class="btn-primary" :disabled="bulkSaving || !newReviewDate">{{ t('pages.riskExceptionsList.extendReview') }}</button>
        <button type="button" @click="bulkPanel = null">{{ t('common.cancel') }}</button>
        <br /><small>{{ t('pages.riskExceptionsList.extendHint') }}</small>
      </form>
      <BulkReasonForm v-if="bulkPanel === 'revoke'" :action="t('pages.riskExceptionsList.revokeAction', { count: formatCount(selection.count) })" :button-label="t('pages.riskExceptionsList.revoke')" :saving="bulkSaving" @submit="bulkRevoke" @cancel="bulkPanel = null" />
      <p v-if="bulkError" role="alert">{{ bulkError }}</p>
      <BulkResultSummary v-if="bulkResult" :result="bulkResult" />
    </template>
    <FilterCountSummary :shown="exceptions.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('pages.riskExceptionsList.couldNotLoad', { error }) }}</p>
    <p v-else-if="exceptions.length === 0">{{ t('pages.riskExceptionsList.noneFound') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-if="canApprove && selection.enabled"><span class="visually-hidden">{{ t('pages.riskExceptionsList.selected') }}</span></th>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
          <th>{{ t('pages.riskExceptionsList.colJustification') }}</th>
          <th>{{ t('pages.riskExceptionsList.colSource') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="exception in exceptions" :key="exception.exceptionKey">
          <td v-if="canApprove && selection.enabled"><input type="checkbox" :checked="selection.isSelected(exception.exceptionKey)" :aria-label="t('pages.riskExceptionsList.selectRow', { id: exception.exceptionID })" @change="selection.toggle(exception.exceptionKey)" /></td>
          <td><router-link :to="{ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } }">{{ exception.exceptionID }}</router-link></td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.approvedByName }}</td>
          <td>{{ formatDate(exception.approvalDate) }}</td>
          <td>{{ formatDate(exception.reviewDate) }}</td>
          <td>{{ exception.statusName }}</td>
          <td>{{ exception.justification }}</td>
          <td>
            <template v-if="exception.sourceTool">
              <a v-if="exception.sourceUrl" :href="exception.sourceUrl" target="_blank" rel="noopener noreferrer">{{ exception.sourceTool }} {{ exception.sourceExceptionId }}</a>
              <template v-else>{{ exception.sourceTool }} {{ exception.sourceExceptionId }}</template>
            </template>
          </td>
        </tr>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>{{ t('pages.riskExceptionsList.sortHint') }}</small></p>
  </div>
</template>
