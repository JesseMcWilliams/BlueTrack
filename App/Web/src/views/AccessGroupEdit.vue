<script setup>
// D-124 Phase 4: Add/Edit moved off AccessGroups.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit state onto this routed
// page, mirroring TargetEdit.vue's/RiskExceptionEdit.vue's create-vs-edit-
// by-route-param shape.
//
// No GET-by-id endpoint existed for Access Groups before this phase -- the
// old inline form worked off the already-loaded list row, never a fresh
// fetch. Since this page can be reached by a direct navigation or a page
// refresh (not just a click from the list page), and Access Groups is now
// paginated (D-124 Phase 3), a new GET /api/admin/access-groups/{accessGroupKey}
// (AccessGroupsController.GetByKey / AccessGroupRepository.GetByKeyAsync)
// was added alongside this page, mirroring TargetsController.GetByKey's
// identical reasoning -- Create/Update/Delete are otherwise unchanged.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const props = defineProps({ accessGroupKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.accessGroupKey !== null && props.accessGroupKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const targets = ref([])
const sorTypes = ref([])

// D-129: Discovery Source defaults to 'Manual' for a brand-new row created
// via this Add form (confirmed directly) -- edit mode overwrites this
// whole object with the real fetched row below, so this default only ever
// takes effect on create.
const editing = ref({ groupName: '', groupIdentifier: '', groupScope: 'Domain', foundOnTargetKey: null, sorTypeKey: null, sorAddress: '', baseRiskScore: 0, description: '', discoverySource: 'Manual' })

onMounted(async () => {
  const referenceDataPromise = Promise.all([
    fetch('/api/admin/targets').then(r => (r.ok ? r.json() : [])).then(data => { targets.value = data }).catch(() => {
      // Non-fatal -- the Found On Target dropdown just won't have options if this fails.
    }),
    fetch('/api/admin/access-groups/sor-types').then(r => (r.ok ? r.json() : [])).then(data => { sorTypes.value = data }).catch(() => {
      // Non-fatal -- the SOR Type dropdown just won't have options if this fails.
    })
  ])

  if (isEditMode.value) {
    try {
      const response = await fetch(`/api/admin/access-groups/${props.accessGroupKey}`)
      if (!response.ok) throw new Error(`Request failed: ${response.status}`)
      editing.value = await response.json()
    } catch (err) {
      loadError.value = err.message
    }
  }

  await referenceDataPromise
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/access-groups/${props.accessGroupKey}` : '/api/admin/access-groups'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = `Save failed: ${response.status}`
      return
    }
    router.push({ name: 'access-groups' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'access-groups' })
}
</script>

<template>
  <div>
    <h1>{{ isEditMode ? 'Edit Access Group' : 'New Access Group' }}</h1>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.groupName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Identifier (e.g. AD SID/DN):</span> <input v-model="editing.groupIdentifier" required /></label></p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Scope:</span>
          <select v-model="editing.groupScope">
            <option value="Domain">Domain</option>
            <option value="Local">Local</option>
          </select>
        </label>
      </p>
      <p v-if="editing.groupScope === 'Local'">
        <label class="field-label">
          <span class="field-label-text">Found On Target:</span>
          <select v-model="editing.foundOnTargetKey">
            <option :value="null">(none)</option>
            <option v-for="target in targets" :key="target.targetKey" :value="target.targetKey">{{ target.targetName }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label">
          <span class="field-label-text">SOR Type:</span>
          <select v-model="editing.sorTypeKey">
            <option :value="null">(none)</option>
            <option v-for="type in sorTypes" :key="type.sorTypeKey" :value="type.sorTypeKey">{{ type.sorTypeName }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">SOR Address:</span> <input v-model="editing.sorAddress" placeholder="e.g. company.com or 192.168.1.1" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Base Risk Score (0-1000):</span> <input v-model.number="editing.baseRiskScore" type="number" min="0" max="1000" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Discovery Source:</span> <input v-model="editing.discoverySource" placeholder="Manual" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Save</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
