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
import { flash, savedMessage } from '../../composables/useFlash'
import { problemMessage } from '../../utils/problemText'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

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
      if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
      const apps = await response.json()
      const match = apps.find(app => String(app.applicationKey) === String(props.applicationKey))
      if (!match) throw new Error(t('admin.applicationEdit.notFound'))
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
      saveError.value = await problemMessage(response, t('common.saveFailed', { status: response.status }))
      return
    }
    flash(savedMessage(t, editing.value.applicationName))
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
    <h2>{{ isEditMode ? t('admin.applicationEdit.editTitle') : t('admin.applicationEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.code') }}</span> <input v-model="editing.applicationCode" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.name') }}</span> <input v-model="editing.applicationName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.description') }}</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.ownerName') }}</span> <input v-model="editing.ownerName" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.ownerEmail') }}</span> <input v-model="editing.ownerEmail" type="email" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.technicalName') }}</span> <input v-model="editing.technicalName" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.technicalEmail') }}</span> <input v-model="editing.technicalEmail" type="email" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.applicationEdit.notes') }}</span> <input v-model="editing.notes" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
