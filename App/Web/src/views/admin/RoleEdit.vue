<script setup>
// D-124 Phase 4: Add/Edit moved off RolesAndPermissions.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit/togglePermission state onto
// this routed page. GetRolesAsync returns the full, unpaginated role
// catalog (a small admin-curated list), so edit mode fetches the full list
// and finds the matching row by key rather than needing a new GET-by-id
// endpoint -- no API contract change for this page.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { flash, savedMessage } from '../../composables/useFlash'
import { problemMessage } from '../../utils/problemText'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({ appRoleKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.appRoleKey !== null && props.appRoleKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const catalog = ref([])
const editing = ref({ roleName: '', description: '', notificationEmail: '', permissionNames: [] })

function togglePermission(name) {
  const set = new Set(editing.value.permissionNames)
  if (set.has(name)) set.delete(name)
  else set.add(name)
  editing.value.permissionNames = [...set]
}

onMounted(async () => {
  const catalogPromise = fetch('/api/admin/permissions').then(r => (r.ok ? r.json() : [])).then(data => { catalog.value = data }).catch(() => {
    // Non-fatal -- the permission checklist just won't have options if this fails.
  })

  if (isEditMode.value) {
    try {
      const response = await fetch('/api/admin/roles')
      if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
      const roles = await response.json()
      const match = roles.find(role => String(role.appRoleKey) === String(props.appRoleKey))
      if (!match) throw new Error(t('admin.roleEdit.notFound'))
      editing.value = { ...match, permissionNames: [...match.permissionNames] }
    } catch (err) {
      loadError.value = err.message
    }
  }

  await catalogPromise
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/roles/${props.appRoleKey}` : '/api/admin/roles'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      saveError.value = await problemMessage(response, t('common.saveFailed', { status: response.status }))
      return
    }
    flash(savedMessage(t, editing.value.roleName))
    router.push({ name: 'admin-roles-permissions' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-roles-permissions' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? t('admin.roleEdit.editTitle') : t('admin.roleEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.roleEdit.roleName') }}</span> <input v-model="editing.roleName" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.roleEdit.description') }}</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.roleEdit.notificationEmail') }}</span> <input v-model="editing.notificationEmail" type="email" :placeholder="t('admin.roleEdit.notificationEmailPlaceholder')" /></label></p>
      <fieldset>
        <legend>{{ t('admin.roleEdit.permissions') }}</legend>
        <label v-for="perm in catalog" :key="perm.permissionKey" style="display: block">
          <input
            type="checkbox"
            :checked="editing.permissionNames.includes(perm.permissionName)"
            @change="togglePermission(perm.permissionName)"
          />
          {{ perm.permissionName }} — {{ perm.description }}
        </label>
      </fieldset>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
