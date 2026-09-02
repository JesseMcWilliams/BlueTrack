<script setup>
// CRUD + lookup/test tool against /api/admin/group-role-mappings
// (GroupRoleMappingsController), scoped to the WindowsIntegrated provider
// only (the only one that actually authenticates anyone). Admins type a
// friendly group name; the server resolves it to the SID that's actually
// stored (D-69) and matched against at login.
import { ref, onMounted } from 'vue'

const mappings = ref([])
const error = ref(null)
const loading = ref(true)

const lookupGroupName = ref('')
const lookupResult = ref(null)
const lookupError = ref(null)

const newGroupName = ref('')
const newRoleName = ref('')

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

async function createMapping() {
  error.value = null
  const response = await fetch('/api/admin/group-role-mappings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ groupName: newGroupName.value, roleName: newRoleName.value })
  })
  if (!response.ok) {
    error.value = `Create failed: ${response.status}`
    return
  }
  newGroupName.value = ''
  newRoleName.value = ''
  await load()
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
    <p v-if="error">{{ error }}</p>
    <p v-if="loading">Loading...</p>

    <template v-else>
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

      <h3>Add Mapping</h3>
      <form @submit.prevent="createMapping">
        <p><label>Group Name (e.g. BUILTIN\Administrators or DOMAIN\GroupName): <input v-model="newGroupName" required /></label></p>
        <p><label>Role Name: <input v-model="newRoleName" required /></label></p>
        <button type="submit">Add</button>
      </form>

      <h3>Lookup / Test Tool</h3>
      <p>Resolve a group name and see what it currently grants, without saving anything.</p>
      <form @submit.prevent="lookup">
        <input v-model="lookupGroupName" placeholder="Group name" required />
        <button type="submit">Resolve</button>
      </form>
      <p v-if="lookupError">{{ lookupError }}</p>
      <div v-if="lookupResult">
        <p>Resolved to: {{ lookupResult.resolvedAccountName }} ({{ lookupResult.sid }})</p>
        <p>Current role(s): {{ lookupResult.currentRoleNames.join(', ') || '(none mapped)' }}</p>
        <p>Current permission(s): {{ lookupResult.currentPermissionNames.join(', ') || '(none)' }}</p>
      </div>
    </template>
  </div>
</template>
