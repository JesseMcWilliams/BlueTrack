<script setup>
// CRUD + lookup/test tool against /api/admin/group-role-mappings
// (GroupRoleMappingsController), scoped to the WindowsIntegrated provider
// only (the only one that actually authenticates anyone). Admins type a
// friendly group name; the server resolves it to the SID that's actually
// stored (D-69) and matched against at login.
//
// D-124 Phase 4 (partial conversion): the "Add Mapping" form moved to its
// own routed page (GroupRoleMappingCreate.vue,
// admin-group-role-mapping-create) -- this page no longer owns that inline
// form or its own roles fetch (used only by that form). Delete-only per
// row and the Lookup/Test Tool below are both untouched.
import { ref, onMounted } from 'vue'

const mappings = ref([])
const error = ref(null)
const loading = ref(true)

const lookupGroupName = ref('')
const lookupResult = ref(null)
const lookupError = ref(null)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/group-role-mappings')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    mappings.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function lookup() {
  lookupError.value = null
  lookupResult.value = null
  const response = await fetch('/api/admin/group-role-mappings/resolve-group', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ groupName: lookupGroupName.value })
  })
  if (!response.ok) {
    lookupError.value = response.status === 404 ? 'Could not resolve that group name.' : `Request failed: ${response.status}`
    return
  }
  lookupResult.value = await response.json()
}

async function remove(mapping) {
  const response = await fetch(`/api/admin/group-role-mappings/${mapping.mappingKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Group → Role Mapping</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><router-link :to="{ name: 'admin-group-role-mapping-create' }">+ Add Mapping</router-link></p>

      <table>
        <thead>
          <tr><th>Provider</th><th>Group (stored identifier)</th><th>Role</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="mapping in mappings" :key="mapping.mappingKey">
            <td>{{ mapping.providerType }}</td>
            <td>{{ mapping.identityGroupName }}</td>
            <td>{{ mapping.roleName }}</td>
            <td><button @click="remove(mapping)">Delete</button></td>
          </tr>
        </tbody>
      </table>

      <h3>Lookup / Test Tool</h3>
      <p>Resolve a group name and see what it currently grants, without saving anything.</p>
      <form class="filter-row" @submit.prevent="lookup">
        <label class="field-label"><span class="field-label-text">Group name</span> <input v-model="lookupGroupName" required /></label>
        <button type="submit">Resolve</button>
      </form>
      <p v-if="lookupError" role="alert">{{ lookupError }}</p>
      <div v-if="lookupResult">
        <p>Resolved to: {{ lookupResult.resolvedAccountName }} ({{ lookupResult.sid }})</p>
        <p>Current role(s): {{ lookupResult.currentRoleNames.join(', ') || '(none mapped)' }}</p>
        <p>Current permission(s): {{ lookupResult.currentPermissionNames.join(', ') || '(none)' }}</p>
      </div>
    </template>
  </div>
</template>
