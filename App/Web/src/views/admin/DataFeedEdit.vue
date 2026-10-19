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
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

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
  sourceSystemKey: null,
  skipGroupRows: false
})

const selectedType = computed(() => feedTypes.value.find(ft => ft.value === editing.value.feedType))

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
    if (!optionsResponse.ok) throw new Error(t('common.requestFailed', { status: optionsResponse.status }))
    const options = await optionsResponse.json()
    feedTypes.value = options.feedTypes
    sourceSystems.value = options.sourceSystems ?? []

    if (isEditMode.value) {
      const response = await fetch(`/api/admin/data-feeds/${props.dataFeedKey}`)
      if (!response.ok) throw new Error(response.status === 404 ? t('admin.dataFeedEdit.notFound') : t('common.requestFailed', { status: response.status }))
      const feed = await response.json()
      editing.value = {
        displayName: feed.displayName,
        feedType: feed.feedType,
        folderPath: feed.folderPath,
        fileNamePattern: feed.fileNamePattern,
        importMappingProfileKey: feed.importMappingProfileKey,
        isEnabled: feed.isEnabled,
        displayOrder: feed.displayOrder,
        sourceSystemKey: feed.sourceSystemKey ?? null,
        skipGroupRows: feed.skipGroupRows ?? false
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
  if (!selectedType.value?.usesSourceSystem) {
    editing.value.sourceSystemKey = null
    editing.value.skipGroupRows = false   // D-237
  }
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
    newSourceError.value = body?.detail ?? t('admin.dataFeedEdit.addSourceFailed', { status: response.status })
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
      testError.value = body?.error ?? t('admin.dataFeedEdit.testFailed', { status: response.status })
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
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
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
    <h2>{{ isEditMode ? t('admin.dataFeedEdit.editTitle') : t('admin.dataFeedEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <form @submit.prevent="save">
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.nameLabel') }}</span> <input v-model="editing.displayName" maxlength="200" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.dataFeedEdit.importLabel') }}</span>
            <select v-model="editing.feedType" required @change="onFeedTypeChanged">
              <option value="" disabled>{{ t('admin.dataFeedEdit.chooseImport') }}</option>
              <option v-for="ft in feedTypes" :key="ft.value" :value="ft.value">{{ ft.label }}</option>
            </select>
          </label>
          <small v-if="selectedType?.usesSourceSystem">{{ t('admin.dataFeedEdit.accountColumnsHint') }}</small>
          <small v-else>{{ t('admin.dataFeedEdit.columnsHint') }}</small>
        </p>
        <p v-if="selectedType?.usesMappingProfile">
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.dataFeedEdit.mappingProfileLabel') }}</span>
            <select v-model="editing.importMappingProfileKey">
              <option :value="null">{{ t('admin.dataFeedEdit.noProfile') }}</option>
              <option v-for="p in selectedType.mappingProfiles" :key="p.importMappingProfileKey" :value="p.importMappingProfileKey">
                {{ p.profileName }}{{ p.isActive ? '' : ' ' + t('admin.dataFeedEdit.inactive') }}
              </option>
            </select>
          </label>
        </p>
        <template v-if="selectedType?.usesSourceSystem">
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.dataFeedEdit.sourceSystemLabel') }}</span>
              <select v-model="editing.sourceSystemKey" required>
                <option :value="null" disabled>{{ t('admin.dataFeedEdit.chooseSourceSystem') }}</option>
                <option v-for="s in sourceSystems" :key="s.sourceSystemKey" :value="s.sourceSystemKey">{{ s.sourceSystemName }}</option>
              </select>
            </label>
            <button v-if="!newSource" type="button" @click="newSource = { sourceSystemCode: '', sourceSystemName: '' }">{{ t('admin.dataFeedEdit.newSourceSystem') }}</button>
            <small>{{ t('admin.dataFeedEdit.sourceSystemHint') }}</small>
          </p>
          <div v-if="newSource" class="new-source">
            <p><label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.sourceNameLabel') }}</span> <input v-model="newSource.sourceSystemName" maxlength="200" @keydown.enter.prevent="addSourceSystem" :placeholder="t('admin.dataFeedEdit.sourceNamePlaceholder')" /></label></p>
            <p><label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.sourceCodeLabel') }}</span> <input v-model="newSource.sourceSystemCode" maxlength="50" @keydown.enter.prevent="addSourceSystem" :placeholder="t('admin.dataFeedEdit.sourceCodePlaceholder')" /></label></p>
            <p v-if="newSourceError" role="alert">{{ newSourceError }}</p>
            <p>
              <button type="button" @click="addSourceSystem">{{ t('admin.dataFeedEdit.addSourceSystem') }}</button>
              <button type="button" @click="newSource = null; newSourceError = null">{{ t('common.cancel') }}</button>
            </p>
          </div>
          <p>
            <label><input v-model="editing.skipGroupRows" type="checkbox" /> {{ t('admin.dataFeedEdit.skipGroupRows') }}</label>
            <small>{{ t('admin.dataFeedEdit.skipGroupRowsHint') }}</small>
          </p>
        </template>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.folderPathLabel') }}</span> <input v-model="editing.folderPath" maxlength="500" :placeholder="t('admin.dataFeedEdit.folderPathPlaceholder')" required /></label>
          <small>{{ t('admin.dataFeedEdit.folderPathHint') }}</small>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.fileNamePatternLabel') }}</span> <input v-model="editing.fileNamePattern" maxlength="260" :placeholder="t('admin.dataFeedEdit.fileNamePatternPlaceholder')" required /></label>
          <small>{{ t('admin.dataFeedEdit.fileNamePatternHint') }}</small>
        </p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.dataFeedEdit.runOrderLabel') }}</span> <input v-model.number="editing.displayOrder" type="number" required /></label></p>
        <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('admin.dataFeedEdit.enabledNightly') }}</label></p>

        <p>
          <button type="button" :disabled="testing || !editing.folderPath || !editing.fileNamePattern" @click="test">{{ t('admin.dataFeedEdit.test') }}</button>
          <span v-if="testing" role="status">{{ ' ' + t('admin.dataFeedEdit.testing') }}</span>
        </p>
        <p v-if="testError" role="alert">{{ testError }}</p>
        <div v-if="testResult" class="test-result" role="status">
          <p>{{ testResult.message }}</p>
          <p v-if="testResult.matchedFile && editing.feedType && testResult.missingColumns.length === 0"><small>{{ t('admin.dataFeedEdit.hasEveryColumn') }}</small></p>
          <p v-if="testResult.matchedFile"><small>{{ t('admin.dataFeedEdit.modifiedColumns', { modified: formatRunTime(testResult.matchedFileModified, true), columns: testResult.columns.join(', ') }) }}</small></p>
        </div>

        <p v-if="saveError" role="alert">{{ saveError }}</p>
        <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
        <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
      </form>

      <template v-if="isEditMode">
        <h3>{{ t('admin.dataFeedEdit.runHistory') }}</h3>
        <p v-if="runs.length === 0">{{ t('admin.dataFeedEdit.noRuns') }}</p>
        <table v-else>
          <thead>
            <tr><th>{{ t('admin.dataFeedEdit.started') }}</th><th>{{ t('admin.dataFeedEdit.trigger') }}</th><th>{{ t('admin.dataFeedEdit.by') }}</th><th>{{ t('admin.dataFeedEdit.outcome') }}</th><th>{{ t('admin.dataFeedEdit.file') }}</th><th>{{ t('admin.dataFeedEdit.rows') }}</th><th>{{ t('admin.dataFeedEdit.summary') }}</th></tr>
          </thead>
          <tbody>
            <template v-for="run in runs" :key="run.dataFeedRunKey">
              <tr>
                <td><a href="#" @click.prevent="showRun(run)">{{ formatRunTime(run.startedAt) }}</a></td>
                <td>{{ run.triggerType === 'RunNow' ? t('admin.dataFeedEdit.triggerRunNow') : t('admin.dataFeedEdit.triggerNightly') }}</td>
                <td>{{ run.triggeredByName }}</td>
                <td>{{ run.outcome }}</td>
                <td>{{ run.fileName }}</td>
                <td>{{ formatCount(run.totalRows) }}<template v-if="run.errorRows">{{ ' ' + t('admin.dataFeedEdit.errorRows', { count: formatCount(run.errorRows) }) }}</template></td>
                <td>{{ run.summary ?? run.errorMessage }}</td>
              </tr>
              <tr v-if="selectedRun?.dataFeedRunKey === run.dataFeedRunKey">
                <td colspan="7">
                  <p v-if="selectedRun.errorMessage">{{ selectedRun.errorMessage }}</p>
                  <p v-if="selectedRunErrors.length === 0 && !selectedRun.errorMessage">{{ t('admin.dataFeedEdit.noRowErrors') }}</p>
                  <ul v-else-if="selectedRunErrors.length">
                    <li v-for="e in selectedRunErrors" :key="e.rowNumber + e.error">{{ t('admin.dataFeedEdit.rowError', { row: e.rowNumber, error: e.error }) }}</li>
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
