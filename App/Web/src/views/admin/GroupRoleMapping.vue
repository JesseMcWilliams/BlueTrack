<script setup>
// CRUD + lookup/test tool against /api/admin/group-role-mappings
// (GroupRoleMappingsController). For WindowsIntegrated, admins type a
// friendly group name and the server resolves it to the SID that's actually
// stored (D-69) and matched against at login. D-204: OIDC and SAML groups
// are stored as the IdP sends them in the groups claim.
//
// D-124 Phase 4 (partial conversion): the "Add Mapping" form moved to its
// own routed page (GroupRoleMappingCreate.vue,
// admin-group-role-mapping-create) -- this page no longer owns that inline
// form or its own roles fetch (used only by that form). Delete-only per
// row and the Lookup/Test Tool below are both untouched.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'

const { t } = useI18n()

const router = useRouter()

const mappings = ref([])
const error = ref(null)
const loading = ref(true)

const lookupGroupName = ref('')
const lookupResult = ref(null)
const lookupError = ref(null)
const providers = ref([])
const lookupProviderKey = ref(null)

const lookupProvider = computed(() => providers.value.find(p => p.providerKey === lookupProviderKey.value) ?? null)
const lookupIsWindows = computed(() => !lookupProvider.value || lookupProvider.value.providerType === 'WindowsIntegrated')

async function load() {
  loading.value = true
  try {
    const [mappingsResponse, providersResponse] = await Promise.all([
      fetch('/api/admin/group-role-mappings'),
      fetch('/api/admin/group-role-mappings/providers')
    ])
    if (!mappingsResponse.ok) throw new Error(t('common.requestFailed', { status: mappingsResponse.status }))
    mappings.value = await mappingsResponse.json()
    if (providersResponse.ok) {
      providers.value = await providersResponse.json()
      if (lookupProviderKey.value === null) {
        lookupProviderKey.value = providers.value.find(p => p.providerType === 'WindowsIntegrated')?.providerKey ?? providers.value[0]?.providerKey ?? null
      }
    }
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
    body: JSON.stringify({ groupName: lookupGroupName.value, providerKey: lookupProviderKey.value })
  })
  if (!response.ok) {
    lookupError.value = response.status === 404 ? t('admin.groupRoleMapping.couldNotResolve') : t('common.requestFailed', { status: response.status })
    return
  }
  lookupResult.value = await response.json()
}

// D-107/Archive_Planning_Outstanding-Work-Survey-2026-09-16.md: generates a filled-in copy of
// Database/Manual/03_BlueTrack_GrantBackupStatusReaderRole.sql targeting the
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
    scriptGenerationError.value = response.status === 404 ? t('admin.groupRoleMapping.couldNotResolve') : t('common.requestFailed', { status: response.status })
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

// D-179: Windows mappings store only a SID (D-69); the API resolves it to
// DOMAIN\Group when listing. Fall back to the stored identifier when it
// couldn't be resolved, or for providers whose identifier is already readable.
function groupLabel(mapping) {
  return mapping.groupDisplayName || mapping.identityGroupName
}

async function remove(mapping) {
  if (!(await confirmDelete(t('admin.groupRoleMapping.confirmDelete', { group: groupLabel(mapping), role: mapping.roleName })))) return
  const response = await fetch(`/api/admin/group-role-mappings/${mapping.mappingKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.groupRoleMapping.title') }}</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-group-role-mapping-create' })">{{ t('admin.groupRoleMapping.addMapping') }}</button></p>

      <table>
        <thead>
          <tr><th>{{ t('admin.groupRoleMapping.provider') }}</th><th>{{ t('admin.groupRoleMapping.group') }}</th><th>{{ t('admin.groupRoleMapping.role') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="mapping in mappings" :key="mapping.mappingKey">
            <td>{{ mapping.providerDisplayName }} <span class="group-identifier">{{ mapping.providerType }}</span></td>
            <td>
              {{ groupLabel(mapping) }}
              <span v-if="mapping.groupDisplayName" class="group-identifier">{{ mapping.identityGroupName }}</span>
              <span v-else-if="mapping.providerType === 'WindowsIntegrated'" class="group-identifier">{{ t('admin.groupRoleMapping.nameNotResolved') }}</span>
            </td>
            <td>{{ mapping.roleName }}</td>
            <td><button @click="remove(mapping)" :aria-label="t('common.deleteNamed', { name: groupLabel(mapping) })">{{ t('common.delete') }}</button></td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="!loading && mappings.length === 0" :message="t('admin.groupRoleMapping.emptyNone')" />

      <h3>{{ t('admin.groupRoleMapping.lookupHeading') }}</h3>
      <p>{{ t('admin.groupRoleMapping.lookupIntro') }}</p>
      <form class="filter-row" @submit.prevent="lookup">
        <label v-if="providers.length > 1" class="field-label">
          <span class="field-label-text">{{ t('admin.groupRoleMapping.provider') }}</span>
          <select v-model="lookupProviderKey" @change="lookupResult = null">
            <option v-for="provider in providers" :key="provider.providerKey" :value="provider.providerKey">{{ provider.displayName }} ({{ provider.providerType }})</option>
          </select>
        </label>
        <label class="field-label"><span class="field-label-text">{{ t('admin.groupRoleMapping.groupName') }}</span> <input v-model="lookupGroupName" required /></label>
        <button type="submit">{{ t('admin.groupRoleMapping.resolve') }}</button>
      </form>
      <p v-if="lookupError" role="alert">{{ lookupError }}</p>
      <div v-if="lookupResult">
        <p v-if="lookupIsWindows">{{ t('admin.groupRoleMapping.resolvedTo', { name: lookupResult.resolvedAccountName, sid: lookupResult.sid }) }}</p>
        <p v-else>{{ t('admin.groupRoleMapping.groupClaimValue', { value: lookupResult.sid }) }}</p>
        <p>{{ t('admin.groupRoleMapping.currentRoles', { roles: lookupResult.currentRoleNames.join(', ') || t('admin.groupRoleMapping.noneMapped') }) }}</p>
        <p>{{ t('admin.groupRoleMapping.currentPermissions', { permissions: lookupResult.currentPermissionNames.join(', ') || t('common.none') }) }}</p>
        <template v-if="lookupIsWindows">
          <p>
            <button type="button" @click="generateBackupStatusReaderScript">{{ t('admin.groupRoleMapping.generateScript') }}</button>
          </p>
          <i18n-t keypath="admin.groupRoleMapping.scriptHint" tag="p" scope="global">
            <template #file><code>{{ 'Database/Manual/03_BlueTrack_GrantBackupStatusReaderRole.sql' }}</code></template>
            <template #tool><code>{{ 'sqlcmd' }}</code></template>
            <template #database><code>{{ 'msdb' }}</code></template>
          </i18n-t>
          <p v-if="scriptGenerationError" role="alert">{{ scriptGenerationError }}</p>
        </template>
      </div>
    </template>
  </div>
</template>

<style scoped>
/* D-179: the stored SID under the resolved group name. Size only, no colour,
   so every theme (including High Visibility) keeps its own text colour. */
.group-identifier {
  display: block;
  font-size: 0.85em;
}
</style>
