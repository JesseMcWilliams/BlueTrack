<script setup>
// D-120: an admin-configurable set of named bands over the computed
// EffectiveRiskScore (web.account_risk_score) -- a brand-new lookup, NOT
// tied to dbo.dim_risk_level (the pre-existing, manually assigned label
// with no numeric score). Mirrors AccessGroups.vue's simpler shape (single
// add/edit/delete, no nested child collection, no bulk import).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (RiskScoreBandEdit.vue, admin-risk-score-band-create/-edit) -- this page
// no longer owns an inline editing/startCreate/startEdit/cancelEdit form.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const items = ref([])
const error = ref(null)
const loading = ref(true)

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
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-risk-score-band-create' })">+ New Band</button></p>

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
              <router-link :to="{ name: 'admin-risk-score-band-edit', params: { riskScoreBandKey: item.riskScoreBandKey } }">Edit</router-link>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
