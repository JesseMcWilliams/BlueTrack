<script setup>
// Reports hub with sub-navigation (D-56) -- mirrors the Admin hub (D-47):
// D-220, the reports are listed in a column on the left, the report itself
// on the right. Reconciliation Review and Possible Account Matches
// (ConfirmReconciliation, D-56/D-212), Risk Score (ViewRiskReport,
// D-101-105 Phase E), Risk Exception SoD (ViewRiskExceptionSodReport) and
// Discovered Accounts (ViewDiscoveredAccounts) are shown only to holders of
// that permission. The rest need ViewDashboard (D-197), which the seeded
// Auditor role lacks (D-304), so each report is shown only to those who may
// open it (routePermissions.js).
import { computed } from 'vue'
import { useRightsStore } from '../../stores/rights'
import { canOpen } from '../../router/routePermissions'
import { useSubnavCurrent } from '../../composables/useSubnavCurrent'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rights = useRightsStore()

// D-330: the 16 reports in four groups, in this order; each group's reports keep the order below. The Reports
// entry still opens the first one the user may see in REPORT_PAGES (routePermissions.js), the first listed here.
const GROUPS = [
  { key: 'progress', labelKey: 'reports.reportsHub.groups.progress' },
  { key: 'reconciliation', labelKey: 'reports.reportsHub.groups.reconciliation' },
  { key: 'risk', labelKey: 'reports.reportsHub.groups.risk' },
  { key: 'decommission', labelKey: 'reports.reportsHub.groups.decommission' }
]

const reports = [
  { name: 'reports-overdue-worklist', labelKey: 'reports.reportsHub.overdueWorklist', group: 'progress' },
  { name: 'reports-stage-status-summary', labelKey: 'reports.reportsHub.stageStatusSummary', group: 'progress' },
  { name: 'reports-kpi-summary', labelKey: 'reports.reportsHub.kpiSummary', group: 'progress' },
  { name: 'reports-trends', labelKey: 'reports.reportsHub.trends', group: 'progress' },   // D-221
  { name: 'reports-reconciliation-review', labelKey: 'reports.reportsHub.reconciliationReview', group: 'reconciliation' },
  { name: 'reports-possible-matches', labelKey: 'reports.reportsHub.possibleMatches', group: 'reconciliation' },
  { name: 'reports-unresolved-entitlement-members', labelKey: 'reports.reportsHub.unresolvedEntitlementMembers', group: 'reconciliation' },
  { name: 'reports-discovered-accounts', labelKey: 'reports.reportsHub.discoveredAccounts', group: 'reconciliation' },
  { name: 'reports-dna-accounts', labelKey: 'reports.reportsHub.dnaAccounts', group: 'reconciliation' },   // D-325
  { name: 'reports-dna-hard-coded-credentials', labelKey: 'reports.reportsHub.dnaHardCoded', group: 'reconciliation' },   // D-345
  { name: 'reports-risk-score', labelKey: 'reports.reportsHub.riskScore', group: 'risk' },
  { name: 'reports-risk-exception-sod', labelKey: 'reports.reportsHub.riskExceptionSod', group: 'risk' },
  { name: 'reports-safe-access', labelKey: 'reports.reportsHub.safeAccess', group: 'risk' },   // D-300
  { name: 'reports-safe-administrators', labelKey: 'reports.reportsHub.safeAdministrators', group: 'risk' },   // D-335
  { name: 'reports-cyberark-applications', labelKey: 'reports.reportsHub.cyberArkApplications', group: 'risk' },   // D-282
  { name: 'reports-cpm-health', labelKey: 'reports.reportsHub.cpmHealth', group: 'risk' },   // D-288
  { name: 'reports-decom-safes', labelKey: 'reports.reportsHub.decomSafes', group: 'decommission' },
  { name: 'reports-decom-accounts', labelKey: 'reports.reportsHub.decomAccounts', group: 'decommission' }
]

const visibleReports = computed(() => reports.filter(r => canOpen(rights, r.name)))
const visibleGroups = computed(() => GROUPS
  .map(g => ({ ...g, reports: visibleReports.value.filter(r => r.group === g.key) }))
  .filter(g => g.reports.length > 0))
const isCurrent = useSubnavCurrent()
</script>

<template>
  <div class="reports-layout">
    <nav class="reports-subnav hub-subnav" :aria-label="t('reports.reportsHub.navLabel')">
      <div v-for="g in visibleGroups" :key="g.key" class="hub-subnav-group" role="group" :aria-labelledby="`reports-group-${g.key}`">
        <span :id="`reports-group-${g.key}`" class="hub-subnav-group-title">{{ t(g.labelKey) }}</span>
        <router-link v-for="r in g.reports" :key="r.name" :to="{ name: r.name }" :class="{ 'hub-subnav-current': isCurrent(r.name) }">{{ t(r.labelKey) }}</router-link>
      </div>
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
  min-width: 220px;
}
/* D-330: on a narrow screen the reports go above the report, not beside it. */
@media (max-width: 900px) {
  .reports-layout {
    flex-direction: column;
    gap: var(--space-4);
  }
}
/* No overflow here: a scroll container would stop the tables' sticky headers (D-136) following the page. */
.reports-content {
  flex: 1;
  min-width: 0;
}
</style>
