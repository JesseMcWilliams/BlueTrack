<script setup>
// D-186: GET /api/reports/decom-accounts (web.vw_decom_account) --
// accounts in a safe flagged for deletion, or whose own name matches the
// account decommission pattern. An account not yet deleted gets a note; if
// the same account (username + address) is also in another, unflagged
// safe, the note names it -- it has probably moved there. Read-only, no
// permission gate.
import { ref, computed, onMounted } from 'vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)
const hideDeleted = ref(false)

const shown = computed(() => (hideDeleted.value ? items.value.filter(i => !i.isDeleted) : items.value))

function why(item) {
  const reasons = []
  if (item.inFlaggedSafe) reasons.push(t('reports.decomAccounts.safeFlagged'))
  if (item.nameFlagged) reasons.push(t('reports.decomAccounts.nameFlagged'))
  return reasons.join(', ')
}

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/decom-accounts')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>{{ t('reports.decomAccounts.title') }}</h2>
    <i18n-t keypath="reports.decomAccounts.hint" tag="p" class="hint" scope="global">
      <template #safeLink><router-link :to="{ name: 'reports-decom-safes' }">{{ t('reports.decomAccounts.hintSafeLink') }}</router-link></template>
      <template #pattern><strong>{{ t('reports.decomAccounts.hintPattern') }}</strong></template>
      <template #notDeleted><strong>{{ t('reports.decomAccounts.hintNotDeleted') }}</strong></template>
      <template #alsoIn><strong>{{ t('reports.decomAccounts.hintAlsoIn') }}</strong></template>
    </i18n-t>
    <p class="hint">{{ t('reports.decomAccounts.whatNext') }}</p>
    <ReportExportButtons path="/api/reports/decom-accounts/export" :params="{ notDeletedOnly: hideDeleted }" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.decomAccounts.loadFailed', { error }) }}</p>
    <template v-else>
      <p><label><input v-model="hideDeleted" type="checkbox" /> {{ t('reports.decomAccounts.onlyNotDeleted') }}</label></p>
      <p v-if="shown.length === 0">{{ t('reports.decomAccounts.empty') }}</p>
      <table v-else>
        <thead>
          <tr><th>{{ t('reports.decomAccounts.colUsername') }}</th><th>{{ t('reports.decomAccounts.colAddress') }}</th><th>{{ t('reports.decomAccounts.colAccountName') }}</th><th>{{ t('reports.decomAccounts.colSafe') }}</th><th>{{ t('reports.decomAccounts.colSource') }}</th><th>{{ t('reports.decomAccounts.colFlaggedBy') }}</th><th>{{ t('reports.decomAccounts.colNote') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="item in shown" :key="item.accountKey">
            <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: item.accountKey } }">{{ item.userName || item.accountName }}</router-link><span v-if="!item.userName" class="no-username">{{ ' ' + t('reports.decomAccounts.noUsername') }}</span></td>
            <td>{{ item.address }}</td>
            <td>{{ item.accountName }}</td>
            <td>{{ item.safeName }}</td>
            <td>{{ item.sourceSystemName }}</td>
            <td>{{ why(item) }}</td>
            <td>
              <template v-if="item.isDeleted">{{ t('reports.decomAccounts.deleted') }}</template>
              <template v-else><strong>{{ t('reports.decomAccounts.notDeletedYet') }}</strong></template>
              <template v-if="item.otherSafes">{{ ' ' + t('reports.decomAccounts.alsoIn', { safes: item.otherSafes }) }}</template>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.no-username {
  font-size: 0.85em;
  opacity: 0.8;
}
</style>
