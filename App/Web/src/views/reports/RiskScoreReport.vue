<script setup>
// D-101-105 Phase E: gated by ViewRiskReport. Sortable table shape mirrors
// AccountProgressList.vue's own multi-column sort (click to sort, shift-click
// to add a secondary key) since sorting/filtering by score is the whole
// point of this report. Clicking a row expands a drill-down showing the
// real Targets/Access Groups contributing to that Account's score
// (GET /api/reports/risk-score/{accountKey}/contributors).
import { ref, computed, onMounted } from 'vue'
import { useAddressState } from '../../composables/useAddressState'
import { readText, readPage, parseSort, compactQuery } from '../../utils/listQuery'
import { formatDate } from '../../utils/formatDate'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import { useRightsStore } from '../../stores/rights'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import SelectionBar from '../../components/SelectionBar.vue'
import { useListSelectionStore } from '../../stores/listSelection'
import { problemMessage } from '../../utils/problemText'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

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

const rows = ref([])
const error = ref(null)
const loading = ref(true)
const recalculating = ref(false)
const recalculateError = ref(null)
// D-198: recalculating changes stored scores, so it needs ManageTargets, not just ViewRiskReport.
const rights = useRightsStore()
const recalculated = ref(false)

// D-315: set or clear the override on many accounts at once, for holders of EditAccountProgress (as on one account).
const canOverride = computed(() => rights.hasPermission('EditAccountProgress'))
const selection = useListSelectionStore('risk-score')
const pageKeys = computed(() => rows.value.map(r => r.accountKey))
const bulk = ref(null)   // { action: 'set' | 'clear', score, reason }
const bulkSaving = ref(false)
const bulkError = ref(null)
const bulkResult = ref(null)

function startBulk(action) {
  bulk.value = { action, score: '', reason: '' }
  bulkError.value = null
  bulkResult.value = null
}

async function applyBulk() {
  bulkSaving.value = true
  bulkError.value = null
  try {
    const setting = bulk.value.action === 'set'
    const response = await fetch('/api/account-progress/risk-score-override/bulk', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ accountKeys: selection.keys, overrideRiskScore: setting ? Number(bulk.value.score) : null, reason: bulk.value.reason.trim() || null })
    })
    if (!response.ok) throw new Error(await problemMessage(response, t('common.requestFailed', { status: response.status })))
    bulkResult.value = await response.json()
    bulk.value = null
    selection.clear()
    await load()
  } catch (err) {
    bulkError.value = err.message
  } finally {
    bulkSaving.value = false
  }
}

const expandedAccountKey = ref(null)
const contributors = ref([])
const contributorsLoading = ref(false)
const contributorsError = ref(null)

const sortColumns = ref([])

const columns = [
  { field: 'accountName', labelKey: 'reports.riskScoreReport.colAccount' },
  { field: 'computedRiskScore', labelKey: 'reports.riskScoreReport.colComputed' },
  { field: 'overrideRiskScore', labelKey: 'reports.riskScoreReport.colOverride' },
  { field: 'effectiveRiskScore', labelKey: 'reports.riskScoreReport.colEffective' },
  { field: 'riskScoreBandName', labelKey: 'reports.riskScoreReport.colRiskBand' },
  { field: 'isRiskScoreStale', labelKey: 'reports.riskScoreReport.colStale' },
  { field: 'riskScoreCalculatedDate', labelKey: 'reports.riskScoreReport.colLastCalculated' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

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
      sortColumns.value = [{ field, descending: field !== 'accountName' }]
    }
    // D-124 Phase 3: a sort change resets to page 1 -- see Targets.vue's
    // identical comment for why page-size/Prev/Next changes are handled separately.
    page.value = 1
    load()
    return
  }

  if (existingIndex === -1) {
    sortColumns.value = [...sortColumns.value, { field, descending: false }]
  } else {
    const updated = [...sortColumns.value]
    updated[existingIndex] = { ...updated[existingIndex], descending: !updated[existingIndex].descending }
    sortColumns.value = updated
  }
  page.value = 1
  load()
}

const sortQueryParam = computed(() =>
  sortColumns.value.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(','))

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)
    const response = await fetch(`/api/reports/risk-score?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

async function toggleDrilldown(accountKey) {
  if (expandedAccountKey.value === accountKey) {
    expandedAccountKey.value = null
    return
  }

  expandedAccountKey.value = accountKey
  contributors.value = []
  contributorsError.value = null
  contributorsLoading.value = true
  try {
    const response = await fetch(`/api/reports/risk-score/${accountKey}/contributors`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    contributors.value = await response.json()
  } catch (err) {
    contributorsError.value = err.message
  } finally {
    contributorsLoading.value = false
  }
}

async function recalculateNow() {
  recalculating.value = true
  recalculateError.value = null
  recalculated.value = false
  try {
    const response = await fetch('/api/reports/risk-score/recalculate', { method: 'POST' })
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    recalculated.value = true
    await load()
  } catch (err) {
    recalculateError.value = err.message
  } finally {
    recalculating.value = false
  }
}

// UI review W7-2: the sort and page are in the page address, so a view can be shared and reloaded.
useAddressState({
  sources: () => [sortQueryParam.value, page.value],
  apply(query) {
    sortColumns.value = parseSort(readText(query, 'sort'), columns.map(c => c.field))
    page.value = readPage(query)
  },
  current: () => compactQuery({ sort: sortQueryParam.value, page: page.value }, { page: 1 }),
  onApplied: load
})

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('reports.riskScoreReport.title') }}</h2>
    <p class="hint">{{ t('reports.riskScoreReport.hint') }}</p>
    <p v-if="rights.hasPermission('ManageTargets')">
      <button type="button" @click="recalculateNow" :disabled="recalculating">
        {{ recalculating ? t('reports.riskScoreReport.recalculating') : t('reports.riskScoreReport.recalculateNow') }}
      </button>
      <span v-if="recalculated" role="status">{{ ' ' + t('reports.riskScoreReport.recalculated') }}</span>
      <span v-if="recalculateError" role="alert"> {{ recalculateError }}</span>
    </p>
    <ReportExportButtons path="/api/reports/risk-score/export" :params="{ sort: sortQueryParam }" />
    <SelectionBar v-if="canOverride" :selection="selection" :page-keys="pageKeys" :filtered-count="filteredCount" keys-url="/api/reports/risk-score/keys">
      <button type="button" :disabled="selection.count === 0" @click="startBulk('set')">{{ t('reports.riskScoreReport.bulkSet') }}</button>
      <button type="button" :disabled="selection.count === 0" @click="startBulk('clear')">{{ t('reports.riskScoreReport.bulkClear') }}</button>
    </SelectionBar>
    <form v-if="bulk" class="bulk-override-form" @submit.prevent="applyBulk">
      <label v-if="bulk.action === 'set'" class="field-label"><span class="field-label-text">{{ t('reports.riskScoreReport.bulkScore') }}</span>
        <input v-model="bulk.score" type="number" min="0" max="1000" required /></label>
      <label class="field-label"><span class="field-label-text">{{ bulk.action === 'set' ? t('reports.riskScoreReport.bulkReason') : t('reports.riskScoreReport.bulkReasonOptional') }}</span>
        <input v-model="bulk.reason" size="50" maxlength="1000" :required="bulk.action === 'set'" /></label>
      <button type="submit" class="btn-primary" :disabled="bulkSaving">{{ bulk.action === 'set'
        ? t('reports.riskScoreReport.bulkApplySet', { count: formatCount(selection.count) }, selection.count)
        : t('reports.riskScoreReport.bulkApplyClear', { count: formatCount(selection.count) }, selection.count) }}</button>
      <button type="button" @click="bulk = null">{{ t('common.cancel') }}</button>
    </form>
    <p v-if="bulkError" role="alert">{{ bulkError }}</p>
    <p v-if="bulkResult" role="status">{{ t('reports.riskScoreReport.bulkDone', { count: formatCount(bulkResult.changed) }, bulkResult.changed) }}<template v-if="bulkResult.notFound.length">{{ ' ' + t('reports.riskScoreReport.bulkNotFound', { count: formatCount(bulkResult.notFound.length) }, bulkResult.notFound.length) }}</template></p>
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.riskScoreReport.loadFailed', { error }) }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-if="selection.enabled && canOverride"><span class="visually-hidden">{{ t('reports.riskScoreReport.selected') }}</span></th>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.accountKey">
          <tr>
            <td v-if="selection.enabled && canOverride">
              <input type="checkbox" :checked="selection.isSelected(row.accountKey)" :aria-label="t('reports.riskScoreReport.selectRow', { name: row.accountName })" @change="selection.toggle(row.accountKey)" />
            </td>
            <td>
              <button type="button" class="link-button" :aria-expanded="expandedAccountKey === row.accountKey" @click="toggleDrilldown(row.accountKey)">
                {{ row.accountName }}
              </button>
            </td>
            <td>{{ formatCount(row.computedRiskScore) || '—' }}</td>
            <td>{{ formatCount(row.overrideRiskScore) || '—' }}</td>
            <td>{{ formatCount(row.effectiveRiskScore) || '—' }}</td>
            <td>{{ row.riskScoreBandName ?? '—' }}</td>
            <td>{{ row.isRiskScoreStale ? t('reports.riskScoreReport.yes') : t('reports.riskScoreReport.no') }}</td>
            <td>{{ formatDate(row.riskScoreCalculatedDate) }}</td>
          </tr>
          <tr v-if="expandedAccountKey === row.accountKey">
            <td :colspan="selection.enabled && canOverride ? 8 : 7">
              <!-- UI review W7-6: the account name expands this panel, so the account itself is one link away. -->
              <p><router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.accountKey } }">{{ t('reports.riskScoreReport.openAccount', { name: row.accountName }) }}</router-link></p>
              <p v-if="contributorsLoading" role="status">{{ t('reports.riskScoreReport.loadingContributors') }}</p>
              <p v-else-if="contributorsError" role="alert">{{ contributorsError }}</p>
              <p v-else-if="contributors.length === 0">{{ t('reports.riskScoreReport.noContributors') }}</p>
              <table v-else class="drilldown-table">
                <thead>
                  <tr>
                    <th>{{ t('reports.riskScoreReport.colType') }}</th>
                    <th>{{ t('common.name') }}</th>
                    <th>{{ t('reports.riskScoreReport.colRiskValue') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="c in contributors" :key="`${c.entityType}-${c.entityKey}`">
                    <td>{{ c.entityType }}</td>
                    <td>{{ c.entityName }}</td>
                    <td>{{ formatCount(c.riskValue) }}</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <EmptyListMessage :show="!loading && !error && rows.length === 0" :message="t('reports.riskScoreReport.emptyNone')" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>{{ t('reports.riskScoreReport.sortHelp') }}</small></p>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}

.link-button {
  background: none;
  border: none;
  padding: 0;
  color: var(--color-link);
  text-decoration: underline;
  cursor: pointer;
  font: inherit;
}

.drilldown-table {
  margin: 0.5rem 0 0.5rem 1.5rem;
}
</style>
