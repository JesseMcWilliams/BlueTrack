<script setup>
// AD Account Discovery feature (2026-09-16): gated by ViewDiscoveredAccounts.
// Real AD accounts not yet onboarded into CyberArk, found nightly by
// matching AD group membership against the Access Group inventory, and
// risk-scored the same way any other Access-Group-reachable entity is.
// Read-only in this pass -- no accept/dismiss/onboard action yet (confirmed
// directly: visibility only). Sortable table + per-row drill-down mirror
// RiskScoreReport.vue's own shape exactly, since this is the same kind of
// "score plus what's behind it" report.
import { ref, computed, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

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

const expandedKey = ref(null)
const accessGroups = ref([])
const accessGroupsLoading = ref(false)
const accessGroupsError = ref(null)

const sortColumns = ref([])

const columns = [
  { field: 'samAccountName', label: 'Account' },
  { field: 'domainName', label: 'Domain' },
  { field: 'computedRiskScore', label: 'Risk Score' },
  { field: 'riskScoreBandName', label: 'Risk Band' },
  { field: 'discoveredDate', label: 'First Discovered' },
  { field: 'lastSeenDate', label: 'Last Seen' }
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
      sortColumns.value = [{ field, descending: field === 'computedRiskScore' }]
    }
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
    const response = await fetch(`/api/reports/discovered-accounts?${params.toString()}`)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    readTotalCount(response)
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

async function toggleDrilldown(discoveredAccountKey) {
  if (expandedKey.value === discoveredAccountKey) {
    expandedKey.value = null
    return
  }

  expandedKey.value = discoveredAccountKey
  accessGroups.value = []
  accessGroupsError.value = null
  accessGroupsLoading.value = true
  try {
    const response = await fetch(`/api/reports/discovered-accounts/${discoveredAccountKey}/access-groups`)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    accessGroups.value = await response.json()
  } catch (err) {
    accessGroupsError.value = err.message
  } finally {
    accessGroupsLoading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div>
    <h2>Discovered Accounts</h2>
    <p class="hint">
      Real Active Directory accounts not yet onboarded into CyberArk, found by matching AD group membership
      against the Access Group inventory -- risk-scored the same way an onboarded account would be. Click a
      row's account name to see which Access Groups it matched.
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
        <template v-for="row in rows" :key="row.discoveredAccountKey">
          <tr>
            <td>
              <button type="button" class="link-button" @click="toggleDrilldown(row.discoveredAccountKey)">
                {{ row.samAccountName }}
              </button>
              <span v-if="row.displayName"> ({{ row.displayName }})</span>
              <span v-if="!row.isEnabled"> [disabled]</span>
              <p v-if="row.possibleExistingAccountKey" class="possible-match" role="status">
                Possibly already onboarded as "{{ row.possibleExistingAccountName }}"
              </p>
            </td>
            <td>{{ row.domainName }}</td>
            <td>{{ row.computedRiskScore ?? '—' }}</td>
            <td>{{ row.riskScoreBandName ?? '—' }}</td>
            <td>{{ formatDate(row.discoveredDate) }}</td>
            <td>{{ formatDate(row.lastSeenDate) }}</td>
          </tr>
          <tr v-if="expandedKey === row.discoveredAccountKey">
            <td colspan="6">
              <p v-if="accessGroupsLoading" role="status">Loading matched Access Groups...</p>
              <p v-else-if="accessGroupsError" role="alert">{{ accessGroupsError }}</p>
              <p v-else-if="accessGroups.length === 0">No matched Access Groups found.</p>
              <ul v-else class="drilldown-list">
                <li v-for="g in accessGroups" :key="g.accessGroupKey">{{ g.groupName }}</li>
              </ul>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
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

.possible-match {
  margin: var(--space-1) 0 0;
  font-size: 0.875em;
}

.drilldown-list {
  margin: 0.5rem 0 0.5rem 1.5rem;
}
</style>
