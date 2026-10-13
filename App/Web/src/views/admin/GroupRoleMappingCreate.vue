<script setup>
// D-124 Phase 4 (partial conversion): only the "Add Mapping" form moves off
// GroupRoleMapping.vue onto this routed page -- that page is otherwise
// delete-only per row (no matching "Edit" concept to move alongside it, so
// none is invented here) and its separate Lookup/Test Tool stays exactly
// where it is.
//
// D-204: a provider choice. WindowsIntegrated groups are resolved to a SID by
// the server (D-69); OIDC/SAML groups are stored as the IdP sends them in the
// provider's groups claim, so the name must match exactly.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const roles = ref([])
const loadError = ref(null)
const saveError = ref(null)
const saving = ref(false)
const loading = ref(true)

const newGroupName = ref('')
const newRoleName = ref('')
const providers = ref([])
const providerKey = ref(null)

const provider = computed(() => providers.value.find(p => p.providerKey === providerKey.value) ?? null)
const isWindows = computed(() => !provider.value || provider.value.providerType === 'WindowsIntegrated')

onMounted(async () => {
  try {
    const [rolesResponse, providersResponse] = await Promise.all([
      fetch('/api/admin/group-role-mappings/roles'),
      fetch('/api/admin/group-role-mappings/providers')
    ])
    if (!rolesResponse.ok) throw new Error(`Roles request failed: ${rolesResponse.status}`)
    if (!providersResponse.ok) throw new Error(`Providers request failed: ${providersResponse.status}`)
    roles.value = await rolesResponse.json()
    providers.value = await providersResponse.json()
    if (roles.value.length > 0) newRoleName.value = roles.value[0].roleName
    providerKey.value = providers.value.find(p => p.providerType === 'WindowsIntegrated')?.providerKey ?? providers.value[0]?.providerKey ?? null
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
      body: JSON.stringify({ groupName: newGroupName.value, roleName: newRoleName.value, providerKey: providerKey.value })
    })
    if (!response.ok) {
      saveError.value = response.status === 409
        ? 'That group is already mapped to that role for this provider.'
        : response.status === 400 && isWindows.value
          ? 'Could not resolve that group name to a Windows group.'
          : `Create failed: ${response.status}`
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
      <p>
        <label class="field-label">
          <span class="field-label-text">Provider:</span>
          <select v-model="providerKey" required>
            <option v-for="p in providers" :key="p.providerKey" :value="p.providerKey">{{ p.displayName }} ({{ p.providerType }}){{ p.isEnabled ? '' : ' -- disabled' }}</option>
          </select>
        </label>
      </p>
      <p v-if="isWindows"><label class="field-label"><span class="field-label-text">Group Name (e.g. BUILTIN\Administrators or DOMAIN\GroupName):</span> <input v-model="newGroupName" required /></label></p>
      <template v-else>
        <p><label class="field-label"><span class="field-label-text">Group name, exactly as the identity provider sends it:</span> <input v-model="newGroupName" required maxlength="300" /></label></p>
        <p>Matched against the <code>{{ provider.groupClaimType || 'groups' }}</code> claim, set on the provider's Identity Providers page. Not checked against the identity provider when saved.</p>
      </template>
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
