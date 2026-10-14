<script setup>
// CRUD against /api/admin/roles + read-only /api/admin/permissions catalog
// (RolesController). The permission catalog itself isn't editable here --
// it's confirmed/fixed (D-05, D-61) -- only which permissions each role bundles.
//
// D-124 Phase 4: Add/Edit moved to its own routed page (RoleEdit.vue,
// admin-role-create/-edit) -- this page no longer owns an inline
// editing/startCreate/startEdit/cancelEdit/togglePermission form, and no
// longer needs the permission catalog itself (that moved to RoleEdit.vue).
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()

const roles = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const rolesResponse = await fetch('/api/admin/roles')
    if (!rolesResponse.ok) throw new Error(t('admin.rolesAndPermissions.rolesRequestFailed', { status: rolesResponse.status }))
    roles.value = await rolesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(role) {
  if (!(await confirmDelete(t('admin.rolesAndPermissions.confirmDelete', { name: role.roleName })))) return
  const response = await fetch(`/api/admin/roles/${role.appRoleKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('admin.rolesAndPermissions.deleteFailedInUse', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.rolesAndPermissions.title') }}</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-role-create' })">{{ t('admin.rolesAndPermissions.newRole') }}</button></p>

      <table>
        <thead>
          <tr><th>{{ t('admin.rolesAndPermissions.role') }}</th><th>{{ t('common.description') }}</th><th>{{ t('admin.rolesAndPermissions.notificationEmail') }}</th><th>{{ t('admin.rolesAndPermissions.permissions') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="role in roles" :key="role.appRoleKey">
            <td><router-link :to="{ name: 'admin-role-edit', params: { appRoleKey: role.appRoleKey } }">{{ role.roleName }}</router-link></td>
            <td>{{ role.description }}</td>
            <td>{{ role.notificationEmail }}</td>
            <td>{{ role.permissionNames.join(', ') }}</td>
            <td>
              <button @click="remove(role)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
