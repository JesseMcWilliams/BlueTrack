<script setup>
// CRUD against /api/admin/roles + read-only /api/admin/permissions catalog
// (RolesController). The permission catalog itself isn't editable here --
// it's confirmed/fixed (D-05, D-61) -- only which permissions each role bundles.
import { ref, onMounted } from 'vue'

const roles = ref([])
const catalog = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

async function load() {
  loading.value = true
  try {
    const [rolesResponse, catalogResponse] = await Promise.all([
      fetch('/api/admin/roles'),
      fetch('/api/admin/permissions')
    ])
    if (!rolesResponse.ok) throw new Error(`Roles request failed: ${rolesResponse.status}`)
    if (!catalogResponse.ok) throw new Error(`Permissions request failed: ${catalogResponse.status}`)
    roles.value = await rolesResponse.json()
    catalog.value = await catalogResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function startCreate() {
  editing.value = { roleName: '', description: '', permissionNames: [] }
}
function startEdit(role) {
  editing.value = { ...role, permissionNames: [...role.permissionNames] }
}
function cancelEdit() {
  editing.value = null
}
function togglePermission(name) {
  const set = new Set(editing.value.permissionNames)
  if (set.has(name)) set.delete(name)
  else set.add(name)
  editing.value.permissionNames = [...set]
}

async function save() {
  const isNew = editing.value.appRoleKey === undefined
  const url = isNew ? '/api/admin/roles' : `/api/admin/roles/${editing.value.appRoleKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(editing.value)
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  editing.value = null
  await load()
}

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
      <button @click="startCreate">+ New Role</button>

      <table>
        <thead>
          <tr><th>Role</th><th>Description</th><th>Permissions</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="role in roles" :key="role.appRoleKey">
            <td>{{ role.roleName }}</td>
            <td>{{ role.description }}</td>
            <td>{{ role.permissionNames.join(', ') }}</td>
            <td>
              <button @click="startEdit(role)">Edit</button>
              <button @click="remove(role)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.appRoleKey === undefined ? 'New Role' : 'Edit Role' }}</h3>
        <p><label>Role Name: <input v-model="editing.roleName" required /></label></p>
        <p><label>Description: <input v-model="editing.description" /></label></p>
        <p>
          Permissions:
          <label v-for="perm in catalog" :key="perm.permissionKey" style="display: block">
            <input
              type="checkbox"
              :checked="editing.permissionNames.includes(perm.permissionName)"
              @change="togglePermission(perm.permissionName)"
            />
            {{ perm.permissionName }} — {{ perm.description }}
          </label>
        </p>
        <button type="submit">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
