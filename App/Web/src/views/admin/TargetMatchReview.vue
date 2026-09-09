<script setup>
// Design_Risk_Scoring.md, D-101-105, D-119 Phase B: the weak-match
// (IP-only, etc.) review queue -- these rows never got auto-merged during
// an import, so an analyst confirms Merged/NewTarget/Ignored by hand.
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)
const mergeTargetKeyByRow = ref({})

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-scoring/target-match-review')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function resolve(item, resolution) {
  const body = { resolution }
  if (resolution === 'Merged') {
    body.mergeIntoTargetKey = mergeTargetKeyByRow.value[item.targetMatchReviewKey] || item.candidateTargetKey
  }
  const response = await fetch(`/api/admin/risk-scoring/target-match-review/${item.targetMatchReviewKey}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  })
  if (!response.ok) {
    error.value = `Resolve failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Target Match Review</h2>
    <p>A weak match (e.g. an IP address alone, which can be reassigned by DHCP/NAT) found during import -- confirm rather than auto-merge.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <table v-if="items.length > 0">
        <thead>
          <tr><th>Identifier</th><th>Candidate Target</th><th>Source File</th><th>Merge Into (Target Key)</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetMatchReviewKey">
            <td>{{ item.identifierType }} = {{ item.identifierValue }}</td>
            <td>{{ item.candidateTargetName ?? item.candidateTargetKey }}</td>
            <td>{{ item.sourceFileName }}</td>
            <td><input v-model.number="mergeTargetKeyByRow[item.targetMatchReviewKey]" type="number" :placeholder="String(item.candidateTargetKey ?? '')" /></td>
            <td>
              <button @click="resolve(item, 'Merged')">Merge</button>
              <button @click="resolve(item, 'NewTarget')">New Target</button>
              <button @click="resolve(item, 'Ignored')">Ignore</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else>No pending reviews.</p>
    </template>
  </div>
</template>
