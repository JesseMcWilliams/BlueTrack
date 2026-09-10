<script setup>
// D-124 Phase 4: Add/Edit moved off IdentityProviders.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit/onProviderTypeChange state
// onto this routed page, including the D-95 structured OIDC/SAML config
// field handling. GetAllAsync returns the full, unpaginated provider
// catalog (a small admin-curated list), so edit mode fetches the full list
// and finds the matching row by key rather than needing a new GET-by-id
// endpoint -- no API contract change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ providerKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.providerKey !== null && props.providerKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ providerType: 'OIDC', displayName: '', isEnabled: false, displayOrder: 0, plaintextSecret: '' })
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

onMounted(async () => {
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/identity-providers')
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const providers = await response.json()
      const match = providers.find(provider => String(provider.providerKey) === String(props.providerKey))
      if (!match) throw new Error('Provider not found.')
      // secretReference comes back redacted ("***") when set -- never
      // round-tripped as an editable value. plaintextSecret is a separate
      // write-only field.
      editing.value = { ...match, plaintextSecret: '' }
      configFields.value = parseConfigFields(match.providerType, match.configurationValues)
    } catch (err) {
      loadError.value = err.message
    }
  } else {
    configFields.value = defaultConfigFields('OIDC')
  }
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/identity-providers/${props.providerKey}` : '/api/admin/identity-providers'
    // secretReference is never sent back -- it may hold the redacted "***"
    // placeholder loaded from GET, and the server derives the real value
    // from plaintextSecret instead (or leaves the stored one alone if blank).
    const { providerType, displayName, isEnabled, displayOrder, plaintextSecret } = editing.value
    const configurationValues = ['OIDC', 'SAML'].includes(providerType) ? JSON.stringify(configFields.value) : null
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ providerType, displayName, isEnabled, displayOrder, configurationValues, plaintextSecret: plaintextSecret || null })
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-identity-providers' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-identity-providers' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Provider' : 'New Provider' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label">
          <span class="field-label-text">Provider Type:</span>
          <select v-model="editing.providerType" @change="onProviderTypeChange">
            <option value="WindowsIntegrated">WindowsIntegrated</option>
            <option value="OIDC">OIDC</option>
            <option value="SAML">SAML</option>
            <option value="DevFakeAuth">DevFakeAuth</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">Display Name:</span> <input v-model="editing.displayName" required /></label></p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> Enabled</label></p>
      <p><label class="field-label"><span class="field-label-text">Display Order:</span> <input v-model.number="editing.displayOrder" type="number" /></label></p>

      <template v-if="editing.providerType === 'OIDC'">
        <p><label class="field-label"><span class="field-label-text">Authority:</span> <input v-model="configFields.authority" placeholder="https://login.microsoftonline.com/{tenant}/v2.0" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Client ID:</span> <input v-model="configFields.clientId" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Callback Path:</span> <input v-model="configFields.callbackPath" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Groups Claim Type:</span> <input v-model="configFields.groupsClaimType" /></label></p>
      </template>
      <template v-else-if="editing.providerType === 'SAML'">
        <p><label class="field-label"><span class="field-label-text">SP Entity ID:</span> <input v-model="configFields.spEntityId" /></label></p>
        <p>
          <label class="field-label"><span class="field-label-text">SP Certificate Thumbprint:</span> <input v-model="configFields.spCertificateThumbprint" /></label>
          <small>This app's own signing/decryption certificate, by thumbprint in the Windows Certificate Store (LocalMachine\My) -- not a certificate file or blob.</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">IdP Entity ID:</span> <input v-model="configFields.idpEntityId" /></label></p>
        <p><label class="field-label"><span class="field-label-text">IdP Single Sign-On Destination:</span> <input v-model="configFields.idpSingleSignOnDestination" /></label></p>
        <p><label class="field-label"><span class="field-label-text">IdP Single Logout Destination:</span> <input v-model="configFields.idpSingleLogoutDestination" /></label></p>
        <p>
          <label class="field-label"><span class="field-label-text">IdP Certificate Thumbprint:</span> <input v-model="configFields.idpCertificateThumbprint" /></label>
          <small>The IdP's signing certificate, by thumbprint in the Windows Certificate Store -- not a certificate file or blob.</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">Group Claim Type:</span> <input v-model="configFields.groupClaimType" /></label></p>
      </template>

      <p>
        <label class="field-label">
          <span class="field-label-text">Secret (e.g. OIDC client secret):</span>
          <input v-model="editing.plaintextSecret" type="password" size="30"
            :placeholder="editing.secretReference ? '(already set -- leave blank to keep)' : '(none set)'" />
        </label>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
