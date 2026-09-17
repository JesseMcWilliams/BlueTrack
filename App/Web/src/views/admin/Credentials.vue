<script setup>
// D-116: generic named-credential store (the SMTP account, the LDAP bind
// account, and whatever else needs one later) against /api/admin/credentials
// (CredentialsController) -- distinct from Secrets Store Configuration,
// which picks the one active backend for resolving privileged-account
// secrets a vault manages. Also hosts LDAP Configuration, a small singleton
// that just needs a bind-account Credential picked from the list below.
import { ref, onMounted } from 'vue'
import { confirmDelete } from '../../composables/useConfirmDialog'

const BACKEND_TYPES = ['WindowsDpapi', 'CyberArkCP', 'CyberArkCCP', 'CyberArkConjur', 'AzureKeyVault', 'AwsSecretsManager']

const credentials = ref([])
const editing = ref(null)
const plaintextPassword = ref('')
const error = ref(null)
const loading = ref(true)
const testResults = ref({})

const ldapConfigs = ref([])
const ldapEditing = ref(null)
const ldapError = ref(null)

async function load() {
  loading.value = true
  try {
    const [credentialsResponse, ldapResponse] = await Promise.all([
      fetch('/api/admin/credentials'),
      fetch('/api/admin/credentials/ldap-config')
    ])
    if (!credentialsResponse.ok) throw new Error(`Credentials request failed: ${credentialsResponse.status}`)
    if (!ldapResponse.ok) throw new Error(`LDAP config request failed: ${ldapResponse.status}`)
    credentials.value = await credentialsResponse.json()
    ldapConfigs.value = await ldapResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function startCreate() {
  editing.value = { credentialName: '', backendType: 'WindowsDpapi', username: '', scopePreference: 'Machine', vaultSafe: '', vaultFolder: '', vaultObject: '' }
  plaintextPassword.value = ''
}
function startEdit(credential) {
  editing.value = { ...credential }
  plaintextPassword.value = ''
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.credentialKey === undefined
  const url = isNew ? '/api/admin/credentials' : `/api/admin/credentials/${editing.value.credentialKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...editing.value, plaintextPassword: plaintextPassword.value || null })
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  editing.value = null
  await load()
}

async function remove(credential) {
  if (!(await confirmDelete(`Delete Credential "${credential.credentialName}"? This cannot be undone.`))) return
  const response = await fetch(`/api/admin/credentials/${credential.credentialKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a credential still in use elsewhere can't be deleted)`
    return
  }
  await load()
}

async function test(credential) {
  testResults.value = { ...testResults.value, [credential.credentialKey]: { testing: true } }
  const response = await fetch(`/api/admin/credentials/${credential.credentialKey}/test`, { method: 'POST' })
  const result = response.ok ? await response.json() : { success: false, error: `Request failed: ${response.status}` }
  testResults.value = { ...testResults.value, [credential.credentialKey]: result }
}

function startCreateLdapConfig() {
  ldapEditing.value = { domainName: '', isEnabled: false, domainController: '', searchBase: '', useSsl: false, useTrustedConnection: false, credentialKey: null }
}
function startEditLdapConfig(config) {
  ldapEditing.value = { ...config }
}
function cancelEditLdapConfig() {
  ldapEditing.value = null
}

async function saveLdapConfig() {
  ldapError.value = null
  const isNew = ldapEditing.value.ldapConfigKey === undefined
  const url = isNew ? '/api/admin/credentials/ldap-config' : `/api/admin/credentials/ldap-config/${ldapEditing.value.ldapConfigKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(ldapEditing.value)
  })
  if (!response.ok) {
    ldapError.value = `Save failed: ${response.status}`
    return
  }
  ldapEditing.value = null
  await load()
}

async function removeLdapConfig(config) {
  if (!(await confirmDelete(`Delete LDAP configuration "${config.domainName}"? This cannot be undone.`))) return
  const response = await fetch(`/api/admin/credentials/ldap-config/${config.ldapConfigKey}`, { method: 'DELETE' })
  if (!response.ok) {
    ldapError.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Credentials & LDAP</h2>
    <p>Named credentials this app authenticates as (the SMTP account, the LDAP bind account) -- pick a secrets backend per credential. Separate from Secrets Store Configuration, which is for retrieving privileged-account secrets a vault manages.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <h3>Credentials</h3>
      <button class="btn-primary" @click="startCreate">+ New Credential</button>

      <table>
        <thead>
          <tr><th>Name</th><th>Backend</th><th>Username</th><th>Scope</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="credential in credentials" :key="credential.credentialKey">
            <td>{{ credential.credentialName }}</td>
            <td>{{ credential.backendType }}</td>
            <td>{{ credential.username || credential.vaultObject }}</td>
            <td>
              <template v-if="credential.backendType === 'WindowsDpapi'">
                {{ credential.currentScope }}<span v-if="credential.scopePreference !== credential.currentScope"> (upgrading to {{ credential.scopePreference }})</span>
              </template>
            </td>
            <td>
              <button @click="startEdit(credential)">Edit</button>
              <button @click="remove(credential)">Delete</button>
              <button @click="test(credential)" :disabled="testResults[credential.credentialKey]?.testing">Test</button>
              <span v-if="testResults[credential.credentialKey]?.success === true" role="status"> OK ({{ testResults[credential.credentialKey].username }})</span>
              <span v-else-if="testResults[credential.credentialKey]?.success === false" role="alert"> {{ testResults[credential.credentialKey].error }}</span>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h4>{{ editing.credentialKey === undefined ? 'New Credential' : 'Edit Credential' }}</h4>
        <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.credentialName" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">Backend:</span>
            <select v-model="editing.backendType">
              <option v-for="type in BACKEND_TYPES" :key="type" :value="type">{{ type }}</option>
            </select>
          </label>
        </p>

        <template v-if="editing.backendType === 'WindowsDpapi'">
          <p><label class="field-label"><span class="field-label-text">Username:</span> <input v-model="editing.username" /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Password:</span>
              <input v-model="plaintextPassword" type="password" :placeholder="editing.credentialKey !== undefined ? '(leave blank to keep)' : ''" />
            </label>
          </p>
          <p>
            <label class="field-label">
              <span class="field-label-text">DPAPI Scope:</span>
              <select v-model="editing.scopePreference">
                <option value="Machine">Machine (any process on this server)</option>
                <option value="User">User (only this app pool's identity)</option>
              </select>
            </label>
            <small>A new "User" scope credential is stored as Machine scope until the app itself decrypts it once, then it upgrades automatically.</small>
          </p>
        </template>

        <template v-else>
          <p><label class="field-label"><span class="field-label-text">Safe:</span> <input v-model="editing.vaultSafe" /></label></p>
          <p><label class="field-label"><span class="field-label-text">Folder:</span> <input v-model="editing.vaultFolder" /></label></p>
          <p><label class="field-label"><span class="field-label-text">Object / Secret Name:</span> <input v-model="editing.vaultObject" /></label></p>
        </template>

        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

      <h3>LDAP Configuration</h3>
      <p>One row per AD domain/forest this app needs to query (notification recipient resolution, AD Account Discovery). Each is independently enabled/disabled and can bind with its own credential.</p>
      <p v-if="ldapError" role="alert">{{ ldapError }}</p>
      <button class="btn-primary" @click="startCreateLdapConfig">+ New Domain</button>

      <table>
        <thead>
          <tr><th>Domain</th><th>Enabled</th><th>Domain Controller</th><th>Bind Method</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="config in ldapConfigs" :key="config.ldapConfigKey">
            <td>{{ config.domainName }}</td>
            <td>{{ config.isEnabled ? 'Yes' : 'No' }}</td>
            <td>{{ config.domainController || '(default domain)' }}</td>
            <td>{{ config.useTrustedConnection ? 'Trusted connection' : (config.credentialName || '(none)') }}</td>
            <td>
              <button @click="startEditLdapConfig(config)">Edit</button>
              <button @click="removeLdapConfig(config)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="ldapEditing" @submit.prevent="saveLdapConfig">
        <h4>{{ ldapEditing.ldapConfigKey === undefined ? 'New Domain' : 'Edit Domain' }}</h4>
        <p><label class="field-label"><span class="field-label-text">Domain Name:</span> <input v-model="ldapEditing.domainName" required placeholder="company.local" /></label></p>
        <p><label><input v-model="ldapEditing.isEnabled" type="checkbox" /> Enabled</label></p>
        <p><label class="field-label"><span class="field-label-text">Domain Controller (blank = default domain):</span> <input v-model="ldapEditing.domainController" placeholder="dc01.company.local" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Search Base:</span> <input v-model="ldapEditing.searchBase" placeholder="DC=company,DC=local" /></label></p>
        <p><label><input v-model="ldapEditing.useSsl" type="checkbox" /> Use SSL</label></p>
        <p><label><input v-model="ldapEditing.useTrustedConnection" type="checkbox" /> Use trusted connection (app pool / local computer account)</label></p>
        <p v-if="!ldapEditing.useTrustedConnection">
          <label class="field-label">
            <span class="field-label-text">Bind Account Credential:</span>
            <select v-model="ldapEditing.credentialKey">
              <option :value="null">(none)</option>
              <option v-for="credential in credentials" :key="credential.credentialKey" :value="credential.credentialKey">{{ credential.credentialName }}</option>
            </select>
          </label>
        </p>
        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEditLdapConfig">Cancel</button>
      </form>
    </template>
  </div>
</template>
