<script setup>
// Calls GET /api/risk-exceptions (RiskExceptionsController) -- D-42:
// stacked filters (status/scope type) plus multi-column sort, same pattern
// as AccountProgressList.vue.
import { ref, computed, watch, onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'

const rights = useRightsStore()
const exceptions = ref([])
const error = ref(null)
const loading = ref(true)
const statusFilter = ref('')
const scopeTypeFilter = ref('')

const sortColumns = ref([])

const columns = [
  { field: 'exceptionID', label: 'Exception ID' },
  { field: 'scopeName', label: 'Scope' },
  { field: 'approvedByName', label: 'Approved By' },
  { field: 'approvalDate', label: 'Approval Date' },
  { field: 'reviewDate', label: 'Review Date' },
  { field: 'statusName', label: 'Status' }
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
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (statusFilter.value) params.set('status', statusFilter.value)
    if (scopeTypeFilter.value) params.set('scopeType', scopeTypeFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)

    const response = await fetch(`/api/risk-exceptions?${params.toString()}`)
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    exceptions.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch([statusFilter, scopeTypeFilter, sortQueryParam], load)
</script>

<template>
  <div>
    <h1>Risk Exceptions</h1>
    <p v-if="rights.hasPermission('ApproveExceptions')">
      <router-link :to="{ name: 'risk-exception-create' }">+ New Exception</router-link>
    </p>
    <p>
      <label>Status:
        <select v-model="statusFilter">
          <option value="">All</option>
          <option value="Active">Active</option>
          <option value="Expired">Expired</option>
          <option value="Revoked">Revoked</option>
        </select>
      </label>
      <label>Scope:
        <select v-model="scopeTypeFilter">
          <option value="">All</option>
          <option value="Account">Account</option>
          <option value="Application">Application</option>
        </select>
      </label>
    </p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load exceptions: {{ error }}</p>
    <p v-else-if="exceptions.length === 0">No exceptions found.</p>
    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
          <th>Justification</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="exception in exceptions" :key="exception.exceptionKey">
          <td><router-link :to="{ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } }">{{ exception.exceptionID }}</router-link></td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.approvedByName }}</td>
          <td>{{ exception.approvalDate }}</td>
          <td>{{ exception.reviewDate }}</td>
          <td>{{ exception.statusName }}</td>
          <td>{{ exception.justification }}</td>
        </tr>
      </tbody>
    </table>
    <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>
  </div>
</template>
