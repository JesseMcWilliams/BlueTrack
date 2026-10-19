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
//
// D-124 Phase 4: Add/Edit (including the structured OIDC/SAML config field
// handling above) moved to its own routed page (IdentityProviderEdit.vue,
// admin-identity-provider-create/-edit) -- this page no longer owns an
// inline editing form.
//
// D-205: the default sign-in method (GET/PUT .../identity-providers/default;
// none = the sign-in page lists the methods), and each method's direct
// sign-in address -- what to put in an IdP's launch URL or hand to users.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import BreakGlassAccounts from './BreakGlassAccounts.vue'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'

const { t } = useI18n()

const router = useRouter()

const providers = ref([])
const error = ref(null)
const loading = ref(true)
const defaultProviderType = ref('')
const savingDefault = ref(false)
const defaultSaved = ref(false)

const SIGN_IN_PATHS = { WindowsIntegrated: 'windows', OIDC: 'oidc', SAML: 'saml' }

// One choice per sign-in type (the enabled provider with the lowest Order wins for its direct address).
const signInTypes = computed(() => Object.keys(SIGN_IN_PATHS).filter(type => providers.value.some(p => p.providerType === type)))

function directAddress(providerType) {
  return `${window.location.origin}/login/${SIGN_IN_PATHS[providerType]}`
}

function isEnabledType(providerType) {
  return providers.value.some(p => p.providerType === providerType && p.isEnabled)
}

async function load() {
  loading.value = true
  try {
    const [response, defaultResponse] = await Promise.all([
      fetch('/api/admin/identity-providers'),
      fetch('/api/admin/identity-providers/default')
    ])
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    providers.value = await response.json()
    if (defaultResponse.ok) defaultProviderType.value = (await defaultResponse.json()).providerType ?? ''
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function saveDefault() {
  savingDefault.value = true
  defaultSaved.value = false
  error.value = null
  try {
    const response = await fetch('/api/admin/identity-providers/default', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ providerType: defaultProviderType.value || null })
    })
    if (!response.ok) {
      error.value = t('admin.identityProviders.saveDefaultFailed', { status: response.status })
      return
    }
    defaultSaved.value = true
  } finally {
    savingDefault.value = false
  }
}

async function remove(provider) {
  if (!(await confirmDelete(t('admin.identityProviders.confirmDelete', { name: provider.displayName })))) return
  const response = await fetch(`/api/admin/identity-providers/${provider.providerKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.identityProviders.title') }}</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-identity-provider-create' })">{{ t('admin.identityProviders.newProvider') }}</button></p>

      <table>
        <thead>
          <tr><th>{{ t('admin.identityProviders.type') }}</th><th>{{ t('admin.identityProviders.displayName') }}</th><th>{{ t('common.enabled') }}</th><th>{{ t('admin.identityProviders.order') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="provider in providers" :key="provider.providerKey">
            <td>{{ provider.providerType }}</td>
            <td><router-link :to="{ name: 'admin-identity-provider-edit', params: { providerKey: provider.providerKey } }">{{ provider.displayName }}</router-link></td>
            <td>{{ provider.isEnabled ? t('common.yes') : t('common.no') }}</td>
            <td>{{ provider.displayOrder }}</td>
            <td>
              <button @click="remove(provider)" :aria-label="t('common.deleteNamed', { name: provider.displayName })">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="!loading && providers.length === 0" :message="t('admin.identityProviders.emptyNone')" />

      <h3>{{ t('admin.identityProviders.signInHeading') }}</h3>
      <form class="filter-row" @submit.prevent="saveDefault">
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.identityProviders.defaultMethod') }}</span>
          <select v-model="defaultProviderType" @change="defaultSaved = false">
            <option value="">{{ t('admin.identityProviders.noDefault') }}</option>
            <option v-for="type in signInTypes" :key="type" :value="type">{{ type }}{{ isEnabledType(type) ? '' : ' ' + t('admin.identityProviders.disabled') }}</option>
          </select>
        </label>
        <button type="submit" :disabled="savingDefault">{{ t('common.save') }}</button>
      </form>
      <p v-if="defaultSaved" role="status">{{ t('admin.identityProviders.defaultSaved') }}</p>
      <p>
        {{ t('admin.identityProviders.defaultHint') }}
      </p>
      <ul>
        <li v-for="type in signInTypes" :key="type"><code>{{ directAddress(type) }}</code> {{ type }}{{ isEnabledType(type) ? '' : ' ' + t('admin.identityProviders.disabled') }}</li>
      </ul>
      <!-- D-216 -->
      <BreakGlassAccounts />
    </template>
  </div>
</template>
