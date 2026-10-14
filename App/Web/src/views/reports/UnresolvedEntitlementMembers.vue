<script setup>
// Calls GET /api/reports/unresolved-entitlement-members (ReportsController),
// backed by dbo.vw_unresolved_entitlement_members -- Safe entitlements
// granted to a CyberArk Identity/Entra-federated user or built-in cloud
// role rather than a classic Vault-native user/group, so this app has no
// display name to show (D-107/D-108). Read-only, no permission gate.
import { ref, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/unresolved-entitlement-members')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
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
    <h2>{{ t('reports.unresolvedEntitlementMembers.title') }}</h2>
    <p class="hint">{{ t('reports.unresolvedEntitlementMembers.hint') }}</p>
    <ReportExportButtons path="/api/reports/unresolved-entitlement-members/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.unresolvedEntitlementMembers.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.unresolvedEntitlementMembers.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.unresolvedEntitlementMembers.colSafe') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colMemberType') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colIdentifier') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colExpiration') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colSnapshotDate') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.entitlementKey">
          <td>{{ item.safeName }}</td>
          <td>{{ item.memberType }}</td>
          <td>
            <span class="unresolved-flag">{{ t('reports.unresolvedEntitlementMembers.unresolved') }}</span>
            {{ item.unresolvedMemberId }}
          </td>
          <td>{{ formatDate(item.membershipExpirationDate) }}</td>
          <td>{{ formatDate(item.snapshotDate) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}

.unresolved-flag {
  font-weight: bold;
  color: var(--color-error-text);
}
</style>
