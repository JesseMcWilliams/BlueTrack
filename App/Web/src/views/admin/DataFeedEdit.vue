<script setup>
// D-181: add/edit one data feed. Test asks the API (as the app pool) which
// file a run would pick up right now and reads its header row -- it works on
// the unsaved values, so a path can be checked before saving. In edit mode
// the feed's run history is shown below the form; clicking a run shows its
// row errors.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'

const props = defineProps({ dataFeedKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.dataFeedKey !== null && props.dataFeedKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const feedTypes = ref([])
// D-212: the source systems an account inventory feed can use; a new one can be added here.
const sourceSystems = ref([])
const newSource = ref(null)
const newSourceError = ref(null)
const testResult = ref(null)
const testError = ref(null)
const testing = ref(false)
const runs = ref([])
const selectedRun = ref(null)

const editing = ref({
  displayName: '',
  feedType: '',
  folderPath: '',
  fileNamePattern: '',
  importMappingProfileKey: null,
  isEnabled: true,
  displayOrder: 0,
  sourceSystemKey: null
})

const selectedType = computed(() => feedTypes.value.find(t => t.value === editing.value.feedType))

const selectedRunErrors = computed(() => {
  if (!selectedRun.value?.resultJson) return []
  try {
    return JSON.parse(selectedRun.value.resultJson).errors ?? []
  } catch {
    return []
  }
})

onMounted(async () => {
  try {
    const optionsResponse = await fetch('/api/admin/data-feeds/options')
    if (!optionsResponse.ok) throw new Error(`Request failed: ${optionsResponse.status}`)
    const options = await optionsResponse.json()
    feedTypes.value = options.feedTypes
    sourceSystems.value = options.sourceSystems ?? []

    if (isEditMode.value) {
      const response = await fetch(`/api/admin/data-feeds/${props.dataFeedKey}`)
      if (!response.ok) throw new Error(response.status === 404 ? 'Data feed not found.' : `Request failed: ${response.status}`)
      const feed = await response.json()
      editing.value = {
        displayName: feed.displayName,
        feedType: feed.feedType,
        folderPath: feed.folderPath,
        fileNamePattern: feed.fileNamePattern,
        importMappingProfileKey: feed.importMappingProfileKey,
        isEnabled: feed.isEnabled,
        displayOrder: feed.displayOrder,
        sourceSystemKey: feed.sourceSystemKey ?? null
      }
      await loadRuns()
    }
  } catch (err) {
    loadError.value = err.message
  }
  loading.value = false
})

async function loadRuns() {
  const response = await fetch(`/api/admin/data-feeds/${props.dataFeedKey}/runs`)
  if (response.ok) runs.value = await response.json()
}

function onFeedTypeChanged() {
  // A profile belongs to one feed type; don't carry it over. Only an account inventory feed has a source system.
  editing.value.importMappingProfileKey = null
  if (!selectedType.value?.usesSourceSystem) editing.value.sourceSystemKey = null
}

async function addSourceSystem() {
  newSourceError.value = null
  const response = await fetch('/api/admin/data-feeds/source-systems', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(newSource.value)
  })
  const body = await response.json().catch(() => null)
  if (!response.ok) {
    newSourceError.value = body?.detail ?? `Could not add the source system: ${response.status}`
    return
  }
  sourceSystems.value = [...sourceSystems.value, body].sort((a, b) => a.sourceSystemName.localeCompare(b.sourceSystemName))
  editing.value.sourceSystemKey = body.sourceSystemKey
  newSource.value = null
}

async function test() {
  testing.value = true
  testResult.value = null
  testError.value = null
  try {
    const response = await fetch('/api/admin/data-feeds/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        folderPath: editing.value.folderPath,
        fileNamePattern: editing.value.fileNamePattern,
        feedType: editing.value.feedType || null,
        importMappingProfileKey: editing.value.importMappingProfileKey
      })
    })
    const body = await response.json().catch(() => null)
    if (!response.ok) {
      testError.value = body?.error ?? `Test failed: ${response.status}`
      return
    }
    testResult.value = body
  } finally {
    testing.value = false
  }
}

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/data-feeds/${props.dataFeedKey}` : '/api/admin/data-feeds'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
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

async function showRun(run) {
  if (selectedRun.value?.dataFeedRunKey === run.dataFeedRunKey) {
    selectedRun.value = null
    return
  }
  const response = await fetch(`/api/admin/data-feeds/runs/${run.dataFeedRunKey}`)
  if (response.ok) selectedRun.value = await response.json()
}

function cancel() {
  router.push({ name: 'admin-data-sources' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Data Feed' : 'New Data Feed' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <form @submit.prevent="save">
        <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.displayName" maxlength="200" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">Import:</span>
            <select v-model="editing.feedType" required @change="onFeedTypeChanged">
              <option value="" disabled>Choose an import</option>
              <option v-for="t in feedTypes" :key="t.value" :value="t.value">{{ t.label }}</option>
            </select>
          </label>
          <small v-if="selectedType?.usesSourceSystem">Columns: UserName and Address (required); AccountName, PlatformName, OwnerName and Notes (optional). Test checks the required ones.</small>
          <small v-else>The file must have the same columns as that import's Bulk Actions template, or match the mapping profile. Test checks the required ones.</small>
        </p>
        <p v-if="selectedType?.usesMappingProfile">
          <label class="field-label">
            <span class="field-label-text">Mapping profile:</span>
            <select v-model="editing.importMappingProfileKey">
              <option :value="null">None (the template's own column names)</option>
              <option v-for="p in selectedType.mappingProfiles" :key="p.importMappingProfileKey" :value="p.importMappingProfileKey">
                {{ p.profileName }}{{ p.isActive ? '' : ' (inactive)' }}
              </option>
            </select>
          </label>
        </p>
        <template v-if="selectedType?.usesSourceSystem">
          <p>
            <label class="field-label">
              <span class="field-label-text">Source system:</span>
              <select v-model="editing.sourceSystemKey" required>
                <option :value="null" disabled>Choose the system the accounts come from</option>
                <option v-for="s in sourceSystems" :key="s.sourceSystemKey" :value="s.sourceSystemKey">{{ s.sourceSystemName }}</option>
              </select>
            </label>
            <button v-if="!newSource" type="button" @click="newSource = { sourceSystemCode: '', sourceSystemName: '' }">+ New source system</button>
            <small>Accounts are identified by UserName and Address within this system. Accounts of this system missing from the file are marked deleted.</small>
          </p>
          <div v-if="newSource" class="new-source">
            <p><label class="field-label"><span class="field-label-text">Source name:</span> <input v-model="newSource.sourceSystemName" maxlength="200" placeholder="Linux servers (CMDB)" /></label></p>
            <p><label class="field-label"><span class="field-label-text">Source code:</span> <input v-model="newSource.sourceSystemCode" maxlength="50" placeholder="LINUX_CMDB" /></label></p>
            <p v-if="newSourceError" role="alert">{{ newSourceError }}</p>
            <p>
              <button type="button" @click="addSourceSystem">Add source system</button>
              <button type="button" @click="newSource = null; newSourceError = null">Cancel</button>
            </p>
          </div>
        </template>
        <p>
          <label class="field-label"><span class="field-label-text">Folder path:</span> <input v-model="editing.folderPath" maxlength="500" placeholder="D:\Feeds\BlueTrack" required /></label>
          <small>A full local or UNC path that the application pool's account can read.</small>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">File name pattern:</span> <input v-model="editing.fileNamePattern" maxlength="260" placeholder="targets_{yyyy-MM-dd}.csv" required /></label>
          <small>{yyyy-MM-dd} (or another date format in braces) is replaced with today's date; * and ? are wildcards. The newest matching file is imported.</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">Run order:</span> <input v-model.number="editing.displayOrder" type="number" required /></label></p>
        <p><label><input v-model="editing.isEnabled" type="checkbox" /> Enabled (runs nightly)</label></p>

        <p>
          <button type="button" :disabled="testing || !editing.folderPath || !editing.fileNamePattern" @click="test">Test</button>
          <span v-if="testing" role="status"> Testing...</span>
        </p>
        <p v-if="testError" role="alert">{{ testError }}</p>
        <div v-if="testResult" class="test-result" role="status">
          <p>{{ testResult.message }}</p>
          <p v-if="testResult.matchedFile && editing.feedType && testResult.missingColumns.length === 0"><small>Has every column the import needs.</small></p>
          <p v-if="testResult.matchedFile"><small>Modified {{ formatRunTime(testResult.matchedFileModified, true) }}. Columns: {{ testResult.columns.join(', ') }}</small></p>
        </div>

        <p v-if="saveError" role="alert">{{ saveError }}</p>
        <button type="submit" class="btn-primary" :disabled="saving">Save</button>
        <button type="button" @click="cancel">Cancel</button>
      </form>

      <template v-if="isEditMode">
        <h3>Run history</h3>
        <p v-if="runs.length === 0">This feed hasn't run yet.</p>
        <table v-else>
          <thead>
            <tr><th>Started</th><th>Trigger</th><th>By</th><th>Outcome</th><th>File</th><th>Rows</th><th>Summary</th></tr>
          </thead>
          <tbody>
            <template v-for="run in runs" :key="run.dataFeedRunKey">
              <tr>
                <td><a href="#" @click.prevent="showRun(run)">{{ formatRunTime(run.startedAt) }}</a></td>
                <td>{{ run.triggerType === 'RunNow' ? 'Run now' : 'Nightly' }}</td>
                <td>{{ run.triggeredByName }}</td>
                <td>{{ run.outcome }}</td>
                <td>{{ run.fileName }}</td>
                <td>{{ formatCount(run.totalRows) }}<template v-if="run.errorRows"> ({{ formatCount(run.errorRows) }} errors)</template></td>
                <td>{{ run.summary ?? run.errorMessage }}</td>
              </tr>
              <tr v-if="selectedRun?.dataFeedRunKey === run.dataFeedRunKey">
                <td colspan="7">
                  <p v-if="selectedRun.errorMessage">{{ selectedRun.errorMessage }}</p>
                  <p v-if="selectedRunErrors.length === 0 && !selectedRun.errorMessage">No row errors.</p>
                  <ul v-else-if="selectedRunErrors.length">
                    <li v-for="e in selectedRunErrors" :key="e.rowNumber + e.error">Row {{ e.rowNumber }}: {{ e.error }}</li>
                  </ul>
                </td>
              </tr>
            </template>
          </tbody>
        </table>
      </template>
    </template>
  </div>
</template>
