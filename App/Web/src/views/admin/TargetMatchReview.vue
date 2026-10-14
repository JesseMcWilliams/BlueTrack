<script setup>
// Design_Risk-Scoring.md, D-101-105, D-119 Phase B: the weak-match
// (IP-only, etc.) review queue -- these rows never got auto-merged during
// an import, so an analyst confirms Merged/NewTarget/Ignored by hand.
// D-225: the target to merge into is picked with a typeahead (Combobox.vue,
// GET /api/admin/targets/search) instead of typing a Target key; nothing
// picked merges into the candidate, as before.
import { ref, onMounted } from 'vue'
import Combobox from '../../components/Combobox.vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)
const mergeTargetByRow = ref({})

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

async function searchTargets(text) {
  const response = await fetch(`/api/admin/targets/search?q=${encodeURIComponent(text)}`)
  if (!response.ok) throw new Error(`Search failed: ${response.status}`)
  return response.json()
}

function targetLabel(target) {
  return `${target.targetName} (${target.targetTypeDisplayName})`
}

async function resolve(item, resolution) {
  error.value = null
  const body = { resolution }
  if (resolution === 'Merged') {
    body.mergeIntoTargetKey = mergeTargetByRow.value[item.targetMatchReviewKey]?.targetKey ?? item.candidateTargetKey
  }
  const response = await fetch(`/api/admin/risk-scoring/target-match-review/${item.targetMatchReviewKey}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    error.value = problem?.detail ?? `Resolve failed: ${response.status}`
    return
  }
  delete mergeTargetByRow.value[item.targetMatchReviewKey]
  await load()
}
</script>

<template>
  <div>
    <h2>Target Match Review</h2>
    <p>A weak match (e.g. an IP address alone, which can be reassigned by DHCP/NAT) found during import -- confirm rather than auto-merge.</p>
    <p>To merge into a different target than the candidate, search for it by name or by any identifier (at least 2 characters). Leave it empty to merge into the candidate.</p>
    <p role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <table v-if="items.length > 0">
        <thead>
          <tr><th>Identifier</th><th>Candidate Target</th><th>Source File</th><th>Merge Into</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetMatchReviewKey">
            <td>{{ item.identifierType }} = {{ item.identifierValue }}</td>
            <td>{{ item.candidateTargetName ?? item.candidateTargetKey }}</td>
            <td>{{ item.sourceFileName }}</td>
            <td>
              <Combobox
                v-model="mergeTargetByRow[item.targetMatchReviewKey]"
                :label="`Merge ${item.identifierType} ${item.identifierValue} into target`"
                :search="searchTargets"
                :item-key="t => t.targetKey"
                :item-label="targetLabel"
                :placeholder="item.candidateTargetName ? `Candidate: ${item.candidateTargetName}` : 'Search targets'"
              >
                <template #option="{ item: target }">
                  <strong>{{ target.targetName }}</strong> <span>({{ target.targetTypeDisplayName }})</span>
                  <br v-if="target.identifiers.length" />
                  <small>{{ target.identifiers.map(i => `${i.identifierType}: ${i.identifierValue}`).join(', ') }}</small>
                </template>
              </Combobox>
            </td>
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
