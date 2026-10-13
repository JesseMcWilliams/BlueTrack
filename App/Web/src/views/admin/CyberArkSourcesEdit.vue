<script setup>
// D-211 (Data Sources phase 3): the CyberArk sources' settings, read each night
// by the SQL Agent job's Import step (usp_Import_Configured). Test works on the
// unsaved values: it checks the EVD database exists and, as the app pool sees
// them, the export folder and today's files.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const fileFields = [
  { field: 'platformsFileName', label: 'Platforms' },
  { field: 'usersFileName', label: 'Users' },
  { field: 'groupsFileName', label: 'Groups' },
  { field: 'groupMembersFileName', label: 'Local group members' },
  { field: 'safesFileName', label: 'Safes' },
  { field: 'accountsFileName', label: 'Accounts' },
  { field: 'entitlementsFileName', label: 'Entitlements' }
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

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/cyberark-sources')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const body = await response.json()
    isConfigured.value = body.isConfigured
    editing.value = { ...body.settings, exportFolderPath: body.settings.exportFolderPath ?? '', evdDatabaseName: body.settings.evdDatabaseName ?? '' }
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
      testError.value = body?.error ?? `Test failed: ${response.status}`
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
      saveError.value = problem?.detail ?? `Save failed: ${response.status}`
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
    <h2>CyberArk Sources</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <p>
        The nightly SQL Agent job imports these sources, then loads them. They don't have Run now.
        Changes apply from the next nightly run.
      </p>
      <p v-if="!isConfigured" role="status">
        Not set up yet: the nightly import fails until these settings are saved. Re-running the installer's nightly job step also saves them.
      </p>

      <form @submit.prevent="save">
        <fieldset>
          <legend><label><input v-model="editing.importPrivilegeCloud" type="checkbox" /> Import Privilege Cloud</label></legend>
          <template v-if="editing.importPrivilegeCloud">
            <p>
              <label class="field-label"><span class="field-label-text">Export folder:</span> <input v-model="editing.exportFolderPath" maxlength="400" placeholder="D:\CyberArk\Exports" required /></label>
              <small>SQL Server's service account reads the files, so it needs read access to this folder.</small>
            </p>
            <p v-for="f in fileFields" :key="f.field">
              <label class="field-label"><span class="field-label-text">{{ f.label }} file:</span> <input v-model="editing[f.field]" maxlength="200" required /></label>
            </p>
            <p><small>{yyyy-MM-dd} (or another date format in braces) is replaced with the run's date. Wildcards aren't allowed.</small></p>
          </template>
        </fieldset>

        <fieldset>
          <legend><label><input v-model="editing.importSelfHosted" type="checkbox" /> Import Self-Hosted</label></legend>
          <p v-if="editing.importSelfHosted">
            <label class="field-label"><span class="field-label-text">EVD database:</span> <input v-model="editing.evdDatabaseName" maxlength="128" placeholder="CyberArkEVD" required /></label>
            <small>A database on BlueTrack's SQL Server.</small>
          </p>
        </fieldset>

        <p>
          <button type="button" :disabled="testing" @click="test">Test</button>
          <span v-if="testing" role="status"> Testing...</span>
        </p>
        <p v-if="testError" role="alert">{{ testError }}</p>
        <div v-if="testChecks" class="test-result" role="status">
          <ul>
            <li v-for="c in testChecks" :key="c.item" :class="c.ok ? 'check-ok' : 'check-failed'">{{ c.ok ? 'OK' : 'Problem' }}: {{ c.item }}: {{ c.message }}</li>
          </ul>
          <p v-if="editing.importPrivilegeCloud"><small>Files were checked as the application pool's account sees them; SQL Server reads them as its own service account.</small></p>
        </div>

        <p v-if="saveError" role="alert">{{ saveError }}</p>
        <button type="submit" class="btn-primary" :disabled="saving">Save</button>
        <button type="button" @click="cancel">Cancel</button>
      </form>
    </template>
  </div>
</template>

<style scoped>
.check-failed { color: var(--color-error, #b00020); }
</style>
