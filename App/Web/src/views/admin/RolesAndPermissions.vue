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

const roles = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const rolesResponse = await fetch('/api/admin/roles')
    if (!rolesResponse.ok) throw new Error(`Roles request failed: ${rolesResponse.status}`)
    roles.value = await rolesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(role) {
  const response = await fetch(`/api/admin/roles/${role.appRoleKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a role still mapped to a group can't be deleted)`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Roles & Permissions</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><router-link :to="{ name: 'admin-role-create' }">+ New Role</router-link></p>

      <table>
        <thead>
          <tr><th>Role</th><th>Description</th><th>Notification Email</th><th>Permissions</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="role in roles" :key="role.appRoleKey">
            <td>{{ role.roleName }}</td>
            <td>{{ role.description }}</td>
            <td>{{ role.notificationEmail }}</td>
            <td>{{ role.permissionNames.join(', ') }}</td>
            <td>
              <router-link :to="{ name: 'admin-role-edit', params: { appRoleKey: role.appRoleKey } }">Edit</router-link>
              <button @click="remove(role)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
