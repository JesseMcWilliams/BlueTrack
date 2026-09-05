<script setup>
// Searchable/filterable view against /api/audit-log (AuditLogController) --
// filters map to query params, and a click on a row drills into its
// field-level changes (Design_Audit_Logging.md's Admin UI Requirements).
import { ref, computed, watch, onMounted } from 'vue'

const events = ref([])
const error = ref(null)
const loading = ref(true)

const eventTypeFilter = ref('')
const entityNameFilter = ref('')
const fromDateFilter = ref('')
const toDateFilter = ref('')

const expandedEventKey = ref(null)
const fieldChanges = ref([])

// D-42: multi-column sort, same click/shift-click pattern as Account
// Progress List and the Risk Exceptions list.
const sortColumns = ref([])
const columns = [
  { field: 'occurredAt', label: 'Occurred At' },
  { field: 'eventTypeName', label: 'Event Type' },
  { field: 'performedByName', label: 'By' },
  { field: 'entityName', label: 'Entity' }
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
    if (eventTypeFilter.value) params.set('eventType', eventTypeFilter.value)
    if (entityNameFilter.value) params.set('entityName', entityNameFilter.value)
    if (fromDateFilter.value) params.set('fromDate', fromDateFilter.value)
    if (toDateFilter.value) params.set('toDate', toDateFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)

    const response = await fetch(`/api/audit-log?${params.toString()}`)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    events.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(sortQueryParam, load)

async function toggleFieldChanges(event) {
  if (expandedEventKey.value === event.auditEventKey) {
    expandedEventKey.value = null
    return
  }
  const response = await fetch(`/api/audit-log/${event.auditEventKey}/field-changes`)
  fieldChanges.value = response.ok ? await response.json() : []
  expandedEventKey.value = event.auditEventKey
}
</script>

<template>
  <div>
    <h2>Audit Log Viewer</h2>

    <form @submit.prevent="load">
      <label>Event Type: <input v-model="eventTypeFilter" placeholder="e.g. FieldEdit" /></label>
      <label>Entity: <input v-model="entityNameFilter" placeholder="e.g. risk_exception" /></label>
      <label>From: <input v-model="fromDateFilter" type="date" /></label>
      <label>To: <input v-model="toDateFilter" type="date" /></label>
      <button type="submit">Filter</button>
    </form>

    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="events.length === 0">No matching audit events.</p>

    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
          <th>Detail</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="event in events" :key="event.auditEventKey">
          <tr>
            <td>
              <button
                type="button"
                :aria-expanded="expandedEventKey === event.auditEventKey"
                :aria-controls="`audit-detail-${event.auditEventKey}`"
                @click="toggleFieldChanges(event)"
              >
                {{ event.occurredAt }}
              </button>
            </td>
            <td>{{ event.eventTypeName }}</td>
            <td>{{ event.performedByName }}</td>
            <td>{{ event.entityName }} {{ event.entityKey }}</td>
            <td>{{ event.detail }}</td>
          </tr>
          <tr v-if="expandedEventKey === event.auditEventKey" :id="`audit-detail-${event.auditEventKey}`">
            <td colspan="5">
              <p v-if="event.reason"><strong>Reason:</strong> {{ event.reason }}</p>
              <p v-if="fieldChanges.length === 0">No field-level changes recorded for this event.</p>
              <table v-else>
                <thead><tr><th>Field</th><th>Old Value</th><th>New Value</th></tr></thead>
                <tbody>
                  <tr v-for="change in fieldChanges" :key="change.fieldName">
                    <td>{{ change.fieldName }}</td>
                    <td>{{ change.oldValue }}</td>
                    <td>{{ change.newValue }}</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </div>
</template>
