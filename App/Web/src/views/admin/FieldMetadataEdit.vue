<script setup>
// D-124 Phase 4: Add/Edit moved off FieldMetadataManagement.vue's own
// inline form/editing/startCreate/startEdit/cancelEdit state onto this
// routed page. GetAllAsync returns the full, unpaginated field-metadata
// catalog (a small admin-curated list), so edit mode fetches the full list
// and finds the matching row by key rather than needing a new GET-by-id
// endpoint -- no API contract change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ fieldMetadataKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.fieldMetadataKey !== null && props.fieldMetadataKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ fieldName: '', displayLabel: '', fieldType: 'text', referenceTable: '', isRequired: false, displayOrder: 0 })

onMounted(async () => {
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/field-metadata')
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const items = await response.json()
      const match = items.find(item => String(item.fieldMetadataKey) === String(props.fieldMetadataKey))
      if (!match) throw new Error('Field not found.')
      editing.value = { ...match }
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
    const url = isEditMode.value ? `/api/admin/field-metadata/${props.fieldMetadataKey}` : '/api/admin/field-metadata'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-field-metadata' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-field-metadata' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Field' : 'New Field' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">Field Name:</span> <input v-model="editing.fieldName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Display Label:</span> <input v-model="editing.displayLabel" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Field Type:</span> <input v-model="editing.fieldType" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Reference Table:</span> <input v-model="editing.referenceTable" /></label></p>
      <p><label><input v-model="editing.isRequired" type="checkbox" /> Required</label></p>
      <p><label class="field-label"><span class="field-label-text">Display Order:</span> <input v-model.number="editing.displayOrder" type="number" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
