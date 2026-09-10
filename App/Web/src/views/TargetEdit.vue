<script setup>
// D-124 Phase 4: Add/Edit moved off Targets.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit state onto this routed
// page, mirroring RiskExceptionEdit.vue's create-vs-edit-by-route-param
// shape (isEditMode from the route param; loading/saving/error refs; save()
// POSTs or PUTs then navigates away). Targets.vue's nested Identifiers
// collection (add/remove rows, replace-all-on-save) moves here unchanged.
//
// No GET-by-id endpoint existed for Targets before this phase -- the old
// inline form worked off the already-loaded list row, never a fresh fetch.
// Since this page can be reached by a direct navigation or a page refresh
// (not just a click from the list page), and Targets is now paginated
// (D-124 Phase 3 -- 2,380 real rows), the list endpoint alone can't be
// trusted to include any one specific row. A new
// GET /api/admin/targets/{targetKey} (TargetsController.GetByKey /
// TargetRepository.GetByKeyAsync) was added alongside this page, mirroring
// RiskExceptionsController.GetByKey's existing shape -- Create/Update/
// Delete are otherwise completely unchanged.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ targetKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.targetKey !== null && props.targetKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const applications = ref([])
const identifierTypes = ref([])
const targetTypes = ref([])

const editing = ref({ targetTypeKey: null, targetName: '', riskScore: 0, description: '', discoverySource: '', identifiers: [] })

function addIdentifierRow() {
  editing.value.identifiers.push({ identifierType: identifierTypes.value[0]?.identifierType ?? '', identifierValue: '' })
}
function removeIdentifierRow(index) {
  editing.value.identifiers.splice(index, 1)
}

onMounted(async () => {
  const referenceDataPromise = Promise.all([
    fetch('/api/applications').then(r => (r.ok ? r.json() : [])).then(data => { applications.value = data }).catch(() => {
      // Non-fatal -- the Application dropdown just won't have options if this fails.
    }),
    fetch('/api/admin/targets/identifier-types').then(r => (r.ok ? r.json() : [])).then(data => { identifierTypes.value = data }).catch(() => {
      // Non-fatal -- the Identifier Type dropdown just won't have options if this fails.
    }),
    fetch('/api/admin/targets/target-types').then(r => (r.ok ? r.json() : [])).then(data => { targetTypes.value = data }).catch(() => {
      // Non-fatal -- the Type dropdown just won't have options if this fails.
    })
  ])

  if (isEditMode.value) {
    try {
      const response = await fetch(`/api/admin/targets/${props.targetKey}`)
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      const target = await response.json()
      editing.value = { ...target, identifiers: target.identifiers.map(i => ({ ...i })) }
    } catch (err) {
      loadError.value = err.message
    }
  }

  await referenceDataPromise
  if (!isEditMode.value) {
    editing.value.targetTypeKey = targetTypes.value[0]?.targetTypeKey ?? null
  }
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/targets/${props.targetKey}` : '/api/admin/targets'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'targets' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'targets' })
}
</script>

<template>
  <div>
    <h1>{{ isEditMode ? 'Edit Target' : 'New Target' }}</h1>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.targetName" required /></label></p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Type:</span>
          <select v-model="editing.targetTypeKey">
            <option v-for="type in targetTypes" :key="type.targetTypeKey" :value="type.targetTypeKey">{{ type.displayName }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">Risk Score (0-1000):</span> <input v-model.number="editing.riskScore" type="number" min="0" max="1000" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Discovery Source:</span> <input v-model="editing.discoverySource" placeholder="Manual" /></label></p>

      <h3>Identifiers</h3>
      <div v-for="(identifier, index) in editing.identifiers" :key="index" class="filter-row">
        <label class="field-label">
          <span class="field-label-text">Type:</span>
          <select v-model="identifier.identifierType">
            <option v-for="type in identifierTypes" :key="type.identifierType" :value="type.identifierType">{{ type.identifierType }}</option>
          </select>
        </label>
        <label class="field-label"><span class="field-label-text">Value:</span> <input v-model="identifier.identifierValue" required /></label>
        <button type="button" @click="removeIdentifierRow(index)">Remove</button>
      </div>
      <p><button type="button" @click="addIdentifierRow">+ Add Identifier</button></p>

      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
