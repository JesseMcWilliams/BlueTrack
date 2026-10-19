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
import { flash, savedMessage } from '../../composables/useFlash'
import { problemMessage } from '../../utils/problemText'
import { readCsvHeaders, matchHeaders, applyMatches, applyConflicts, fieldsFor } from '../../utils/csvHeaderMapping'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

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
    if (response.ok) identifierTypes.value = (await response.json()).map(type => type.identifierType)
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
      detectError.value = t('admin.importMappingProfileEdit.noHeaders')
      return
    }
    detectFrom(headers, delimiter)
  } catch (err) {
    detectError.value = t('admin.importMappingProfileEdit.couldNotRead', { error: err.message })
  }
}

function replaceConflicts() {
  editing.value.fields = applyConflicts(editing.value.fields, detection.value.conflicts)
  detection.value = { ...detection.value, filled: [...detection.value.filled, ...detection.value.conflicts.map(c => ({ field: c.field, header: c.detected, how: 'replaced' }))], conflicts: [] }
}

function keepConflicts() {
  detection.value = { ...detection.value, conflicts: [] }
}

const DELIMITER_KEYS = { ',': 'admin.importMappingProfileEdit.delimiterComma', ';': 'admin.importMappingProfileEdit.delimiterSemicolon', '\t': 'admin.importMappingProfileEdit.delimiterTab' }

function filledText(filled) {
  return filled.map(m => m.how === 'synonym'
    ? t('admin.importMappingProfileEdit.filledSynonym', { field: m.field, header: m.header })
    : t('admin.importMappingProfileEdit.filledExact', { field: m.field, header: m.header })).join(', ')
}

onMounted(async () => {
  await loadIdentifierTypes()
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/risk-scoring/import-mapping-profiles')
      if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
      const items = await response.json()
      const match = items.find(item => String(item.importMappingProfileKey) === String(props.importMappingProfileKey))
      if (!match) throw new Error(t('admin.importMappingProfileEdit.notFound'))
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
      saveError.value = await problemMessage(response, t('common.saveFailed', { status: response.status }))
      return
    }
    flash(savedMessage(t, editing.value.profileName))
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
    <h2>{{ isEditMode ? t('admin.importMappingProfileEdit.editTitle') : t('admin.importMappingProfileEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.importMappingProfileEdit.feedType') }}</span>
          <select v-model="editing.feedType">
            <option v-for="type in FEED_TYPES" :key="type" :value="type">{{ type }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.importMappingProfileEdit.profileName') }}</span> <input v-model="editing.profileName" required /></label></p>
      <p><label><input v-model="editing.isActive" type="checkbox" /> {{ t('common.active') }}</label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.importMappingProfileEdit.description') }}</span> <input v-model="editing.description" /></label></p>

      <h4>{{ t('admin.importMappingProfileEdit.detectHeading') }}</h4>
      <p>{{ t('admin.importMappingProfileEdit.detectIntro') }}</p>
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.importMappingProfileEdit.sampleFile') }}</span>
          <input type="file" accept=".csv,.txt,text/csv" @change="onSampleFileChange" />
        </label>
      </p>
      <div role="status">
        <template v-if="detection">
          <p>
            {{ detection.delimiter
              ? t('admin.importMappingProfileEdit.foundColumnsDelimited', { count: detection.headers.length, delimiter: t(DELIMITER_KEYS[detection.delimiter]) })
              : t('admin.importMappingProfileEdit.foundColumns', { count: detection.headers.length }) }}
            <template v-if="detection.filled.length">{{ t('admin.importMappingProfileEdit.filledIn', { fields: filledText(detection.filled) }) }}</template>
            <template v-else>{{ t('admin.importMappingProfileEdit.nothingToFill') }}</template>
          </p>
          <p v-if="detection.unmatchedFields.length">
            {{ t('admin.importMappingProfileEdit.notMatched', { fields: detection.unmatchedFields.join(', ') }) }}
            <strong v-if="detection.requiredUnmatched.length">{{ t('admin.importMappingProfileEdit.requiredUnmatched', { fields: detection.requiredUnmatched.join(', ') }) }}</strong>
          </p>
          <p v-if="detection.unusedHeaders.length">{{ t('admin.importMappingProfileEdit.unusedColumns', { columns: detection.unusedHeaders.join(', ') }) }}</p>
        </template>
      </div>
      <p v-if="detectError" role="alert">{{ detectError }}</p>
      <div v-if="detection?.conflicts.length" class="detect-conflicts">
        <p>{{ t('admin.importMappingProfileEdit.conflictsIntro') }}</p>
        <ul>
          <li v-for="c in detection.conflicts" :key="c.field">{{ t('admin.importMappingProfileEdit.conflict', { field: c.field, current: c.current, detected: c.detected }) }}</li>
        </ul>
        <p>
          <button type="button" @click="replaceConflicts">{{ t('admin.importMappingProfileEdit.replaceConflicts') }}</button>
          <button type="button" @click="keepConflicts">{{ t('admin.importMappingProfileEdit.keepConflicts') }}</button>
        </p>
      </div>

      <h4>{{ t('admin.importMappingProfileEdit.fieldsHeading') }}</h4>
      <div v-for="(field, index) in editing.fields" :key="index" class="filter-row">
        <label class="field-label"><span class="field-label-text">{{ t('admin.importMappingProfileEdit.sourceColumn') }}</span> <input v-model="field.sourceColumnName" required /></label>
        <label class="field-label"><span class="field-label-text">{{ t('admin.importMappingProfileEdit.internalField') }}</span> <input v-model="field.targetFieldName" required /></label>
        <label><input v-model="field.isRequired" type="checkbox" /> {{ t('admin.importMappingProfileEdit.required') }}</label>
        <label class="field-label"><span class="field-label-text">{{ t('admin.importMappingProfileEdit.default') }}</span> <input v-model="field.defaultValue" /></label>
        <button type="button" @click="removeFieldRow(index)">{{ t('common.remove') }}</button>
      </div>
      <p><button type="button" @click="addFieldRow">{{ t('admin.importMappingProfileEdit.addField') }}</button></p>

      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
