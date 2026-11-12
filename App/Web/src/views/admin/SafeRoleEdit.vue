<script setup>
// D-300: add or edit a Safe role: the Safe permissions a member must hold (and, with "exactly", no others).
// POST/PUT /api/admin/safe-access/roles; the permissions from GET /api/admin/safe-access/permissions. Duplicate
// (?copyOf=key) opens a new role filled from that one.
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { flash, savedMessage } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const props = defineProps({ roleKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const copyOf = useRoute().query.copyOf ?? null
const copiedFrom = ref(null)
const isEditMode = computed(() => props.roleKey !== null && props.roleKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const permissionNames = ref([])
const editing = ref({ roleName: '', description: '', exactMatch: false, permissions: [] })

onMounted(async () => {
  try {
    const [permissionsResponse, rolesResponse] = await Promise.all([
      fetch('/api/admin/safe-access/permissions'),
      isEditMode.value || copyOf !== null ? fetch('/api/admin/safe-access/roles') : Promise.resolve(null)
    ])
    for (const r of [permissionsResponse, rolesResponse]) {
      if (r && !r.ok) throw new Error(t('common.requestFailed', { status: r.status }))
    }
    permissionNames.value = await permissionsResponse.json()
    if (rolesResponse) {
      const wanted = isEditMode.value ? props.roleKey : copyOf
      const match = (await rolesResponse.json()).find(r => String(r.safeRoleKey) === String(wanted))
      if (!match) throw new Error(t('admin.safeRoleEdit.notFound'))
      if (!isEditMode.value) copiedFrom.value = match.roleName
      editing.value = {
        roleName: isEditMode.value ? match.roleName : t('admin.safeRoleEdit.copyName', { name: match.roleName }),
        description: match.description ?? '', exactMatch: match.exactMatch, permissions: [...match.permissions]
      }
    }
  } catch (err) {
    loadError.value = err.message
  }
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/safe-access/roles/${props.roleKey}` : '/api/admin/safe-access/roles'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    flash(savedMessage(t, editing.value.roleName))
    router.push({ name: 'admin-safe-access-policies' })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? t('admin.safeRoleEdit.editTitle') : t('admin.safeRoleEdit.newTitle') }}</h2>
    <p v-if="copiedFrom" class="hint copied-from">{{ t('admin.safeRoleEdit.copiedFrom', { name: copiedFrom }) }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>
    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.safeRoleEdit.name') }}</span> <input v-model="editing.roleName" required maxlength="100" /></label></p>
      <fieldset>
        <legend>{{ t('admin.safeRoleEdit.permissions') }}</legend>
        <div class="permissions">
          <label v-for="name in permissionNames" :key="name"><input v-model="editing.permissions" type="checkbox" :value="name" /> {{ name }}</label>
        </div>
      </fieldset>
      <p>
        <label><input v-model="editing.exactMatch" type="checkbox" /> {{ t('admin.safeRoleEdit.exactMatch') }}</label>
        <small>{{ ' ' + t('admin.safeRoleEdit.exactMatchHint') }}</small>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.safeRoleEdit.description') }}</span> <textarea v-model="editing.description" rows="2" maxlength="1000"></textarea></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="router.push({ name: 'admin-safe-access-policies' })">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>

<style scoped>
.permissions {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: 0.25rem 1rem;
}
</style>
