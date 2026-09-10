<script setup>
// Application CRUD against /api/applications (detailed/create/update) plus
// Safe -> Application assignment against /api/safes (SafesController).
// dim_safe is small enough to load in full (unlike fact_account).
//
// D-124 Phase 4: the Applications section's Add/Edit moved to its own
// routed page (ApplicationEdit.vue, admin-application-create/-edit) -- this
// page no longer owns an inline editing/startCreate/startEdit/cancelEdit
// form for Applications. The Safes section below (a plain per-row <select>
// assignment, not a form) is untouched.
import { ref, onMounted } from 'vue'

const applications = ref([])
const safes = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const [appsResponse, safesResponse] = await Promise.all([
      fetch('/api/applications/detailed'),
      fetch('/api/safes')
    ])
    if (!appsResponse.ok) throw new Error(`Applications request failed: ${appsResponse.status}`)
    if (!safesResponse.ok) throw new Error(`Safes request failed: ${safesResponse.status}`)
    applications.value = await appsResponse.json()
    safes.value = await safesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function assignSafe(safe, applicationKeyRaw) {
  const applicationKey = applicationKeyRaw === '' ? null : Number(applicationKeyRaw)
  const response = await fetch(`/api/safes/${safe.safeKey}/application`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(applicationKey)
  })
  if (!response.ok) {
    error.value = `Assign failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Application ↔ Safe Mapping</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <h3>Applications</h3>
      <p><router-link :to="{ name: 'admin-application-create' }">+ New Application</router-link></p>
      <table>
        <thead>
          <tr><th>Code</th><th>Name</th><th>Owner</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="app in applications" :key="app.applicationKey">
            <td>{{ app.applicationCode }}</td>
            <td>{{ app.applicationName }}</td>
            <td>{{ app.ownerName }}</td>
            <td><router-link :to="{ name: 'admin-application-edit', params: { applicationKey: app.applicationKey } }">Edit</router-link></td>
          </tr>
        </tbody>
      </table>

      <h3>Safes</h3>
      <table>
        <thead>
          <tr><th>Safe</th><th>Application</th></tr>
        </thead>
        <tbody>
          <tr v-for="safe in safes" :key="safe.safeKey">
            <td>{{ safe.safeName }}</td>
            <td>
              <select :value="safe.applicationKey ?? ''" @change="assignSafe(safe, $event.target.value)">
                <option value="">(none)</option>
                <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">
                  {{ app.applicationName }}
                </option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
