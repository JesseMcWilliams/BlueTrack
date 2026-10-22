<script setup>
// Reports hub with sub-navigation (D-56) -- mirrors the Admin hub (D-47):
// D-220, the reports are listed in a column on the left, the report itself
// on the right. Reconciliation Review and Possible Account Matches
// (ConfirmReconciliation, D-56/D-212), Risk Score (ViewRiskReport,
// D-101-105 Phase E), Risk Exception SoD (ViewRiskExceptionSodReport) and
// Discovered Accounts (ViewDiscoveredAccounts) are shown only to holders of
// that permission. The rest stay unconditionally visible, as before: their
// API endpoints need only ViewDashboard (D-197), which every role has.
import { computed } from 'vue'
import { useRightsStore } from '../../stores/rights'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rights = useRightsStore()

const reports = [
  { name: 'reports-overdue-worklist', labelKey: 'reports.reportsHub.overdueWorklist' },
  { name: 'reports-stage-status-summary', labelKey: 'reports.reportsHub.stageStatusSummary' },
  { name: 'reports-reconciliation-review', labelKey: 'reports.reportsHub.reconciliationReview', permission: 'ConfirmReconciliation' },
  { name: 'reports-possible-matches', labelKey: 'reports.reportsHub.possibleMatches', permission: 'ConfirmReconciliation' },
  { name: 'reports-unresolved-entitlement-members', labelKey: 'reports.reportsHub.unresolvedEntitlementMembers' },
  { name: 'reports-cpm-health', labelKey: 'reports.reportsHub.cpmHealth' },   // D-288
  { name: 'reports-cyberark-applications', labelKey: 'reports.reportsHub.cyberArkApplications' },   // D-282
  { name: 'reports-risk-score', labelKey: 'reports.reportsHub.riskScore', permission: 'ViewRiskReport' },
  { name: 'reports-risk-exception-sod', labelKey: 'reports.reportsHub.riskExceptionSod', permission: 'ViewRiskExceptionSodReport' },
  { name: 'reports-discovered-accounts', labelKey: 'reports.reportsHub.discoveredAccounts', permission: 'ViewDiscoveredAccounts' },
  { name: 'reports-decom-safes', labelKey: 'reports.reportsHub.decomSafes' },
  { name: 'reports-decom-accounts', labelKey: 'reports.reportsHub.decomAccounts' },
  { name: 'reports-kpi-summary', labelKey: 'reports.reportsHub.kpiSummary' },
  { name: 'reports-trends', labelKey: 'reports.reportsHub.trends' }   // D-221
]

const visibleReports = computed(() => reports.filter(r => !r.permission || rights.hasPermission(r.permission)))
</script>

<template>
  <div class="reports-layout">
    <nav class="reports-subnav" :aria-label="t('reports.reportsHub.navLabel')">
      <router-link v-for="r in visibleReports" :key="r.name" :to="{ name: r.name }">{{ t(r.labelKey) }}</router-link>
    </nav>
    <div class="reports-content">
      <router-view />
    </div>
  </div>
</template>

<style scoped>
.reports-layout {
  display: flex;
  gap: 2rem;
}
.reports-subnav {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  min-width: 220px;
}
/* No overflow here: a scroll container would stop the tables' sticky headers (D-136) following the page. */
.reports-content {
  flex: 1;
  min-width: 0;
}
</style>
