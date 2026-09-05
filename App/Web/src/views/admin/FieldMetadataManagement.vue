<script setup>
// CRUD against /api/admin/field-metadata (FieldMetadataController).
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null) // null = not editing; {} = new; object = existing item being edited

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

function startCreate() {
  editing.value = { fieldName: '', displayLabel: '', fieldType: 'text', referenceTable: '', isRequired: false, displayOrder: 0 }
}
function startEdit(item) {
  editing.value = { ...item }
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.fieldMetadataKey === undefined
  const url = isNew ? '/api/admin/field-metadata' : `/api/admin/field-metadata/${editing.value.fieldMetadataKey}`
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
      <button @click="startCreate">+ New Field</button>

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
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.fieldMetadataKey === undefined ? 'New Field' : 'Edit Field' }}</h3>
        <p><label>Field Name: <input v-model="editing.fieldName" required /></label></p>
        <p><label>Display Label: <input v-model="editing.displayLabel" required /></label></p>
        <p><label>Field Type: <input v-model="editing.fieldType" required /></label></p>
        <p><label>Reference Table: <input v-model="editing.referenceTable" /></label></p>
        <p><label><input v-model="editing.isRequired" type="checkbox" /> Required</label></p>
        <p><label>Display Order: <input v-model.number="editing.displayOrder" type="number" /></label></p>
        <button type="submit">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
