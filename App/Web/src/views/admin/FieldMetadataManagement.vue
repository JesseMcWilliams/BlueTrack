<script setup>
// CRUD against /api/admin/field-metadata (FieldMetadataController).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (FieldMetadataEdit.vue, admin-field-metadata-create/-edit) -- this page
// no longer owns an inline editing/startCreate/startEdit/cancelEdit form.
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/field-metadata')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(item) {
  const response = await fetch(`/api/admin/field-metadata/${item.fieldMetadataKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Field Metadata Management</h2>
    <p>Governed field-definition list backing the Account Progress edit form (Design_Interface_Extensibility.md).</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><router-link :to="{ name: 'admin-field-metadata-create' }">+ New Field</router-link></p>

      <table>
        <thead>
          <tr>
            <th>Field Name</th><th>Display Label</th><th>Type</th><th>Required</th><th>Order</th><th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.fieldMetadataKey">
            <td>{{ item.fieldName }}</td>
            <td>{{ item.displayLabel }}</td>
            <td>{{ item.fieldType }}</td>
            <td>{{ item.isRequired }}</td>
            <td>{{ item.displayOrder }}</td>
            <td>
              <router-link :to="{ name: 'admin-field-metadata-edit', params: { fieldMetadataKey: item.fieldMetadataKey } }">Edit</router-link>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
