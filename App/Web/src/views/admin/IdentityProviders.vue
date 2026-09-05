<script setup>
// CRUD against /api/admin/identity-providers (IdentityProvidersController).
// WindowsIntegrated, DevFakeAuth, OIDC, and SAML are all wired at runtime
// now (D-84) -- OIDC's scheme is registered once at startup though
// (App/Api/Auth/AuthenticationExtensions.cs), so enabling/disabling it or
// changing its Authority/ClientId/secret here needs an app restart to take
// effect; SAML is read fresh on every request (Saml2ConfigurationFactory),
// so it needs no restart.
//
// D-95: OIDC/SAML's ConfigurationValues used to be a raw JSON textarea an
// admin had to hand-write against OidcProviderSettings/SamlProviderSettings'
// exact property names -- structured fields below, still serialized to the
// same ConfigurationValues JSON string on save (the API/database shape is
// unchanged; System.Text.Json's case-insensitive read means these camelCase
// keys deserialize the same as the PascalCase ones typed by hand before).
import { ref, onMounted } from 'vue'

const providers = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)
const configFields = ref({})

function defaultConfigFields(providerType) {
  if (providerType === 'OIDC') {
    return { authority: '', clientId: '', callbackPath: '/signin-oidc', groupsClaimType: 'groups' }
  }
  if (providerType === 'SAML') {
    return {
      spEntityId: '',
      spCertificateThumbprint: '',
      idpEntityId: '',
      idpSingleSignOnDestination: '',
      idpSingleLogoutDestination: '',
      idpCertificateThumbprint: '',
      groupClaimType: 'http://schemas.xmlsoap.org/claims/Group'
    }
  }
  return {}
}

function parseConfigFields(providerType, configurationValues) {
  const defaults = defaultConfigFields(providerType)
  if (!configurationValues) return defaults
  try {
    return { ...defaults, ...JSON.parse(configurationValues) }
  } catch {
    return defaults
  }
}

function onProviderTypeChange() {
  configFields.value = defaultConfigFields(editing.value.providerType)
}

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
  editing.value = { providerType: 'OIDC', displayName: '', isEnabled: false, displayOrder: 0, plaintextSecret: '' }
  configFields.value = defaultConfigFields('OIDC')
}
function startEdit(provider) {
  // secretReference comes back redacted ("***") when set -- never round-tripped
  // as an editable value. plaintextSecret is a separate write-only field.
  editing.value = { ...provider, plaintextSecret: '' }
  configFields.value = parseConfigFields(provider.providerType, provider.configurationValues)
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.providerKey === undefined
  const url = isNew ? '/api/admin/identity-providers' : `/api/admin/identity-providers/${editing.value.providerKey}`
  // secretReference is never sent back -- it may hold the redacted "***"
  // placeholder loaded from GET, and the server derives the real value
  // from plaintextSecret instead (or leaves the stored one alone if blank).
  const { providerType, displayName, isEnabled, displayOrder, plaintextSecret } = editing.value
  const configurationValues = ['OIDC', 'SAML'].includes(providerType) ? JSON.stringify(configFields.value) : null
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ providerType, displayName, isEnabled, displayOrder, configurationValues, plaintextSecret: plaintextSecret || null })
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
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

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
            <select v-model="editing.providerType" @change="onProviderTypeChange">
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

        <template v-if="editing.providerType === 'OIDC'">
          <p><label>Authority: <input v-model="configFields.authority" placeholder="https://login.microsoftonline.com/{tenant}/v2.0" /></label></p>
          <p><label>Client ID: <input v-model="configFields.clientId" /></label></p>
          <p><label>Callback Path: <input v-model="configFields.callbackPath" /></label></p>
          <p><label>Groups Claim Type: <input v-model="configFields.groupsClaimType" /></label></p>
        </template>
        <template v-else-if="editing.providerType === 'SAML'">
          <p><label>SP Entity ID: <input v-model="configFields.spEntityId" /></label></p>
          <p>
            <label>SP Certificate Thumbprint: <input v-model="configFields.spCertificateThumbprint" /></label>
            <br /><small>This app's own signing/decryption certificate, by thumbprint in the Windows Certificate Store (LocalMachine\My) -- not a certificate file or blob.</small>
          </p>
          <p><label>IdP Entity ID: <input v-model="configFields.idpEntityId" /></label></p>
          <p><label>IdP Single Sign-On Destination: <input v-model="configFields.idpSingleSignOnDestination" /></label></p>
          <p><label>IdP Single Logout Destination: <input v-model="configFields.idpSingleLogoutDestination" /></label></p>
          <p>
            <label>IdP Certificate Thumbprint: <input v-model="configFields.idpCertificateThumbprint" /></label>
            <br /><small>The IdP's signing certificate, by thumbprint in the Windows Certificate Store -- not a certificate file or blob.</small>
          </p>
          <p><label>Group Claim Type: <input v-model="configFields.groupClaimType" /></label></p>
        </template>

        <p>
          <label>
            Secret (e.g. OIDC client secret):
            <input v-model="editing.plaintextSecret" type="password" size="30"
              :placeholder="editing.secretReference ? '(already set -- leave blank to keep)' : '(none set)'" />
          </label>
        </p>
        <button type="submit">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
