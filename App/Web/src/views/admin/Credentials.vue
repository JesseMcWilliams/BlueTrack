<script setup>
// D-116: generic named-credential store (the SMTP account, the LDAP bind
// account, and whatever else needs one later) against /api/admin/credentials
// (CredentialsController) -- distinct from Secrets Store Configuration,
// which picks the one active backend for resolving privileged-account
// secrets a vault manages. Also hosts LDAP Configuration, a small singleton
// that just needs a bind-account Credential picked from the list below.
// D-248: each credential links to the CyberArk sources page ("Use for Self-Hosted Direct"),
// and the sources page links here with ?new=<backend>&returnTo=cyberark-sources to create
// one; saving that new credential goes back there with it chosen.
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useRightsStore } from '../../stores/rights'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const returnToSources = ref(false)
const rights = useRightsStore()
// The link goes to a page that needs ManageDataSources; without it the link would only lead to "access denied".
const canConfigureSources = computed(() => rights.hasPermission('ManageDataSources'))

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
    if (!credentialsResponse.ok) throw new Error(t('admin.credentials.credentialsRequestFailed', { status: credentialsResponse.status }))
    if (!ldapResponse.ok) throw new Error(t('admin.credentials.ldapRequestFailed', { status: ldapResponse.status }))
    credentials.value = await credentialsResponse.json()
    ldapConfigs.value = await ldapResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  await load()
  const wanted = route?.query?.new
  if (wanted) {
    startCreate()
    if (BACKEND_TYPES.includes(wanted)) editing.value.backendType = wanted
    returnToSources.value = route.query.returnTo === 'cyberark-sources'
  }
})

const isCyberArkVault = computed(() => editing.value?.backendType === 'CyberArkCP' || editing.value?.backendType === 'CyberArkCCP')

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
    const problem = await response.json().catch(() => null)
    error.value = problem?.detail ?? problem?.title ?? t('common.saveFailed', { status: response.status })
    return
  }
  const created = isNew ? await response.json().catch(() => null) : null
  editing.value = null
  if (created?.credentialKey && returnToSources.value) {
    router.push({ name: 'admin-cyberark-sources-edit', query: { credentialKey: created.credentialKey } })
    return
  }
  await load()
}

async function remove(credential) {
  if (!(await confirmDelete(t('admin.credentials.confirmDeleteCredential', { name: credential.credentialName })))) return
  const response = await fetch(`/api/admin/credentials/${credential.credentialKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('admin.credentials.deleteFailedInUse', { status: response.status })
    return
  }
  await load()
}

async function test(credential) {
  testResults.value = { ...testResults.value, [credential.credentialKey]: { testing: true } }
  const response = await fetch(`/api/admin/credentials/${credential.credentialKey}/test`, { method: 'POST' })
  const result = response.ok ? await response.json() : { success: false, error: t('common.requestFailed', { status: response.status }) }
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
    ldapError.value = t('common.saveFailed', { status: response.status })
    return
  }
  ldapEditing.value = null
  await load()
}

async function removeLdapConfig(config) {
  if (!(await confirmDelete(t('admin.credentials.confirmDeleteLdap', { name: config.domainName })))) return
  const response = await fetch(`/api/admin/credentials/ldap-config/${config.ldapConfigKey}`, { method: 'DELETE' })
  if (!response.ok) {
    ldapError.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.credentials.title') }}</h2>
    <p>{{ t('admin.credentials.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <h3>{{ t('admin.credentials.credentialsHeading') }}</h3>
      <button class="btn-primary" @click="startCreate">{{ t('admin.credentials.newCredentialButton') }}</button>

      <table>
        <thead>
          <tr><th>{{ t('common.name') }}</th><th>{{ t('admin.credentials.backend') }}</th><th>{{ t('admin.credentials.username') }}</th><th>{{ t('admin.credentials.scope') }}</th><th>{{ t('admin.credentials.usedBy') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="credential in credentials" :key="credential.credentialKey">
            <td>{{ credential.credentialName }}</td>
            <td>{{ credential.backendType }}</td>
            <td>{{ credential.username || credential.vaultObject }}</td>
            <td>
              <template v-if="credential.backendType === 'WindowsDpapi'">
                {{ credential.currentScope }}<span v-if="credential.scopePreference !== credential.currentScope">{{ ' ' + t('admin.credentials.upgradingTo', { scope: credential.scopePreference }) }}</span>
              </template>
            </td>
            <td>{{ (credential.usedBy ?? []).join('; ') }}</td>
            <td>
              <button @click="startEdit(credential)">{{ t('common.edit') }}</button>
              <router-link v-if="canConfigureSources" :to="{ name: 'admin-cyberark-sources-edit', query: { credentialKey: credential.credentialKey } }">{{ t('admin.credentials.useForSelfHosted') }}</router-link>
              <button @click="remove(credential)" :disabled="(credential.usedBy ?? []).length > 0" :title="(credential.usedBy ?? []).length > 0 ? t('admin.credentials.inUseCannotDelete') : ''">{{ t('common.delete') }}</button>
              <button @click="test(credential)" :disabled="testResults[credential.credentialKey]?.testing">{{ t('admin.credentials.test') }}</button>
              <span v-if="testResults[credential.credentialKey]?.success === true" role="status">{{ ' ' + t('admin.credentials.testOk', { username: testResults[credential.credentialKey].username }) }}</span>
              <span v-else-if="testResults[credential.credentialKey]?.success === false" role="alert"> {{ testResults[credential.credentialKey].error }}</span>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h4>{{ editing.credentialKey === undefined ? t('admin.credentials.newCredential') : t('admin.credentials.editCredential') }}</h4>
        <p v-if="returnToSources && editing.credentialKey === undefined" role="status">{{ t('admin.credentials.returnToSources') }}</p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.nameLabel') }}</span> <input v-model="editing.credentialName" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.credentials.backendLabel') }}</span>
            <select v-model="editing.backendType">
              <option v-for="type in BACKEND_TYPES" :key="type" :value="type">{{ type }}</option>
            </select>
          </label>
        </p>

        <template v-if="editing.backendType === 'WindowsDpapi'">
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.usernameLabel') }}</span> <input v-model="editing.username" /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.credentials.passwordLabel') }}</span>
              <input v-model="plaintextPassword" type="password" :placeholder="editing.credentialKey !== undefined ? t('admin.credentials.leaveBlankToKeep') : ''" />
            </label>
          </p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.credentials.dpapiScopeLabel') }}</span>
              <select v-model="editing.scopePreference">
                <option value="Machine">{{ t('admin.credentials.scopeMachine') }}</option>
                <option value="User">{{ t('admin.credentials.scopeUser') }}</option>
              </select>
            </label>
            <small>{{ t('admin.credentials.scopeHint') }}</small>
          </p>
        </template>

        <template v-else>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.safeLabel') }}</span> <input v-model="editing.vaultSafe" /></label></p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.credentials.folderLabel') }}</span> <input v-model="editing.vaultFolder" :placeholder="isCyberArkVault ? t('admin.credentials.folderPlaceholder') : ''" /></label>
            <small v-if="isCyberArkVault">{{ t('admin.credentials.folderHint') }}</small>
          </p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.objectLabel') }}</span> <input v-model="editing.vaultObject" /></label></p>
        </template>

        <button type="submit" class="btn-primary">{{ t('common.save') }}</button>
        <button type="button" @click="cancelEdit">{{ t('common.cancel') }}</button>
        <router-link v-if="returnToSources" :to="{ name: 'admin-cyberark-sources-edit' }">{{ t('admin.credentials.cancelAndReturn') }}</router-link>
      </form>

      <h3>{{ t('admin.credentials.ldapHeading') }}</h3>
      <p>{{ t('admin.credentials.ldapIntro') }}</p>
      <p v-if="ldapError" role="alert">{{ ldapError }}</p>
      <button class="btn-primary" @click="startCreateLdapConfig">{{ t('admin.credentials.newDomainButton') }}</button>

      <table>
        <thead>
          <tr><th>{{ t('admin.credentials.domain') }}</th><th>{{ t('common.enabled') }}</th><th>{{ t('admin.credentials.domainController') }}</th><th>{{ t('admin.credentials.bindMethod') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="config in ldapConfigs" :key="config.ldapConfigKey">
            <td>{{ config.domainName }}</td>
            <td>{{ config.isEnabled ? t('admin.credentials.yes') : t('admin.credentials.no') }}</td>
            <td>{{ config.domainController || t('admin.credentials.defaultDomain') }}</td>
            <td>{{ config.useTrustedConnection ? t('admin.credentials.trustedConnection') : (config.credentialName || t('common.none')) }}</td>
            <td>
              <button @click="startEditLdapConfig(config)">{{ t('common.edit') }}</button>
              <button @click="removeLdapConfig(config)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="ldapEditing" @submit.prevent="saveLdapConfig">
        <h4>{{ ldapEditing.ldapConfigKey === undefined ? t('admin.credentials.newDomain') : t('admin.credentials.editDomain') }}</h4>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.domainNameLabel') }}</span> <input v-model="ldapEditing.domainName" required :placeholder="t('admin.credentials.domainNamePlaceholder')" /></label></p>
        <p><label><input v-model="ldapEditing.isEnabled" type="checkbox" /> {{ t('common.enabled') }}</label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.domainControllerLabel') }}</span> <input v-model="ldapEditing.domainController" :placeholder="t('admin.credentials.domainControllerPlaceholder')" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.credentials.searchBaseLabel') }}</span> <input v-model="ldapEditing.searchBase" :placeholder="t('admin.credentials.searchBasePlaceholder')" /></label></p>
        <p><label><input v-model="ldapEditing.useSsl" type="checkbox" /> {{ t('admin.credentials.useSsl') }}</label></p>
        <p><label><input v-model="ldapEditing.useTrustedConnection" type="checkbox" /> {{ t('admin.credentials.useTrustedConnection') }}</label></p>
        <p v-if="!ldapEditing.useTrustedConnection">
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.credentials.bindCredentialLabel') }}</span>
            <select v-model="ldapEditing.credentialKey">
              <option :value="null">{{ t('common.none') }}</option>
              <option v-for="credential in credentials" :key="credential.credentialKey" :value="credential.credentialKey">{{ credential.credentialName }}</option>
            </select>
          </label>
        </p>
        <button type="submit" class="btn-primary">{{ t('common.save') }}</button>
        <button type="button" @click="cancelEditLdapConfig">{{ t('common.cancel') }}</button>
      </form>
    </template>
  </div>
</template>
