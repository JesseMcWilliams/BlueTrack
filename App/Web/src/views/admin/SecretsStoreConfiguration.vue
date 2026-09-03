<script setup>
// Against /api/admin/secrets-store (SecretsStoreController). This manages
// the config record only (which backend is active, plus its non-secret
// settings) -- no actual backend (Windows DPAPI, CyberArk CP, etc.) is
// implemented in this app yet (Design_Secrets_Storage.md).
import { ref, onMounted } from 'vue'

const backends = ref([])
const error = ref(null)
const loading = ref(true)
const settingsDraft = ref({})

const testSafe = ref('')
const testFolder = ref('Root')
const testObject = ref('')
const testResult = ref(null)
const testing = ref(false)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/secrets-store')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    backends.value = await response.json()
    for (const backend of backends.value) {
      settingsDraft.value[backend.backendType] = backend.backendSettings ?? ''
    }
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function activate(backend) {
  const response = await fetch('/api/admin/secrets-store/active', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ backendType: backend.backendType, backendSettings: settingsDraft.value[backend.backendType] || null })
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  await load()
}

async function testConnection() {
  testing.value = true
  testResult.value = null
  try {
    const response = await fetch('/api/admin/secrets-store/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ safe: testSafe.value, folder: testFolder.value, object: testObject.value })
    })
    testResult.value = response.ok ? await response.json() : { success: false, error: `Request failed: ${response.status}` }
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div>
    <h2>Secrets Store Configuration</h2>
    <p>Exactly one backend is active at a time. Configures the record only -- no backend is actually implemented yet.</p>
    <p v-if="error">{{ error }}</p>
    <p v-if="loading">Loading...</p>

    <table v-else>
      <thead>
        <tr><th>Backend</th><th>Active</th><th>Settings (JSON)</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="backend in backends" :key="backend.secretStoreKey">
          <td>{{ backend.backendType }}</td>
          <td>{{ backend.isActive ? 'Yes' : '' }}</td>
          <td><input v-model="settingsDraft[backend.backendType]" size="40" /></td>
          <td><button :disabled="backend.isActive" @click="activate(backend)">Make Active</button></td>
        </tr>
      </tbody>
    </table>

    <h3>Test Connection</h3>
    <p>Attempts a real retrieval against the active backend. Never shows the retrieved secret -- only whether it succeeded and non-secret metadata (username/address).</p>
    <form @submit.prevent="testConnection">
      <label>Safe: <input v-model="testSafe" required /></label>
      <label>Folder: <input v-model="testFolder" required /></label>
      <label>Object: <input v-model="testObject" required size="50" /></label>
      <button type="submit" :disabled="testing">Test</button>
    </form>
    <div v-if="testResult">
      <p v-if="testResult.success">
        Success. UserName: {{ testResult.userName }}, Address: {{ testResult.address }}, password length: {{ testResult.passwordLength }}
        <span v-if="testResult.fromFallbackCache"> (served from fallback cache, D-49)</span>
      </p>
      <p v-else>Failed{{ testResult.errorCategory ? ` (${testResult.errorCategory})` : '' }}: {{ testResult.error }}</p>
    </div>
  </div>
</template>
