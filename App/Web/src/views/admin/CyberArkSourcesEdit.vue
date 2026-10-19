<script setup>
// D-211 (Data Sources phase 3): the CyberArk sources' settings, read each night
// by the SQL Agent job's Import step (usp_Import_Configured). Test works on the
// unsaved values: it checks the EVD database exists and, as the app pool sees
// them, the export folder and today's files.
// D-248: Self-Hosted can come from the EVD database or straight from PVWA's REST
// API (Self-Hosted Direct, pulled by the API each night at the pull time); for
// Direct, Test logs on with the chosen credential and counts what it can see.
import { ref, watch, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()
const route = useRoute()

// D-248: "Create a credential" leaves this page, so the unsaved values wait in sessionStorage for the way back
// (the Credentials page returns here with ?credentialKey=). A draft older than an hour is ignored.
const DRAFT_KEY = 'cyberArkSourcesDraft'
function rememberDraft() {
  try { sessionStorage.setItem(DRAFT_KEY, JSON.stringify({ at: Date.now(), editing: editing.value })) } catch { /* private window: the values are simply not kept */ }
}
function takeDraft() {
  try {
    const raw = sessionStorage.getItem(DRAFT_KEY)
    sessionStorage.removeItem(DRAFT_KEY)
    const draft = raw ? JSON.parse(raw) : null
    return draft && Date.now() - draft.at < 3600_000 ? draft.editing : null
  } catch { return null }
}

const fileFields = [
  { field: 'platformsFileName', labelKey: 'admin.cyberArkSourcesEdit.platformsFile' },
  { field: 'usersFileName', labelKey: 'admin.cyberArkSourcesEdit.usersFile' },
  { field: 'groupsFileName', labelKey: 'admin.cyberArkSourcesEdit.groupsFile' },
  { field: 'groupMembersFileName', labelKey: 'admin.cyberArkSourcesEdit.groupMembersFile' },
  { field: 'safesFileName', labelKey: 'admin.cyberArkSourcesEdit.safesFile' },
  { field: 'accountsFileName', labelKey: 'admin.cyberArkSourcesEdit.accountsFile' },
  { field: 'entitlementsFileName', labelKey: 'admin.cyberArkSourcesEdit.entitlementsFile' }
]

const loading = ref(true)
const saving = ref(false)
const testing = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const testError = ref(null)
const testChecks = ref(null)
const isConfigured = ref(true)
const editing = ref(null)
const credentials = ref(null)
const credentialsError = ref(null)

const selfHostedSources = [
  { value: 'Evd', labelKey: 'admin.cyberArkSourcesEdit.sourceEvd' },
  { value: 'Direct', labelKey: 'admin.cyberArkSourcesEdit.sourceDirect' }
]
const logonMethods = ['CyberArk', 'LDAP']

// The credential list is only needed for Direct, so it is fetched when Direct is first in play.
async function loadCredentials() {
  if (credentials.value || credentialsError.value) return
  try {
    const response = await fetch('/api/admin/cyberark-sources/credentials')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    credentials.value = await response.json()
  } catch (err) {
    credentialsError.value = err.message
  }
}

watch(() => editing.value && editing.value.importSelfHosted && editing.value.selfHostedSource === 'Direct', (direct) => {
  if (direct) loadCredentials()
})

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/cyberark-sources')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const body = await response.json()
    isConfigured.value = body.isConfigured
    editing.value = {
      ...body.settings,
      exportFolderPath: body.settings.exportFolderPath ?? '',
      evdDatabaseName: body.settings.evdDatabaseName ?? '',
      pvwaBaseUrl: body.settings.pvwaBaseUrl ?? ''
    }
    // Arriving from a credential ("Use for Self-Hosted Direct", or after creating one): pick Direct with that
    // credential, keeping whatever was typed before leaving this page. Nothing is saved until Save.
    const credentialKey = Number(route?.query?.credentialKey) || null
    if (credentialKey) {
      const draft = takeDraft()
      if (draft) editing.value = draft
      editing.value.importSelfHosted = true
      editing.value.selfHostedSource = 'Direct'
      editing.value.pvwaCredentialKey = credentialKey
    }
  } catch (err) {
    loadError.value = err.message
  }
  loading.value = false
})

async function test() {
  testing.value = true
  testChecks.value = null
  testError.value = null
  try {
    const response = await fetch('/api/admin/cyberark-sources/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    const body = await response.json().catch(() => null)
    if (!response.ok) {
      testError.value = body?.error ?? t('admin.cyberArkSourcesEdit.testFailed', { status: response.status })
      return
    }
    testChecks.value = body
  } finally {
    testing.value = false
  }
}

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/cyberark-sources', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    router.push({ name: 'admin-data-sources' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-data-sources' })
}
</script>

<template>
  <div>
    <h2>{{ t('admin.cyberArkSourcesEdit.title') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <p>
        {{ t('admin.cyberArkSourcesEdit.intro') }}
      </p>
      <p v-if="!isConfigured" role="status">
        {{ t('admin.cyberArkSourcesEdit.notConfigured') }}
      </p>

      <form @submit.prevent="save">
        <fieldset>
          <legend><label><input v-model="editing.importPrivilegeCloud" type="checkbox" /> {{ t('admin.cyberArkSourcesEdit.importPrivilegeCloud') }}</label></legend>
          <template v-if="editing.importPrivilegeCloud">
            <p>
              <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.exportFolderLabel') }}</span> <input v-model="editing.exportFolderPath" maxlength="400" :placeholder="t('admin.cyberArkSourcesEdit.exportFolderPlaceholder')" required /></label>
              <small>{{ t('admin.cyberArkSourcesEdit.exportFolderHint') }}</small>
            </p>
            <p v-for="f in fileFields" :key="f.field">
              <label class="field-label"><span class="field-label-text">{{ t(f.labelKey) }}</span> <input v-model="editing[f.field]" maxlength="200" required /></label>
            </p>
            <p><small>{{ t('admin.cyberArkSourcesEdit.dateTokenHint') }}</small></p>
          </template>
        </fieldset>

        <fieldset>
          <legend><label><input v-model="editing.importSelfHosted" type="checkbox" /> {{ t('admin.cyberArkSourcesEdit.importSelfHosted') }}</label></legend>
          <template v-if="editing.importSelfHosted">
            <p>
              <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.selfHostedSourceLabel') }}</span>
                <select v-model="editing.selfHostedSource">
                  <option v-for="o in selfHostedSources" :key="o.value" :value="o.value">{{ t(o.labelKey) }}</option>
                </select>
              </label>
            </p>
            <p v-if="editing.selfHostedSource === 'Evd'">
              <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.evdDatabaseLabel') }}</span> <input v-model="editing.evdDatabaseName" maxlength="128" :placeholder="t('admin.cyberArkSourcesEdit.evdDatabasePlaceholder')" required /></label>
              <small>{{ t('admin.cyberArkSourcesEdit.evdDatabaseHint') }}</small>
            </p>
            <template v-else>
              <p>{{ t('admin.cyberArkSourcesEdit.directIntro') }}</p>
              <p>
                <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.pvwaAddressLabel') }}</span> <input v-model="editing.pvwaBaseUrl" maxlength="400" :placeholder="t('admin.cyberArkSourcesEdit.pvwaAddressPlaceholder')" required /></label>
                <small>{{ t('admin.cyberArkSourcesEdit.pvwaAddressHint') }}</small>
              </p>
              <p>
                <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.credentialLabel') }}</span>
                  <select v-model="editing.pvwaCredentialKey" required>
                    <option :value="null" disabled>{{ t('admin.cyberArkSourcesEdit.chooseCredential') }}</option>
                    <option v-for="c in credentials ?? []" :key="c.credentialKey" :value="c.credentialKey">{{ c.credentialName }} ({{ c.backendType }})</option>
                  </select>
                </label>
                <small>{{ t('admin.cyberArkSourcesEdit.credentialHint') }}</small>
              </p>
              <p>
                <router-link :to="{ name: 'admin-credentials', query: { new: 'CyberArkCP', returnTo: 'cyberark-sources' } }" @click="rememberDraft">{{ t('admin.cyberArkSourcesEdit.createCredential') }}</router-link>
                <small>{{ ' ' + t('admin.cyberArkSourcesEdit.createCredentialHint') }}</small>
              </p>
              <p v-if="credentialsError" role="alert">{{ credentialsError }}</p>
              <p>
                <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.logonMethodLabel') }}</span>
                  <select v-model="editing.pvwaLogonMethod">
                    <option v-for="m in logonMethods" :key="m" :value="m">{{ m }}</option>
                  </select>
                </label>
              </p>
              <p>
                <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.pullTimeLabel') }}</span> <input v-model="editing.directPullTime" maxlength="5" pattern="([01][0-9]|2[0-3]):[0-5][0-9]" required /></label>
                <small>{{ t('admin.cyberArkSourcesEdit.pullTimeHint') }}</small>
              </p>
              <p>
                <label class="field-label"><span class="field-label-text">{{ t('admin.cyberArkSourcesEdit.maxChangeLabel') }}</span> <input v-model.number="editing.directMaxChangePercent" type="number" min="0" max="100" required /></label>
                <small>{{ t('admin.cyberArkSourcesEdit.maxChangeHint') }}</small>
              </p>
            </template>
          </template>
        </fieldset>
        <p><small>{{ t('admin.cyberArkSourcesEdit.plannedSources') }}</small></p>

        <p>
          <button type="button" :disabled="testing" @click="test">{{ t('admin.cyberArkSourcesEdit.test') }}</button>
          <span v-if="testing" role="status">{{ ' ' + t('admin.cyberArkSourcesEdit.testing') }}</span>
        </p>
        <p v-if="testError" role="alert">{{ testError }}</p>
        <div v-if="testChecks" class="test-result" role="status">
          <ul>
            <li v-for="c in testChecks" :key="c.item" :class="c.ok ? 'check-ok' : 'check-failed'">{{ c.ok ? t('admin.cyberArkSourcesEdit.checkOk', { item: c.item, message: c.message }) : t('admin.cyberArkSourcesEdit.checkProblem', { item: c.item, message: c.message }) }}</li>
          </ul>
          <p v-if="editing.importPrivilegeCloud"><small>{{ t('admin.cyberArkSourcesEdit.testFilesHint') }}</small></p>
        </div>

        <p v-if="saveError" role="alert">{{ saveError }}</p>
        <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
        <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
      </form>
    </template>
  </div>
</template>

<style scoped>
.check-failed { color: var(--color-error, #b00020); }
</style>
