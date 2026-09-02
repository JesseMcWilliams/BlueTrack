<script setup>
// CRUD against /api/admin/identity-providers (IdentityProvidersController).
// Only WindowsIntegrated is actually wired at runtime
// (App/Api/Auth/AuthenticationExtensions.cs) -- adding an OIDC/SAML/
// DevFakeAuth row here stores configuration data but has no live effect
// yet, matching that file's own note on why they aren't registered.
import { ref, onMounted } from 'vue'

const providers = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/identity-providers')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    providers.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function startCreate() {
  editing.value = { providerType: 'OIDC', displayName: '', isEnabled: false, displayOrder: 0, configurationValues: '', secretReference: '' }
}
function startEdit(provider) {
  editing.value = { ...provider }
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.providerKey === undefined
  const url = isNew ? '/api/admin/identity-providers' : `/api/admin/identity-providers/${editing.value.providerKey}`
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

async function remove(provider) {
  const response = await fetch(`/api/admin/identity-providers/${provider.providerKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Identity Providers</h2>
    <p v-if="error">{{ error }}</p>
    <p v-if="loading">Loading...</p>

    <template v-else>
      <button @click="startCreate">+ New Provider</button>

      <table>
        <thead>
          <tr><th>Type</th><th>Display Name</th><th>Enabled</th><th>Order</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="provider in providers" :key="provider.providerKey">
            <td>{{ provider.providerType }}</td>
            <td>{{ provider.displayName }}</td>
            <td>{{ provider.isEnabled }}</td>
            <td>{{ provider.displayOrder }}</td>
            <td>
              <button @click="startEdit(provider)">Edit</button>
              <button @click="remove(provider)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.providerKey === undefined ? 'New Provider' : 'Edit Provider' }}</h3>
        <p>
          <label>
            Provider Type:
            <select v-model="editing.providerType">
              <option value="WindowsIntegrated">WindowsIntegrated</option>
              <option value="OIDC">OIDC</option>
              <option value="SAML">SAML</option>
              <option value="DevFakeAuth">DevFakeAuth</option>
            </select>
          </label>
        </p>
        <p><label>Display Name: <input v-model="editing.displayName" required /></label></p>
        <p><label><input v-model="editing.isEnabled" type="checkbox" /> Enabled</label></p>
        <p><label>Display Order: <input v-model.number="editing.displayOrder" type="number" /></label></p>
        <p><label>Configuration Values (JSON): <textarea v-model="editing.configurationValues"></textarea></label></p>
        <p><label>Secret Reference: <input v-model="editing.secretReference" /></label></p>
        <button type="submit">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
