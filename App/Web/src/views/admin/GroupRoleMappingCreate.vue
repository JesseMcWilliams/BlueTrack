<script setup>
// D-124 Phase 4 (partial conversion): only the "Add Mapping" form moves off
// GroupRoleMapping.vue onto this routed page -- that page is otherwise
// delete-only per row (no matching "Edit" concept to move alongside it, so
// none is invented here) and its separate Lookup/Test Tool stays exactly
// where it is.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const roles = ref([])
const loadError = ref(null)
const saveError = ref(null)
const saving = ref(false)
const loading = ref(true)

const newGroupName = ref('')
const newRoleName = ref('')

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/group-role-mappings/roles')
    if (!response.ok) throw new Error(`Roles request failed: ${response.status}`)
    roles.value = await response.json()
    if (roles.value.length > 0) newRoleName.value = roles.value[0].roleName
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
})

async function createMapping() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/group-role-mappings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ groupName: newGroupName.value, roleName: newRoleName.value })
    })
    if (!response.ok) {
      saveError.value = `Create failed: ${response.status}`
      return
    }
    router.push({ name: 'admin-group-role-mapping' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-group-role-mapping' })
}
</script>

<template>
  <div>
    <h2>Add Mapping</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="createMapping">
      <p><label class="field-label"><span class="field-label-text">Group Name (e.g. BUILTIN\Administrators or DOMAIN\GroupName):</span> <input v-model="newGroupName" required /></label></p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Role:</span>
          <select v-model="newRoleName" required>
            <option value="" disabled>Select a role</option>
            <option v-for="role in roles" :key="role.appRoleKey" :value="role.roleName">{{ role.roleName }}</option>
          </select>
        </label>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">Add</button>
      <button type="button" @click="cancel">Cancel</button>
    </form>
  </div>
</template>
