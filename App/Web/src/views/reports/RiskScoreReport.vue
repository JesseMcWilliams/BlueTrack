<script setup>
// D-101-105 Phase E: gated by ViewRiskReport. Sortable table shape mirrors
// AccountProgressList.vue's own multi-column sort (click to sort, shift-click
// to add a secondary key) since sorting/filtering by score is the whole
// point of this report. Clicking a row expands a drill-down showing the
// real Targets/Access Groups contributing to that Account's score
// (GET /api/reports/risk-score/{accountKey}/contributors).
import { ref, computed, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'

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
const recalculated = ref(false)

const expandedAccountKey = ref(null)
const contributors = ref([])
const contributorsLoading = ref(false)
const contributorsError = ref(null)

const sortColumns = ref([])

const columns = [
  { field: 'accountName', label: 'Account' },
  { field: 'computedRiskScore', label: 'Computed' },
  { field: 'overrideRiskScore', label: 'Override' },
  { field: 'effectiveRiskScore', label: 'Effective' },
  { field: 'riskScoreBandName', label: 'Risk Band' },
  { field: 'isRiskScoreStale', label: 'Stale?' },
  { field: 'riskScoreCalculatedDate', label: 'Last Calculated' }
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
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
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
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
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
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    recalculated.value = true
    await load()
  } catch (err) {
    recalculateError.value = err.message
  } finally {
    recalculating.value = false
  }
}

onMounted(load)
</script>

<template>
  <div>
    <h2>Risk Score</h2>
    <p class="hint">
      EffectiveRiskScore is the Override (if set) or otherwise the Computed score. Click a row's Account
      name to see the real Targets/Access Groups behind its score.
    </p>
    <p>
      <button type="button" @click="recalculateNow" :disabled="recalculating">
        {{ recalculating ? 'Recalculating...' : 'Recalculate Now' }}
      </button>
      <span v-if="recalculated" role="status"> Recalculated.</span>
      <span v-if="recalculateError" role="alert"> {{ recalculateError }}</span>
    </p>
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load report: {{ error }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.accountKey">
          <tr>
            <td>
              <button type="button" class="link-button" @click="toggleDrilldown(row.accountKey)">
                {{ row.accountName }}
              </button>
            </td>
            <td>{{ row.computedRiskScore ?? '—' }}</td>
            <td>{{ row.overrideRiskScore ?? '—' }}</td>
            <td>{{ row.effectiveRiskScore ?? '—' }}</td>
            <td>{{ row.riskScoreBandName ?? '—' }}</td>
            <td>{{ row.isRiskScoreStale ? 'Yes' : 'No' }}</td>
            <td>{{ formatDate(row.riskScoreCalculatedDate) }}</td>
          </tr>
          <tr v-if="expandedAccountKey === row.accountKey">
            <td colspan="7">
              <p v-if="contributorsLoading" role="status">Loading contributors...</p>
              <p v-else-if="contributorsError" role="alert">{{ contributorsError }}</p>
              <p v-else-if="contributors.length === 0">No contributing Targets or Access Groups found.</p>
              <table v-else class="drilldown-table">
                <thead>
                  <tr>
                    <th>Type</th>
                    <th>Name</th>
                    <th>Risk Value</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="c in contributors" :key="`${c.entityType}-${c.entityKey}`">
                    <td>{{ c.entityType }}</td>
                    <td>{{ c.entityName }}</td>
                    <td>{{ c.riskValue }}</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>
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
