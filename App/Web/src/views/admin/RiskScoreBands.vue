<script setup>
// D-120: an admin-configurable set of named bands over the computed
// EffectiveRiskScore (web.account_risk_score) -- a brand-new lookup, NOT
// tied to dbo.dim_risk_level (the pre-existing, manually assigned label
// with no numeric score). Mirrors AccessGroups.vue's simpler shape (single
// add/edit/delete, no nested child collection, no bulk import).
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)
const saveError = ref(null)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-score-bands')
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
  saveError.value = null
  editing.value = { bandName: '', minScore: 0, maxScore: 0, riskOrder: 0 }
}
function startEdit(item) {
  saveError.value = null
  editing.value = { ...item }
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  saveError.value = null
  const isNew = editing.value.riskScoreBandKey === undefined
  const url = isNew ? '/api/admin/risk-score-bands' : `/api/admin/risk-score-bands/${editing.value.riskScoreBandKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(editing.value)
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    saveError.value = problem?.message ?? `Save failed: ${response.status}`
    return
  }
  editing.value = null
  await load()
}

async function remove(item) {
  const response = await fetch(`/api/admin/risk-score-bands/${item.riskScoreBandKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Risk Score Bands</h2>
    <p>Named ranges over the computed risk score (0-1000) -- e.g. Low/Medium/High/Critical. The resulting band name shows up on the Account Progress list and the Risk Score report. A new/edited range must not overlap any other existing band.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Band</button>

      <table>
        <thead>
          <tr><th>Name</th><th>Min Score</th><th>Max Score</th><th>Order</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.riskScoreBandKey">
            <td>{{ item.bandName }}</td>
            <td>{{ item.minScore }}</td>
            <td>{{ item.maxScore }}</td>
            <td>{{ item.riskOrder }}</td>
            <td>
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.riskScoreBandKey === undefined ? 'New Band' : 'Edit Band' }}</h3>
        <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.bandName" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Min Score:</span> <input v-model.number="editing.minScore" type="number" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Max Score:</span> <input v-model.number="editing.maxScore" type="number" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Risk Order:</span> <input v-model.number="editing.riskOrder" type="number" required /></label></p>
        <p v-if="saveError" role="alert">{{ saveError }}</p>
        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
