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

onMounted(async () => {
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
