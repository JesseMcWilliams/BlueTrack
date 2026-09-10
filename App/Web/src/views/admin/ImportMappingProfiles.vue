<script setup>
// Design_Risk_Scoring.md, D-101-105, D-119 Phase B: a named field mapping
// per distinct source file shape -- an admin types in which real column
// name in their export corresponds to each internal field. Uploading this
// app's own generated template needs no profile at all (its headers
// already match the internal field names 1:1).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (ImportMappingProfileEdit.vue, admin-import-mapping-profile-create/-edit)
// -- this page no longer owns an inline editing/startCreate/startEdit/
// cancelEdit/addFieldRow/removeFieldRow form.
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-scoring/import-mapping-profiles')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(item) {
  const response = await fetch(`/api/admin/risk-scoring/import-mapping-profiles/${item.importMappingProfileKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Import Mapping Profiles</h2>
    <p>One named profile per distinct source file shape for the risk-scoring import feeds -- not needed at all for this app's own generated CSV templates.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><router-link :to="{ name: 'admin-import-mapping-profile-create' }">+ New Profile</router-link></p>

      <table>
        <thead>
          <tr><th>Feed Type</th><th>Name</th><th>Active</th><th>Fields</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.importMappingProfileKey">
            <td>{{ item.feedType }}</td>
            <td>{{ item.profileName }}</td>
            <td>{{ item.isActive ? 'Yes' : 'No' }}</td>
            <td>{{ item.fields.map(f => `${f.sourceColumnName} -> ${f.targetFieldName}`).join(', ') }}</td>
            <td>
              <router-link :to="{ name: 'admin-import-mapping-profile-edit', params: { importMappingProfileKey: item.importMappingProfileKey } }">Edit</router-link>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
