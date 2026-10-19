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
import { flash, savedMessage } from '../../composables/useFlash'
import { problemMessage } from '../../utils/problemText'
import { mergeProviderSettings } from '../../utils/providerSettings'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

// The provider types, by their identifiers (not translated).
const PROVIDER_TYPES = ['WindowsIntegrated', 'OIDC', 'SAML', 'DevFakeAuth']

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

// Stored names are matched regardless of case (the seed script uses
// PascalCase), so saving writes one copy of each setting -- see
// utils/providerSettings.js.
function parseConfigFields(providerType, configurationValues) {
  return mergeProviderSettings(defaultConfigFields(providerType), configurationValues)
}

function onProviderTypeChange() {
  configFields.value = defaultConfigFields(editing.value.providerType)
}

onMounted(async () => {
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/identity-providers')
      if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
      const providers = await response.json()
      const match = providers.find(provider => String(provider.providerKey) === String(props.providerKey))
      if (!match) throw new Error(t('admin.identityProviderEdit.notFound'))
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
      saveError.value = await problemMessage(response, t('common.saveFailed', { status: response.status }))
      return
    }
    flash(savedMessage(t, editing.value.displayName))
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
    <h2>{{ isEditMode ? t('admin.identityProviderEdit.editTitle') : t('admin.identityProviderEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.identityProviderEdit.providerType') }}</span>
          <select v-model="editing.providerType" @change="onProviderTypeChange">
            <option v-for="type in PROVIDER_TYPES" :key="type" :value="type">{{ type }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.displayName') }}</span> <input v-model="editing.displayName" required /></label></p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('common.enabled') }}</label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.displayOrder') }}</span> <input v-model.number="editing.displayOrder" type="number" /></label></p>

      <template v-if="editing.providerType === 'OIDC'">
        <!-- UI review W1-5/W10-2: the OIDC scheme is registered once, at application startup (AuthController); SAML is read on every request. -->
        <p class="hint">{{ t('admin.identityProviderEdit.oidcRestartNote') }}</p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.authority') }}</span> <input v-model="configFields.authority" :placeholder="t('admin.identityProviderEdit.authorityPlaceholder')" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.clientId') }}</span> <input v-model="configFields.clientId" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.callbackPath') }}</span> <input v-model="configFields.callbackPath" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.groupsClaimType') }}</span> <input v-model="configFields.groupsClaimType" /></label></p>
      </template>
      <template v-else-if="editing.providerType === 'SAML'">
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.spEntityId') }}</span> <input v-model="configFields.spEntityId" /></label></p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.spCertificateThumbprint') }}</span> <input v-model="configFields.spCertificateThumbprint" /></label>
          <small>{{ t('admin.identityProviderEdit.spCertificateHint') }}</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.idpEntityId') }}</span> <input v-model="configFields.idpEntityId" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.idpSsoDestination') }}</span> <input v-model="configFields.idpSingleSignOnDestination" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.idpSloDestination') }}</span> <input v-model="configFields.idpSingleLogoutDestination" /></label></p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.idpCertificateThumbprint') }}</span> <input v-model="configFields.idpCertificateThumbprint" /></label>
          <small>{{ t('admin.identityProviderEdit.idpCertificateHint') }}</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.identityProviderEdit.groupClaimType') }}</span> <input v-model="configFields.groupClaimType" /></label></p>
      </template>

      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.identityProviderEdit.secret') }}</span>
          <input v-model="editing.plaintextSecret" type="password" size="30"
            :placeholder="editing.secretReference ? t('admin.identityProviderEdit.secretAlreadySet') : t('admin.identityProviderEdit.secretNoneSet')" />
        </label>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
