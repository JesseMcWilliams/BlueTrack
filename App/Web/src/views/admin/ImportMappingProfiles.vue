<script setup>
// Design_Risk_Scoring.md, D-101-105, D-119 Phase B: a named field mapping
// per distinct source file shape -- an admin types in which real column
// name in their export corresponds to each internal field. Uploading this
// app's own generated template needs no profile at all (its headers
// already match the internal field names 1:1).
import { ref, onMounted } from 'vue'

const FEED_TYPES = ['TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap', 'AccountAccessGroupMembership', 'AccountTargetMap']

const items = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-scoring/import-mapping-profiles')
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
  editing.value = { feedType: FEED_TYPES[0], profileName: '', isActive: true, description: '', fields: [] }
}
function startEdit(item) {
  editing.value = { ...item, fields: item.fields.map(f => ({ ...f })) }
}
function cancelEdit() {
  editing.value = null
}
function addFieldRow() {
  editing.value.fields.push({ sourceColumnName: '', targetFieldName: '', isRequired: false, defaultValue: '' })
}
function removeFieldRow(index) {
  editing.value.fields.splice(index, 1)
}

async function save() {
  const isNew = editing.value.importMappingProfileKey === undefined
  const url = isNew ? '/api/admin/risk-scoring/import-mapping-profiles' : `/api/admin/risk-scoring/import-mapping-profiles/${editing.value.importMappingProfileKey}`
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
  const response = await fetch(`/api/admin/risk-scoring/import-mapping-profiles/${item.importMappingProfileKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Import Mapping Profiles</h2>
    <p>One named profile per distinct source file shape for the risk-scoring import feeds -- not needed at all for this app's own generated CSV templates.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Profile</button>

      <table>
        <thead>
          <tr><th>Feed Type</th><th>Name</th><th>Active</th><th>Fields</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.importMappingProfileKey">
            <td>{{ item.feedType }}</td>
            <td>{{ item.profileName }}</td>
            <td>{{ item.isActive ? 'Yes' : 'No' }}</td>
            <td>{{ item.fields.map(f => `${f.sourceColumnName} -> ${f.targetFieldName}`).join(', ') }}</td>
            <td>
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.importMappingProfileKey === undefined ? 'New Profile' : 'Edit Profile' }}</h3>
        <p>
          <label class="field-label">
            <span class="field-label-text">Feed Type:</span>
            <select v-model="editing.feedType">
              <option v-for="type in FEED_TYPES" :key="type" :value="type">{{ type }}</option>
            </select>
          </label>
        </p>
        <p><label class="field-label"><span class="field-label-text">Profile Name:</span> <input v-model="editing.profileName" required /></label></p>
        <p><label><input v-model="editing.isActive" type="checkbox" /> Active</label></p>
        <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>

        <h4>Fields</h4>
        <div v-for="(field, index) in editing.fields" :key="index" class="filter-row">
          <label class="field-label"><span class="field-label-text">Source Column:</span> <input v-model="field.sourceColumnName" required /></label>
          <label class="field-label"><span class="field-label-text">Internal Field:</span> <input v-model="field.targetFieldName" required /></label>
          <label><input v-model="field.isRequired" type="checkbox" /> Required</label>
          <label class="field-label"><span class="field-label-text">Default:</span> <input v-model="field.defaultValue" /></label>
          <button type="button" @click="removeFieldRow(index)">Remove</button>
        </div>
        <p><button type="button" @click="addFieldRow">+ Add Field</button></p>

        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
