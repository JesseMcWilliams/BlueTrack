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

const rights = useRightsStore()

const reports = [
  { name: 'reports-overdue-worklist', label: 'Overdue / At-Risk' },
  { name: 'reports-stage-status-summary', label: 'Stage/Status Summary' },
  { name: 'reports-reconciliation-review', label: 'Reconciliation Review', permission: 'ConfirmReconciliation' },
  { name: 'reports-possible-matches', label: 'Possible Account Matches', permission: 'ConfirmReconciliation' },
  { name: 'reports-unresolved-entitlement-members', label: 'Unresolved Entitlement Members' },
  { name: 'reports-risk-score', label: 'Risk Score', permission: 'ViewRiskReport' },
  { name: 'reports-risk-exception-sod', label: 'Risk Exception SoD', permission: 'ViewRiskExceptionSodReport' },
  { name: 'reports-discovered-accounts', label: 'Discovered Accounts', permission: 'ViewDiscoveredAccounts' },
  { name: 'reports-decom-safes', label: 'Safes Flagged for Deletion' },
  { name: 'reports-decom-accounts', label: 'Accounts Flagged for Deletion' },
  { name: 'reports-kpi-summary', label: 'KPI Summary' },
  { name: 'reports-trends', label: 'Trends' }   // D-221
]

const visibleReports = computed(() => reports.filter(r => !r.permission || rights.hasPermission(r.permission)))
</script>

<template>
  <div class="reports-layout">
    <nav class="reports-subnav" aria-label="Reports">
      <router-link v-for="r in visibleReports" :key="r.name" :to="{ name: r.name }">{{ r.label }}</router-link>
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
