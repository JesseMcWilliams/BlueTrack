<script setup>
// CRUD + lookup/test tool against /api/admin/group-role-mappings
// (GroupRoleMappingsController), scoped to the WindowsIntegrated provider
// only (the only one that actually authenticates anyone). Admins type a
// friendly group name; the server resolves it to the SID that's actually
// stored (D-69) and matched against at login.
//
// D-124 Phase 4 (partial conversion): the "Add Mapping" form moved to its
// own routed page (GroupRoleMappingCreate.vue,
// admin-group-role-mapping-create) -- this page no longer owns that inline
// form or its own roles fetch (used only by that form). Delete-only per
// row and the Lookup/Test Tool below are both untouched.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'

const router = useRouter()

const mappings = ref([])
const error = ref(null)
const loading = ref(true)

const lookupGroupName = ref('')
const lookupResult = ref(null)
const lookupError = ref(null)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/group-role-mappings')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    mappings.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function lookup() {
  lookupError.value = null
  lookupResult.value = null
  const response = await fetch('/api/admin/group-role-mappings/resolve-group', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ groupName: lookupGroupName.value })
  })
  if (!response.ok) {
    lookupError.value = response.status === 404 ? 'Could not resolve that group name.' : `Request failed: ${response.status}`
    return
  }
  lookupResult.value = await response.json()
}

// D-107/Outstanding_Work_Survey.md: generates a filled-in copy of
// Database/38_BlueTrack_GrantBackupStatusReaderRole.sql targeting the
// group just resolved above, for a DBA to run manually via sqlcmd --
// this app never runs it itself (it's an msdb permission grant, outside
// what this app's own least-privileged connection can do to itself).
const scriptGenerationError = ref(null)

async function generateBackupStatusReaderScript() {
  scriptGenerationError.value = null
  const response = await fetch('/api/admin/group-role-mappings/generate-backupstatus-reader-script', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ groupName: lookupGroupName.value })
  })
  if (!response.ok) {
    scriptGenerationError.value = response.status === 404 ? 'Could not resolve that group name.' : `Request failed: ${response.status}`
    return
  }
  const blob = await response.blob()
  const disposition = response.headers.get('Content-Disposition') || ''
  const fileNameMatch = disposition.match(/filename="?([^"]+)"?/)
  const fileName = fileNameMatch ? fileNameMatch[1] : 'Grant-db_backupstatus_reader.sql'

  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

async function remove(mapping) {
  if (!(await confirmDelete(`Delete mapping "${mapping.identityGroupName} → ${mapping.roleName}"? This cannot be undone.`))) return
  const response = await fetch(`/api/admin/group-role-mappings/${mapping.mappingKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Group → Role Mapping</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-group-role-mapping-create' })">+ Add Mapping</button></p>

      <table>
        <thead>
          <tr><th>Provider</th><th>Group (stored identifier)</th><th>Role</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="mapping in mappings" :key="mapping.mappingKey">
            <td>{{ mapping.providerType }}</td>
            <td>{{ mapping.identityGroupName }}</td>
            <td>{{ mapping.roleName }}</td>
            <td><button @click="remove(mapping)">Delete</button></td>
          </tr>
        </tbody>
      </table>

      <h3>Lookup / Test Tool</h3>
      <p>Resolve a group name and see what it currently grants, without saving anything.</p>
      <form class="filter-row" @submit.prevent="lookup">
        <label class="field-label"><span class="field-label-text">Group name</span> <input v-model="lookupGroupName" required /></label>
        <button type="submit">Resolve</button>
      </form>
      <p v-if="lookupError" role="alert">{{ lookupError }}</p>
      <div v-if="lookupResult">
        <p>Resolved to: {{ lookupResult.resolvedAccountName }} ({{ lookupResult.sid }})</p>
        <p>Current role(s): {{ lookupResult.currentRoleNames.join(', ') || '(none mapped)' }}</p>
        <p>Current permission(s): {{ lookupResult.currentPermissionNames.join(', ') || '(none)' }}</p>
        <p>
          <button type="button" @click="generateBackupStatusReaderScript">Generate db_backupstatus_reader Script</button>
        </p>
        <p>
          Downloads a copy of <code>Database/38_BlueTrack_GrantBackupStatusReaderRole.sql</code> targeting this
          resolved account -- a DBA still has to run it manually via <code>sqlcmd</code> against <code>msdb</code>,
          never through the app itself.
        </p>
        <p v-if="scriptGenerationError" role="alert">{{ scriptGenerationError }}</p>
      </div>
    </template>
  </div>
</template>
