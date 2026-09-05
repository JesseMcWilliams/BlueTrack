<script setup>
// Application CRUD against /api/applications (detailed/create/update) plus
// Safe -> Application assignment against /api/safes (SafesController).
// dim_safe is small enough to load in full (unlike fact_account).
import { ref, onMounted } from 'vue'

const applications = ref([])
const safes = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

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

function startCreate() {
  editing.value = { applicationCode: '', applicationName: '', description: '', ownerName: '', ownerEmail: '', technicalName: '', technicalEmail: '', notes: '' }
}
function startEdit(app) {
  editing.value = { ...app }
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.applicationKey === undefined
  const url = isNew ? '/api/applications' : `/api/applications/${editing.value.applicationKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(editing.value)
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  editing.value = null
  await load()
}

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
      <button @click="startCreate">+ New Application</button>
      <table>
        <thead>
          <tr><th>Code</th><th>Name</th><th>Owner</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="app in applications" :key="app.applicationKey">
            <td>{{ app.applicationCode }}</td>
            <td>{{ app.applicationName }}</td>
            <td>{{ app.ownerName }}</td>
            <td><button @click="startEdit(app)">Edit</button></td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h4>{{ editing.applicationKey === undefined ? 'New Application' : 'Edit Application' }}</h4>
        <p><label>Code: <input v-model="editing.applicationCode" required /></label></p>
        <p><label>Name: <input v-model="editing.applicationName" required /></label></p>
        <p><label>Description: <input v-model="editing.description" /></label></p>
        <p><label>Owner Name: <input v-model="editing.ownerName" /></label></p>
        <p><label>Owner Email: <input v-model="editing.ownerEmail" /></label></p>
        <p><label>Technical Contact Name: <input v-model="editing.technicalName" /></label></p>
        <p><label>Technical Contact Email: <input v-model="editing.technicalEmail" /></label></p>
        <p><label>Notes: <input v-model="editing.notes" /></label></p>
        <button type="submit">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

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
