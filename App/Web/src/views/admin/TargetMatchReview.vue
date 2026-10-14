<script setup>
// Design_Risk-Scoring.md, D-101-105, D-119 Phase B: the weak-match
// (IP-only, etc.) review queue -- these rows never got auto-merged during
// an import, so an analyst confirms Merged/NewTarget/Ignored by hand.
// D-225: the target to merge into is picked with a typeahead (Combobox.vue,
// GET /api/admin/targets/search) instead of typing a Target key; nothing
// picked merges into the candidate, as before.
import { ref, onMounted } from 'vue'
import Combobox from '../../components/Combobox.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)
const mergeTargetByRow = ref({})

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-scoring/target-match-review')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
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
  if (!response.ok) throw new Error(t('admin.targetMatchReview.searchFailed', { status: response.status }))
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
    error.value = problem?.detail ?? t('admin.targetMatchReview.resolveFailed', { status: response.status })
    return
  }
  delete mergeTargetByRow.value[item.targetMatchReviewKey]
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.targetMatchReview.title') }}</h2>
    <p>{{ t('admin.targetMatchReview.intro') }}</p>
    <p>{{ t('admin.targetMatchReview.mergeHint') }}</p>
    <p role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <table v-if="items.length > 0">
        <thead>
          <tr><th>{{ t('admin.targetMatchReview.identifier') }}</th><th>{{ t('admin.targetMatchReview.candidateTarget') }}</th><th>{{ t('admin.targetMatchReview.sourceFile') }}</th><th>{{ t('admin.targetMatchReview.mergeInto') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetMatchReviewKey">
            <td>{{ item.identifierType }} = {{ item.identifierValue }}</td>
            <td>{{ item.candidateTargetName ?? item.candidateTargetKey }}</td>
            <td>{{ item.sourceFileName }}</td>
            <td>
              <Combobox
                v-model="mergeTargetByRow[item.targetMatchReviewKey]"
                :label="t('admin.targetMatchReview.mergeLabel', { type: item.identifierType, value: item.identifierValue })"
                :search="searchTargets"
                :item-key="target => target.targetKey"
                :item-label="targetLabel"
                :placeholder="item.candidateTargetName ? t('admin.targetMatchReview.candidatePlaceholder', { name: item.candidateTargetName }) : t('admin.targetMatchReview.searchPlaceholder')"
              >
                <template #option="{ item: target }">
                  <strong>{{ target.targetName }}</strong> <span>({{ target.targetTypeDisplayName }})</span>
                  <br v-if="target.identifiers.length" />
                  <small>{{ target.identifiers.map(i => `${i.identifierType}: ${i.identifierValue}`).join(', ') }}</small>
                </template>
              </Combobox>
            </td>
            <td>
              <button @click="resolve(item, 'Merged')">{{ t('admin.targetMatchReview.merge') }}</button>
              <button @click="resolve(item, 'NewTarget')">{{ t('admin.targetMatchReview.newTarget') }}</button>
              <button @click="resolve(item, 'Ignored')">{{ t('admin.targetMatchReview.ignore') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else>{{ t('admin.targetMatchReview.noReviews') }}</p>
    </template>
  </div>
</template>
