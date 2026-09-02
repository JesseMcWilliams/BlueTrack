<script setup>
// Searchable/filterable view against /api/audit-log (AuditLogController) --
// filters map to query params, and a click on a row drills into its
// field-level changes (Design_Audit_Logging.md's Admin UI Requirements).
import { ref, onMounted } from 'vue'

const events = ref([])
const error = ref(null)
const loading = ref(true)

const eventTypeFilter = ref('')
const entityNameFilter = ref('')
const fromDateFilter = ref('')
const toDateFilter = ref('')

const expandedEventKey = ref(null)
const fieldChanges = ref([])

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (eventTypeFilter.value) params.set('eventType', eventTypeFilter.value)
    if (entityNameFilter.value) params.set('entityName', entityNameFilter.value)
    if (fromDateFilter.value) params.set('fromDate', fromDateFilter.value)
    if (toDateFilter.value) params.set('toDate', toDateFilter.value)

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

    <p v-if="error">{{ error }}</p>
    <p v-if="loading">Loading...</p>
    <p v-else-if="events.length === 0">No matching audit events.</p>

    <table v-else>
      <thead>
        <tr><th>Occurred At</th><th>Event Type</th><th>By</th><th>Entity</th><th>Detail</th></tr>
      </thead>
      <tbody>
        <template v-for="event in events" :key="event.auditEventKey">
          <tr @click="toggleFieldChanges(event)">
            <td>{{ event.occurredAt }}</td>
            <td>{{ event.eventTypeName }}</td>
            <td>{{ event.performedByName }}</td>
            <td>{{ event.entityName }} {{ event.entityKey }}</td>
            <td>{{ event.detail }}</td>
          </tr>
          <tr v-if="expandedEventKey === event.auditEventKey">
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
