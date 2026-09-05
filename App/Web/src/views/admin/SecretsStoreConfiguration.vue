<script setup>
// Against /api/admin/secrets-store (SecretsStoreController). Windows DPAPI,
// CyberArk CP/CCP, Azure Key Vault, AWS Secrets Manager, and CyberArk
// Conjur are all implemented (Design_Secrets_Storage.md, D-84) -- this page
// manages the config record (which backend is active, its non-secret
// settings, and a write-only credential field for backends that need one
// to authenticate to their own remote service).
//
// D-95: each backend's Settings used to be a raw JSON textarea an admin had
// to hand-write against that backend's own private *Settings class's exact
// property names -- structured fields below, keyed by PascalCase names
// matching those classes exactly (CyberArkCpSettings.cs,
// CyberArkCcpSettings.cs, and the private *Settings classes inside
// CyberArkConjurSecretsProvider.cs/AzureKeyVaultSecretsProvider.cs/
// AwsSecretsManagerSecretsProvider.cs -- confirmed directly, not guessed).
// A parsed settings OBJECT is kept per backend (not a string) and only its
// known fields are edited -- any other key already present (in practice
// just "ProtectedCredential", written server-side from PlaintextCredential
// and returned redacted as "***" by SecretsStoreRepository.Redact) is left
// untouched and still round-trips on save, since SetActiveAsync replaces
// BackendSettings wholesale with whatever this page sends when no new
// PlaintextCredential is supplied.
import { ref, onMounted } from 'vue'

const backends = ref([])
const error = ref(null)
const loading = ref(true)
const settingsObjects = ref({})
const credentialDraft = ref({})

const testSafe = ref('')
const testFolder = ref('Root')
const testObject = ref('')
const testResult = ref(null)
const testing = ref(false)

function parseSettings(backendType, raw) {
  let parsed = {}
  if (raw) {
    try {
      parsed = JSON.parse(raw)
    } catch {
      parsed = {}
    }
  }
  if (backendType === 'AzureKeyVault' && !parsed.AuthMethod) parsed.AuthMethod = 'ManagedIdentity'
  if (backendType === 'AwsSecretsManager' && !parsed.AuthMethod) parsed.AuthMethod = 'IamRole'
  return parsed
}

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/secrets-store')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    backends.value = await response.json()
    for (const backend of backends.value) {
      settingsObjects.value[backend.backendType] = parseSettings(backend.backendType, backend.backendSettings)
    }
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function activate(backend) {
  const settings = settingsObjects.value[backend.backendType] || {}
  const backendSettings = Object.keys(settings).length > 0 ? JSON.stringify(settings) : null
  const response = await fetch('/api/admin/secrets-store/active', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      backendType: backend.backendType,
      backendSettings,
      // D-84: write-only -- protected server-side (ILocalSecretProtector)
      // and merged into BackendSettings as "ProtectedCredential", never
      // sent back on read. Left blank keeps whatever credential (if any)
      // is already stored.
      plaintextCredential: credentialDraft.value[backend.backendType] || null
    })
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  credentialDraft.value[backend.backendType] = ''
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
    <p>Exactly one backend is active at a time.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <table v-else>
      <thead>
        <tr><th>Backend</th><th>Active</th><th>Settings</th><th>Credential</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="backend in backends" :key="backend.secretStoreKey">
          <td>{{ backend.backendType }}</td>
          <td>{{ backend.isActive ? 'Yes' : '' }}</td>
          <td>
            <template v-if="backend.backendType === 'CyberArkCP'">
              <p><label>App ID: <input v-model="settingsObjects[backend.backendType].AppId" size="20" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'CyberArkCCP'">
              <p><label>Base URL: <input v-model="settingsObjects[backend.backendType].BaseUrl" size="30" placeholder="https://pvwa.company.com" /></label></p>
              <p><label>App ID: <input v-model="settingsObjects[backend.backendType].AppId" size="20" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'CyberArkConjur'">
              <p><label>Appliance URL: <input v-model="settingsObjects[backend.backendType].ApplianceUrl" size="30" /></label></p>
              <p><label>Account: <input v-model="settingsObjects[backend.backendType].Account" size="15" /></label></p>
              <p><label>Login: <input v-model="settingsObjects[backend.backendType].Login" size="15" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'AzureKeyVault'">
              <p><label>Vault URI: <input v-model="settingsObjects[backend.backendType].VaultUri" size="30" /></label></p>
              <p>
                <label>
                  Auth Method:
                  <select v-model="settingsObjects[backend.backendType].AuthMethod">
                    <option value="ManagedIdentity">ManagedIdentity</option>
                    <option value="ServicePrincipal">ServicePrincipal</option>
                  </select>
                </label>
              </p>
              <template v-if="settingsObjects[backend.backendType].AuthMethod === 'ServicePrincipal'">
                <p><label>Tenant ID: <input v-model="settingsObjects[backend.backendType].TenantId" size="20" /></label></p>
                <p><label>Client ID: <input v-model="settingsObjects[backend.backendType].ClientId" size="20" /></label></p>
              </template>
              <template v-else>
                <p><label>Client ID (only if using a user-assigned managed identity): <input v-model="settingsObjects[backend.backendType].ClientId" size="20" /></label></p>
              </template>
            </template>
            <template v-else-if="backend.backendType === 'AwsSecretsManager'">
              <p><label>Region: <input v-model="settingsObjects[backend.backendType].Region" size="15" placeholder="us-east-1" /></label></p>
              <p>
                <label>
                  Auth Method:
                  <select v-model="settingsObjects[backend.backendType].AuthMethod">
                    <option value="IamRole">IamRole</option>
                    <option value="AccessKey">AccessKey</option>
                  </select>
                </label>
              </p>
              <template v-if="settingsObjects[backend.backendType].AuthMethod === 'AccessKey'">
                <p><label>Access Key ID: <input v-model="settingsObjects[backend.backendType].AccessKeyId" size="25" /></label></p>
              </template>
            </template>
            <template v-else>
              <em>(no settings)</em>
            </template>
          </td>
          <td>
            <input
              v-model="credentialDraft[backend.backendType]"
              type="password"
              size="20"
              :placeholder="backend.backendSettings?.includes('ProtectedCredential') ? '(already set -- leave blank to keep)' : '(none set)'"
            />
          </td>
          <td><button @click="activate(backend)">{{ backend.isActive ? 'Save' : 'Make Active' }}</button></td>
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
      <p v-if="testResult.success" role="status">
        Success. UserName: {{ testResult.userName }}, Address: {{ testResult.address }}, password length: {{ testResult.passwordLength }}
        <span v-if="testResult.fromFallbackCache"> (served from fallback cache, D-49)</span>
      </p>
      <p v-else role="alert">Failed{{ testResult.errorCategory ? ` (${testResult.errorCategory})` : '' }}: {{ testResult.error }}</p>
    </div>
  </div>
</template>
