<script setup>
// Against /api/admin/secrets-store (SecretsStoreController). Windows DPAPI,
// CyberArk CP/CCP, Azure Key Vault, AWS Secrets Manager, and CyberArk
// Conjur are all implemented (Design_Secrets-Storage.md, D-84) -- this page
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
// untouched and sent back on save. With no new PlaintextCredential,
// SetActiveAsync swaps that "***" for the stored credential.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'

const { t } = useI18n()

// Auth method identifiers, shown as they are (not translated).
const AZURE_AUTH_METHODS = ['ManagedIdentity', 'ServicePrincipal']
const AWS_AUTH_METHODS = ['IamRole', 'AccessKey']

const backends = ref([])
const error = ref(null)
const notice = ref(null)
const loading = ref(true)
const settingsObjects = ref({})
const credentialDraft = ref({})

const testSafe = ref('')
const testFolder = ref('Root')
const testObject = ref('')
const testResult = ref(null)
const testing = ref(false)

// D-118: WindowsDpapi has no Safe/Folder hierarchy of its own -- Object
// names a web.credential row directly, Safe/Folder are accepted but ignored.
const activeBackendIsDpapi = computed(() => backends.value.find(b => b.isActive)?.backendType === 'WindowsDpapi')

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
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
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
  error.value = null
  notice.value = null
  const wasActive = backend.isActive
  // UI review W10-1: making a different backend active switches where the application looks secrets up, so ask first,
  // naming it. Saving the settings of the one that is already active needs no question.
  if (!wasActive) {
    const confirmed = await confirmDelete({
      title: t('admin.secretsStoreConfiguration.activateTitle', { name: backend.backendType }),
      details: [t('admin.secretsStoreConfiguration.activateDetail', { name: backend.backendType })],
      warning: t('admin.secretsStoreConfiguration.activateWarning', { name: backend.backendType })
    }, t('admin.secretsStoreConfiguration.makeActive'))
    if (!confirmed) return
  }
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
    const problem = await response.json().catch(() => null)
    error.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
    return
  }
  credentialDraft.value[backend.backendType] = ''
  await load()
  notice.value = wasActive
    ? t('admin.secretsStoreConfiguration.saved', { name: backend.backendType })
    : t('admin.secretsStoreConfiguration.activated', { name: backend.backendType })
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
    testResult.value = response.ok ? await response.json() : { success: false, error: t('common.requestFailed', { status: response.status }) }
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ t('admin.secretsStoreConfiguration.title') }}</h2>
    <p>{{ t('admin.secretsStoreConfiguration.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <table v-else>
      <thead>
        <tr><th>{{ t('admin.secretsStoreConfiguration.backend') }}</th><th>{{ t('common.active') }}</th><th>{{ t('admin.secretsStoreConfiguration.settings') }}</th><th>{{ t('admin.secretsStoreConfiguration.credential') }}</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="backend in backends" :key="backend.secretStoreKey">
          <td>{{ backend.backendType }}</td>
          <td>{{ backend.isActive ? t('admin.secretsStoreConfiguration.yes') : '' }}</td>
          <td>
            <template v-if="backend.backendType === 'CyberArkCP'">
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.appId') }}</span> <input v-model="settingsObjects[backend.backendType].AppId" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'CyberArkCCP'">
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.baseUrl') }}</span> <input v-model="settingsObjects[backend.backendType].BaseUrl" :placeholder="t('admin.secretsStoreConfiguration.baseUrlPlaceholder')" /></label></p>
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.appId') }}</span> <input v-model="settingsObjects[backend.backendType].AppId" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'CyberArkConjur'">
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.applianceUrl') }}</span> <input v-model="settingsObjects[backend.backendType].ApplianceUrl" /></label></p>
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.account') }}</span> <input v-model="settingsObjects[backend.backendType].Account" /></label></p>
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.login') }}</span> <input v-model="settingsObjects[backend.backendType].Login" /></label></p>
            </template>
            <template v-else-if="backend.backendType === 'AzureKeyVault'">
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.vaultUri') }}</span> <input v-model="settingsObjects[backend.backendType].VaultUri" /></label></p>
              <p>
                <label class="field-label">
                  <span class="field-label-text">{{ t('admin.secretsStoreConfiguration.authMethod') }}</span>
                  <select v-model="settingsObjects[backend.backendType].AuthMethod">
                    <option v-for="method in AZURE_AUTH_METHODS" :key="method" :value="method">{{ method }}</option>
                  </select>
                </label>
              </p>
              <template v-if="settingsObjects[backend.backendType].AuthMethod === 'ServicePrincipal'">
                <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.tenantId') }}</span> <input v-model="settingsObjects[backend.backendType].TenantId" /></label></p>
                <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.clientId') }}</span> <input v-model="settingsObjects[backend.backendType].ClientId" /></label></p>
              </template>
              <template v-else>
                <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.clientIdManagedIdentity') }}</span> <input v-model="settingsObjects[backend.backendType].ClientId" /></label></p>
              </template>
            </template>
            <template v-else-if="backend.backendType === 'AwsSecretsManager'">
              <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.region') }}</span> <input v-model="settingsObjects[backend.backendType].Region" :placeholder="t('admin.secretsStoreConfiguration.regionPlaceholder')" /></label></p>
              <p>
                <label class="field-label">
                  <span class="field-label-text">{{ t('admin.secretsStoreConfiguration.authMethod') }}</span>
                  <select v-model="settingsObjects[backend.backendType].AuthMethod">
                    <option v-for="method in AWS_AUTH_METHODS" :key="method" :value="method">{{ method }}</option>
                  </select>
                </label>
              </p>
              <template v-if="settingsObjects[backend.backendType].AuthMethod === 'AccessKey'">
                <p><label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.accessKeyId') }}</span> <input v-model="settingsObjects[backend.backendType].AccessKeyId" /></label></p>
              </template>
            </template>
            <template v-else>
              <em>{{ t('admin.secretsStoreConfiguration.noSettings') }}</em>
            </template>
          </td>
          <td>
            <input
              v-model="credentialDraft[backend.backendType]"
              type="password"
              :aria-label="t('admin.secretsStoreConfiguration.credentialFor', { backend: backend.backendType })"
              :placeholder="backend.backendSettings?.includes('ProtectedCredential') ? t('admin.secretsStoreConfiguration.alreadySet') : t('admin.secretsStoreConfiguration.noneSet')"
            />
          </td>
          <td><button class="btn-primary" @click="activate(backend)">{{ backend.isActive ? t('common.save') : t('admin.secretsStoreConfiguration.makeActive') }}</button></td>
        </tr>
      </tbody>
    </table>

    <h3>{{ t('admin.secretsStoreConfiguration.testHeading') }}</h3>
    <p>{{ t('admin.secretsStoreConfiguration.testIntro') }}</p>
    <i18n-t v-if="activeBackendIsDpapi" keypath="admin.secretsStoreConfiguration.dpapiHint" tag="p" scope="global">
      <template #link><router-link :to="{ name: 'admin-credentials' }">{{ t('admin.secretsStoreConfiguration.credentialsLink') }}</router-link></template>
    </i18n-t>
    <form class="filter-row" @submit.prevent="testConnection">
      <label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.safe') }}</span> <input v-model="testSafe" required /></label>
      <label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.folder') }}</span> <input v-model="testFolder" required /></label>
      <label class="field-label"><span class="field-label-text">{{ t('admin.secretsStoreConfiguration.object') }}</span> <input v-model="testObject" required /></label>
      <button type="submit" class="btn-primary" :disabled="testing">{{ t('admin.secretsStoreConfiguration.test') }}</button>
    </form>
    <div v-if="testResult">
      <p v-if="testResult.success" role="status">
        {{ t('admin.secretsStoreConfiguration.testSuccess', { userName: testResult.userName, address: testResult.address }) }}
        <span v-if="testResult.fromFallbackCache">{{ ' ' + t('admin.secretsStoreConfiguration.fromFallbackCache') }}</span>
      </p>
      <p v-else role="alert">{{ testResult.errorCategory ? t('admin.secretsStoreConfiguration.testFailedCategory', { category: testResult.errorCategory, error: testResult.error }) : t('admin.secretsStoreConfiguration.testFailed', { error: testResult.error }) }}</p>
    </div>
  </div>
</template>
