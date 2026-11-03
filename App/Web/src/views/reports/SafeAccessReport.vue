<script setup>
// D-300/D-301: Reports > Safe Access. GET /api/reports/safe-access (ViewDashboard): every Safe with accounts, failing
// first, with the Safe access policy that applied (the first enabled one whose Safe name pattern matches), what failed
// and how many of its accounts have had their compliance reviewed. Review accounts marks every account in the Safe
// not reviewed yet (one review each; ReviewAccountCompliance). The policies are on Admin > Safe Access Policies.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../../stores/rights'
import { formatUtcDateTime } from '../../utils/formatLocale'
import { formatCount } from '../../utils/formatCount'
import { flash } from '../../composables/useFlash'

const { t } = useI18n()
const rights = useRightsStore()
const items = ref([])
const error = ref(null)
const actionError = ref(null)
const loading = ref(true)
const failingOnly = ref(false)
const busySafe = ref(null)

const shown = computed(() => failingOnly.value ? items.value.filter(r => r.isCompliant === false) : items.value)
const checkedAt = computed(() => items.value[0]?.checkedAt ?? null)

async function load() {
  try {
    const response = await fetch('/api/reports/safe-access')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function result(row) {
  if (row.isCompliant === true) return t('reports.safeAccess.passed')
  if (row.isCompliant === false) return t('reports.safeAccess.failed')
  return t('reports.safeAccess.noPolicy')
}

async function reviewSafe(row) {
  actionError.value = null
  busySafe.value = row.safeKey
  try {
    const response = await fetch(`/api/reports/safe-access/${row.safeKey}/review`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({})
    })
    if (!response.ok) {
      actionError.value = t('common.saveFailed', { status: response.status })
      return
    }
    const body = await response.json()
    flash(t('reports.safeAccess.reviewedFlash', { count: formatCount(body.reviewedCount), safe: row.safeName }, body.reviewedCount))
    await load()
  } finally {
    busySafe.value = null
  }
}
</script>

<template>
  <div>
    <h2>{{ t('reports.safeAccess.title') }}</h2>
    <p class="hint">{{ t('reports.safeAccess.hint') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.safeAccess.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.safeAccess.empty') }}</p>
    <template v-else>
      <p v-if="checkedAt"><small>{{ t('reports.safeAccess.checkedAt', { time: formatUtcDateTime(checkedAt) }) }}</small></p>
      <p><label><input v-model="failingOnly" type="checkbox" /> {{ t('reports.safeAccess.failingOnly') }}</label></p>
      <p v-if="actionError" role="alert">{{ actionError }}</p>
      <table>
        <thead>
          <tr>
            <th>{{ t('reports.safeAccess.colSafe') }}</th>
            <th>{{ t('reports.safeAccess.colPolicy') }}</th>
            <th>{{ t('reports.safeAccess.colResult') }}</th>
            <th>{{ t('reports.safeAccess.colProblems') }}</th>
            <th>{{ t('reports.safeAccess.colReviewed') }}</th>
            <th v-if="rights.hasPermission('ReviewAccountCompliance')"><span class="visually-hidden">{{ t('reports.safeAccess.colActions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in shown" :key="row.safeKey">
            <td>{{ row.safeName }} <small>({{ row.source }})</small></td>
            <td>{{ row.policyName ?? '' }}</td>
            <td :class="{ 'safe-access-failed': row.isCompliant === false }">{{ result(row) }}</td>
            <td>
              <ul v-if="row.problems.length" class="problems">
                <li v-for="(problem, i) in row.problems" :key="i">{{ problem }}</li>
              </ul>
            </td>
            <td>{{ t('reports.safeAccess.reviewedOf', { reviewed: formatCount(row.reviewedCount), total: formatCount(row.accountCount) }) }}</td>
            <td v-if="rights.hasPermission('ReviewAccountCompliance')">
              <button v-if="row.reviewedCount < row.accountCount" type="button" :disabled="busySafe === row.safeKey" @click="reviewSafe(row)">
                {{ t('reports.safeAccess.reviewAccounts') }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.safe-access-failed {
  font-weight: 600;
}
.problems {
  margin: 0;
  padding-left: 1.1rem;
}
</style>
