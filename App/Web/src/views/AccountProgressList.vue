<script setup>
// D-42: multiple simultaneous filter layers (stage/status/riskLevel/owner,
// stacked with AND) plus multi-column sort -- click a column header to sort
// by just that column; shift-click another header to add it as a secondary
// sort key without losing the first (badges show the resulting priority).
import { ref, computed, watch, onMounted } from 'vue'
import { formatDate } from '../utils/formatDate'
import { useTotalCount } from '../composables/useTotalCount'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'

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

const accounts = ref([])
const referenceData = ref({})
const error = ref(null)
const loading = ref(true)

const stageFilter = ref('')
const statusFilter = ref('')
const riskLevelFilter = ref('')
const ownerFilter = ref('')

// Each entry: { field, descending }. Order in this array IS sort priority.
const sortColumns = ref([])

const columns = [
  { field: 'accountName', label: 'Account' },
  { field: 'stageName', label: 'Stage' },
  { field: 'statusName', label: 'Status' },
  { field: 'riskLevelName', label: 'Risk Level' },
  { field: 'ownerName', label: 'Owner' },
  { field: 'targetRemediationDate', label: 'Target Remediation Date' },
  { field: 'effectiveRiskScore', label: 'Risk Score' },
  { field: 'riskScoreBandName', label: 'Risk Band' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

// D-92 (ARIA APG Sortable Table pattern): aria-sort only ever reflects the
// primary sort key -- aria-sort has no "this is the secondary key" value,
// so a secondary sort column (idx > 0) still reports "none" here even
// though sortIndicator() above shows it a numbered arrow visually.
function ariaSortFor(field) {
  if (sortColumns.value.length === 0 || sortColumns.value[0].field !== field) return 'none'
  return sortColumns.value[0].descending ? 'descending' : 'ascending'
}

function toggleSort(field, event) {
  const existingIndex = sortColumns.value.findIndex(s => s.field === field)

  if (!event.shiftKey) {
    // Plain click: this column becomes the only sort key. Clicking the
    // same column again (when it's already the sole key) flips direction.
    if (existingIndex === 0 && sortColumns.value.length === 1) {
      sortColumns.value = [{ field, descending: !sortColumns.value[0].descending }]
    } else {
      sortColumns.value = [{ field, descending: false }]
    }
    return
  }

  // Shift-click: add/toggle this column as an additional sort key.
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
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (stageFilter.value) params.set('stage', stageFilter.value)
    if (statusFilter.value) params.set('status', statusFilter.value)
    if (riskLevelFilter.value) params.set('riskLevel', riskLevelFilter.value)
    if (ownerFilter.value) params.set('owner', ownerFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const response = await fetch(`/api/account-progress?${params.toString()}`)
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    readTotalCount(response)
    accounts.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const refResponse = await fetch('/api/account-progress/reference-data')
    if (refResponse.ok) referenceData.value = await refResponse.json()
  } catch {
    // Non-fatal -- filters just won't have dropdown options if this fails.
  }
  await load()
})

// D-124 Phase 3: a filter/sort change resets to page 1 -- see Targets.vue's
// identical comment for why page-size/Prev/Next changes are handled separately.
watch([stageFilter, statusFilter, riskLevelFilter, ownerFilter, sortQueryParam], () => {
  page.value = 1
  load()
})
</script>

<template>
  <div>
    <h1>Account Progress</h1>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Stage:</span>
        <select v-model="stageFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_blueprint_stage ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Status:</span>
        <select v-model="statusFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_progress_status ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Risk Level:</span>
        <select v-model="riskLevelFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_risk_level ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Owner:</span> <input v-model="ownerFilter" type="text" placeholder="contains..." /></label>
    </p>
    <FilterCountSummary :shown="accounts.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load accounts: {{ error }}</p>
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
        <template v-for="account in accounts" :key="account.accountKey">
          <tr>
            <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.accountName }}</router-link></td>
            <td>{{ account.stageName }}</td>
            <td>{{ account.statusName }}</td>
            <td>{{ account.riskLevelName }}</td>
            <td>{{ account.ownerName }}</td>
            <td>{{ formatDate(account.targetRemediationDate) }}</td>
            <td>{{ account.effectiveRiskScore ?? '—' }}</td>
            <td>{{ account.riskScoreBandName ?? '—' }}</td>
          </tr>
        </template>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>
  </div>
</template>
