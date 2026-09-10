<script setup>
// D-124 Phase 4: Add/Edit moved off ApplicationSafeMapping.vue's
// Applications section (its own inline form/editing/startCreate/
// startEdit/cancelEdit state) onto this routed page -- the Safes section
// below it (a plain per-row <select> assignment, not a form) is untouched
// and stays on ApplicationSafeMapping.vue. GET /api/applications/detailed
// returns the full, unpaginated application catalog (a small admin-curated
// list), so edit mode fetches the full list and finds the matching row by
// key rather than needing a new GET-by-id endpoint -- no API contract
// change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ applicationKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.applicationKey !== null && props.applicationKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ applicationCode: '', applicationName: '', description: '', ownerName: '', ownerEmail: '', technicalName: '', technicalEmail: '', notes: '' })

onMounted(async () => {
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/applications/detailed')
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const apps = await response.json()
      const match = apps.find(app => String(app.applicationKey) === String(props.applicationKey))
      if (!match) throw new Error('Application not found.')
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
    const url = isEditMode.value ? `/api/applications/${props.applicationKey}` : '/api/applications'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-application-mapping' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-application-mapping' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Application' : 'New Application' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">Code:</span> <input v-model="editing.applicationCode" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.applicationName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Owner Name:</span> <input v-model="editing.ownerName" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Owner Email:</span> <input v-model="editing.ownerEmail" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Technical Contact Name:</span> <input v-model="editing.technicalName" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Technical Contact Email:</span> <input v-model="editing.technicalEmail" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Notes:</span> <input v-model="editing.notes" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
