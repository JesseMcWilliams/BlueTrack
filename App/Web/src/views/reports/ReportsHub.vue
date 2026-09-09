<script setup>
// Reports hub with sub-navigation (D-56) -- mirrors the Admin hub pattern
// (D-47). Reconciliation Review (ConfirmReconciliation, D-56) and Risk
// Score (ViewRiskReport, D-101-105 Phase E) are gated by a permission --
// Overdue/At-Risk, Stage/Status Summary, and Unresolved Entitlement
// Members (D-107/D-108) have no [Authorize(Policy = ...)] on their API
// endpoints, so they stay unconditionally visible here too.
import { useRightsStore } from '../../stores/rights'

const rights = useRightsStore()
</script>

<template>
  <div>
    <nav class="reports-subnav">
      <router-link :to="{ name: 'reports-overdue-worklist' }">Overdue / At-Risk</router-link>
      <router-link :to="{ name: 'reports-stage-status-summary' }">Stage/Status Summary</router-link>
      <router-link v-if="rights.hasPermission('ConfirmReconciliation')" :to="{ name: 'reports-reconciliation-review' }">
        Reconciliation Review
      </router-link>
      <router-link :to="{ name: 'reports-unresolved-entitlement-members' }">
        Unresolved Entitlement Members
      </router-link>
      <router-link v-if="rights.hasPermission('ViewRiskReport')" :to="{ name: 'reports-risk-score' }">
        Risk Score
      </router-link>
    </nav>
    <router-view />
  </div>
</template>

<style scoped>
.reports-subnav {
  display: flex;
  gap: 1rem;
  padding-bottom: 1rem;
}
</style>
