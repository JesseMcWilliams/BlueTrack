<script setup>
// CRUD against /api/admin/field-metadata (FieldMetadataController).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (FieldMetadataEdit.vue, admin-field-metadata-create/-edit) -- this page
// no longer owns an inline editing/startCreate/startEdit/cancelEdit form.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'

const { t } = useI18n()

const router = useRouter()

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/field-metadata')
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
  if (!(await confirmDelete(t('admin.fieldMetadataManagement.confirmDelete', { name: item.displayLabel })))) return
  const response = await fetch(`/api/admin/field-metadata/${item.fieldMetadataKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.fieldMetadataManagement.title') }}</h2>
    <p>{{ t('admin.fieldMetadataManagement.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-field-metadata-create' })">{{ t('admin.fieldMetadataManagement.newField') }}</button></p>

      <table>
        <thead>
          <tr>
            <th>{{ t('admin.fieldMetadataManagement.fieldName') }}</th><th>{{ t('admin.fieldMetadataManagement.displayLabel') }}</th><th>{{ t('admin.fieldMetadataManagement.type') }}</th><th>{{ t('admin.fieldMetadataManagement.required') }}</th><th>{{ t('admin.fieldMetadataManagement.requiredPermission') }}</th><th>{{ t('admin.fieldMetadataManagement.order') }}</th><th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.fieldMetadataKey">
            <td><router-link :to="{ name: 'admin-field-metadata-edit', params: { fieldMetadataKey: item.fieldMetadataKey } }">{{ item.fieldName }}</router-link></td>
            <td>{{ item.displayLabel }}</td>
            <td>{{ item.fieldType }}</td>
            <td>{{ item.isRequired }}</td>
            <td>{{ item.requiredPermissionName ?? '' }}</td>
            <td>{{ item.displayOrder }}</td>
            <td>
              <button @click="remove(item)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="!loading && items.length === 0" :message="t('admin.fieldMetadataManagement.emptyNone')" />
    </template>
  </div>
</template>
