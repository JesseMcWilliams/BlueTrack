<script setup>
// Design_Risk-Scoring.md, D-101-105, D-119 Phase B: a named field mapping
// per distinct source file shape -- an admin types in which real column
// name in their export corresponds to each internal field. Uploading this
// app's own generated template needs no profile at all (its headers
// already match the internal field names 1:1).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (ImportMappingProfileEdit.vue, admin-import-mapping-profile-create/-edit)
// -- this page no longer owns an inline editing/startCreate/startEdit/
// cancelEdit/addFieldRow/removeFieldRow form.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-scoring/import-mapping-profiles')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(item) {
  if (!(await confirmDelete(t('admin.importMappingProfiles.confirmDelete', { name: item.profileName })))) return
  const response = await fetch(`/api/admin/risk-scoring/import-mapping-profiles/${item.importMappingProfileKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.importMappingProfiles.title') }}</h2>
    <p>{{ t('admin.importMappingProfiles.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-import-mapping-profile-create' })">{{ t('admin.importMappingProfiles.newProfile') }}</button></p>

      <table>
        <thead>
          <tr><th>{{ t('admin.importMappingProfiles.feedType') }}</th><th>{{ t('common.name') }}</th><th>{{ t('common.active') }}</th><th>{{ t('admin.importMappingProfiles.fields') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.importMappingProfileKey">
            <td>{{ item.feedType }}</td>
            <td><router-link :to="{ name: 'admin-import-mapping-profile-edit', params: { importMappingProfileKey: item.importMappingProfileKey } }">{{ item.profileName }}</router-link></td>
            <td>{{ item.isActive ? t('admin.importMappingProfiles.yes') : t('admin.importMappingProfiles.no') }}</td>
            <td>{{ item.fields.map(f => `${f.sourceColumnName} -> ${f.targetFieldName}`).join(', ') }}</td>
            <td>
              <button @click="remove(item)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
