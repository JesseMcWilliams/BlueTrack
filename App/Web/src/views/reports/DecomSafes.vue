<script setup>
// D-186: GET /api/reports/decom-safes (web.vw_decom_safe) -- safes whose
// name matches the safe decommission pattern on Global Application
// Configuration. Ignored safes aren't listed. Read-only, no permission gate.
import { ref, onMounted } from 'vue'
import { formatCount } from '../../utils/formatCount'
import { useRightsStore } from '../../stores/rights'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const rights = useRightsStore() // UI review W6-4: the application link needs CurateApplicationMapping
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/decom-safes')
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
    <h2>{{ t('reports.decomSafes.title') }}</h2>
    <i18n-t keypath="reports.decomSafes.hint" tag="p" class="hint" scope="global">
      <template #pattern><strong>{{ t('reports.decomSafes.hintPattern') }}</strong></template>
      <template #prefix><code>{{ 'DEL_' }}</code></template>
      <template #suffix><code>{{ '_DECOM' }}</code></template>
      <template #accountsLink><router-link :to="{ name: 'reports-decom-accounts' }">{{ t('reports.decomSafes.hintAccountsLink') }}</router-link></template>
    </i18n-t>
    <p class="hint">{{ t('reports.decomSafes.whatNext') }}</p>
    <ReportExportButtons path="/api/reports/decom-safes/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.decomSafes.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.decomSafes.empty') }}</p>
    <table v-else>
      <thead>
        <tr><th>{{ t('reports.decomSafes.colSafe') }}</th><th>{{ t('reports.decomSafes.colSource') }}</th><th>{{ t('reports.decomSafes.colApplication') }}</th><th>{{ t('reports.decomSafes.colActiveAccounts') }}</th><th>{{ t('reports.decomSafes.colDeletedAccounts') }}</th></tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.safeKey">
          <td>{{ item.safeName }}</td>
          <td>{{ item.sourceSystemName }}</td>
          <td>
            <router-link v-if="item.applicationKey && item.applicationName && rights.hasPermission('CurateApplicationMapping')" :to="{ name: 'admin-application-edit', params: { applicationKey: item.applicationKey } }">{{ item.applicationName }}</router-link>
            <template v-else>{{ item.applicationName }}</template>
          </td>
          <td>
            <!-- UI review W6-1: opens the Accounts report for this Safe. -->
            <router-link v-if="item.activeAccountCount + item.deletedAccountCount > 0" :to="{ name: 'reports-decom-accounts', query: { safe: item.safeName } }" :aria-label="t('reports.decomSafes.showAccounts', { safe: item.safeName })">{{ formatCount(item.activeAccountCount) }}</router-link>
            <template v-else>{{ formatCount(item.activeAccountCount) }}</template>
          </td>
          <td>{{ formatCount(item.deletedAccountCount) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
