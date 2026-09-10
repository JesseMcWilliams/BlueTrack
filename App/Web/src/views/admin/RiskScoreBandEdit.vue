<script setup>
// D-124 Phase 4: Add/Edit moved off RiskScoreBands.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit state onto this routed
// page. GetAllAsync returns the full, unpaginated band catalog (a small
// admin-curated list, unlike Targets/Access Groups), so edit mode fetches
// the full list and finds the matching row by key rather than needing a
// new GET-by-id endpoint -- no API contract change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ riskScoreBandKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.riskScoreBandKey !== null && props.riskScoreBandKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ bandName: '', minScore: 0, maxScore: 0, riskOrder: 0 })

onMounted(async () => {
  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/risk-score-bands')
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const items = await response.json()
      const match = items.find(item => String(item.riskScoreBandKey) === String(props.riskScoreBandKey))
      if (!match) throw new Error('Band not found.')
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
    const url = isEditMode.value ? `/api/admin/risk-score-bands/${props.riskScoreBandKey}` : '/api/admin/risk-score-bands'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.message ?? `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-risk-score-bands' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-risk-score-bands' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? 'Edit Band' : 'New Band' }}</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.bandName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Min Score:</span> <input v-model.number="editing.minScore" type="number" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Max Score:</span> <input v-model.number="editing.maxScore" type="number" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Risk Order:</span> <input v-model.number="editing.riskOrder" type="number" required /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
