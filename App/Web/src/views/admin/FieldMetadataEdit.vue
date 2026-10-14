<script setup>
// D-124 Phase 4: Add/Edit moved off FieldMetadataManagement.vue's own
// inline form/editing/startCreate/startEdit/cancelEdit state onto this
// routed page. GetAllAsync returns the full, unpaginated field-metadata
// catalog (a small admin-curated list), so edit mode fetches the full list
// and finds the matching row by key rather than needing a new GET-by-id
// endpoint -- no API contract change for this page.
// D-214: Required Permission (D-198) -- the permission a user needs to change
// this field -- is chosen from GET /api/admin/field-metadata/permission-options.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({ fieldMetadataKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.fieldMetadataKey !== null && props.fieldMetadataKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const editing = ref({ fieldName: '', displayLabel: '', fieldType: 'text', referenceTable: '', isRequired: false, requiredPermission: null, displayOrder: 0 })
const permissions = ref([])

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/field-metadata/permission-options')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    permissions.value = await response.json()
  } catch (err) {
    loadError.value = err.message
  }
  if (isEditMode.value && !loadError.value) {
    try {
      const response = await fetch('/api/admin/field-metadata')
      if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
      const items = await response.json()
      const match = items.find(item => String(item.fieldMetadataKey) === String(props.fieldMetadataKey))
      if (!match) throw new Error(t('admin.fieldMetadataEdit.notFound'))
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
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
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
    <h2>{{ isEditMode ? t('admin.fieldMetadataEdit.editTitle') : t('admin.fieldMetadataEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.fieldMetadataEdit.fieldName') }}</span> <input v-model="editing.fieldName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.fieldMetadataEdit.displayLabel') }}</span> <input v-model="editing.displayLabel" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.fieldMetadataEdit.fieldType') }}</span> <input v-model="editing.fieldType" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.fieldMetadataEdit.referenceTable') }}</span> <input v-model="editing.referenceTable" /></label></p>
      <p><label><input v-model="editing.isRequired" type="checkbox" /> {{ t('admin.fieldMetadataEdit.required') }}</label></p>
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.fieldMetadataEdit.requiredPermission') }}</span>
          <select v-model="editing.requiredPermission">
            <option :value="null">{{ t('admin.fieldMetadataEdit.noPermission') }}</option>
            <option v-for="p in permissions" :key="p.permissionKey" :value="p.permissionKey">{{ p.permissionName }}</option>
          </select>
        </label>
        <small>{{ t('admin.fieldMetadataEdit.permissionHint') }}</small>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.fieldMetadataEdit.displayOrder') }}</span> <input v-model.number="editing.displayOrder" type="number" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
