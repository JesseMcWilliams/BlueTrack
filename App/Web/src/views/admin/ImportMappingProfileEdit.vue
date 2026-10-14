<script setup>
// D-124 Phase 4: Add/Edit moved off ImportMappingProfiles.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit/addFieldRow/removeFieldRow
// state onto this routed page -- the nested Fields collection (add/remove
// rows, whole-object replace-on-save) moves here unchanged. GetAllAsync
// returns the full, unpaginated profile catalog (a small admin-curated
// list), so edit mode fetches the full list and finds the matching row by
// key rather than needing a new GET-by-id endpoint -- no API contract
// change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { readCsvHeaders, matchHeaders, applyMatches, applyConflicts, fieldsFor } from '../../utils/csvHeaderMapping'

const FEED_TYPES = ['TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap', 'AccountAccessGroupMembership', 'AccountTargetMap']

const props = defineProps({ importMappingProfileKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.importMappingProfileKey !== null && props.importMappingProfileKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ feedType: FEED_TYPES[0], profileName: '', isActive: true, description: '', fields: [] })

function addFieldRow() {
  editing.value.fields.push({ sourceColumnName: '', targetFieldName: '', isRequired: false, defaultValue: '' })
}
function removeFieldRow(index) {
  editing.value.fields.splice(index, 1)
}

// D-225: "Detect from a sample file" -- the file's header row is read in
// the browser (first chunk only; nothing is uploaded) and each internal
// field of the feed type is matched to a column (csvHeaderMapping.js).
// A field the admin already mapped to another column is never changed
// without "Replace" being pressed.
const identifierTypes = ref([])
const detection = ref(null)
const detectError = ref(null)

async function loadIdentifierTypes() {
  try {
    const response = await fetch('/api/admin/targets/identifier-types')
    if (response.ok) identifierTypes.value = (await response.json()).map(t => t.identifierType)
  } catch {
    // The seeded identifier types in csvHeaderMapping.js still match.
  }
}

function detectFrom(headers, delimiter) {
  const fields = fieldsFor(editing.value.feedType, identifierTypes.value)
  const matched = matchHeaders(headers, fields)
  const applied = applyMatches(editing.value.fields, matched.matches)
  editing.value.fields = applied.rows
  detection.value = {
    headers,
    delimiter,
    filled: applied.filled,
    conflicts: applied.conflicts,
    unmatchedFields: matched.unmatchedFields,
    requiredUnmatched: fields.filter(f => f.required && matched.unmatchedFields.includes(f.name)).map(f => f.name),
    unusedHeaders: matched.unusedHeaders
  }
}

async function onSampleFileChange(event) {
  detectError.value = null
  detection.value = null
  const file = event.target.files[0]
  if (!file) return
  try {
    const { headers, delimiter } = await readCsvHeaders(file)
    if (headers.length === 0) {
      detectError.value = 'No column headers found in the first line of that file.'
      return
    }
    detectFrom(headers, delimiter)
  } catch (err) {
    detectError.value = `Couldn't read the file: ${err.message}`
  }
}

function replaceConflicts() {
  editing.value.fields = applyConflicts(editing.value.fields, detection.value.conflicts)
  detection.value = { ...detection.value, filled: [...detection.value.filled, ...detection.value.conflicts.map(c => ({ field: c.field, header: c.detected, how: 'replaced' }))], conflicts: [] }
}

function keepConflicts() {
  detection.value = { ...detection.value, conflicts: [] }
}

const DELIMITER_NAMES = { ',': 'comma', ';': 'semicolon', '\t': 'tab' }

onMounted(async () => {
  await loadIdentifierTypes()
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/risk-scoring/import-mapping-profiles')
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const items = await response.json()
      const match = items.find(item => String(item.importMappingProfileKey) === String(props.importMappingProfileKey))
      if (!match) throw new Error('Profile not found.')
      editing.value = { ...match, fields: match.fields.map(f => ({ ...f })) }
    } catch (err) {
      loadError.value = err.message
    }
  } else if (Array.isArray(window.history.state?.sampleHeaders)) {
    // From a Bulk Actions page's "Create a mapping profile from these columns".
    const { sampleHeaders, sampleFeedType } = window.history.state
    if (FEED_TYPES.includes(sampleFeedType)) editing.value.feedType = sampleFeedType
    detectFrom(sampleHeaders, null)
  }
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value
      ? `/api/admin/risk-scoring/import-mapping-profiles/${props.importMappingProfileKey}`
      : '/api/admin/risk-scoring/import-mapping-profiles'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-import-mapping-profiles' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-import-mapping-profiles' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Profile' : 'New Profile' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label">
          <span class="field-label-text">Feed Type:</span>
          <select v-model="editing.feedType">
            <option v-for="type in FEED_TYPES" :key="type" :value="type">{{ type }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">Profile Name:</span> <input v-model="editing.profileName" required /></label></p>
      <p><label><input v-model="editing.isActive" type="checkbox" /> Active</label></p>
      <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>

      <h4>Detect from a sample file</h4>
      <p>Pick a sample of the CSV this profile is for. Only its header row is read, in your browser; the file isn't uploaded. Each field is matched to a column with the same name (ignoring case, spaces, underscores and punctuation), then to a common alternative name. Fields you've already mapped aren't changed unless you choose Replace.</p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Sample CSV file:</span>
          <input type="file" accept=".csv,.txt,text/csv" @change="onSampleFileChange" />
        </label>
      </p>
      <div role="status">
        <template v-if="detection">
          <p>
            Found {{ detection.headers.length }} columns<template v-if="detection.delimiter"> ({{ DELIMITER_NAMES[detection.delimiter] }}-separated)</template>.
            <template v-if="detection.filled.length">Filled in: {{ detection.filled.map(m => `${m.field} ← ${m.header}${m.how === 'synonym' ? ' (alternative name)' : ''}`).join(', ') }}.</template>
            <template v-else>No empty fields to fill in.</template>
          </p>
          <p v-if="detection.unmatchedFields.length">
            Not matched (fill in by hand if the file has them): {{ detection.unmatchedFields.join(', ') }}.
            <strong v-if="detection.requiredUnmatched.length">Required by the import: {{ detection.requiredUnmatched.join(', ') }}.</strong>
          </p>
          <p v-if="detection.unusedHeaders.length">Columns not used: {{ detection.unusedHeaders.join(', ') }}.</p>
        </template>
      </div>
      <p v-if="detectError" role="alert">{{ detectError }}</p>
      <div v-if="detection?.conflicts.length" class="detect-conflicts">
        <p>These fields are already mapped to a different column:</p>
        <ul>
          <li v-for="c in detection.conflicts" :key="c.field">{{ c.field }}: now {{ c.current }}, the file has {{ c.detected }}</li>
        </ul>
        <p>
          <button type="button" @click="replaceConflicts">Replace with the file's columns</button>
          <button type="button" @click="keepConflicts">Keep my mappings</button>
        </p>
      </div>

      <h4>Fields</h4>
      <div v-for="(field, index) in editing.fields" :key="index" class="filter-row">
        <label class="field-label"><span class="field-label-text">Source Column:</span> <input v-model="field.sourceColumnName" required /></label>
        <label class="field-label"><span class="field-label-text">Internal Field:</span> <input v-model="field.targetFieldName" required /></label>
        <label><input v-model="field.isRequired" type="checkbox" /> Required</label>
        <label class="field-label"><span class="field-label-text">Default:</span> <input v-model="field.defaultValue" /></label>
        <button type="button" @click="removeFieldRow(index)">Remove</button>
      </div>
      <p><button type="button" @click="addFieldRow">+ Add Field</button></p>

      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
