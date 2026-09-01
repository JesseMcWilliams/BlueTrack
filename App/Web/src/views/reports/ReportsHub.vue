<script setup>
// Reports hub with sub-navigation (D-56) -- mirrors the Admin hub pattern (D-47).
</script>

<template>
  <div>
    <nav class="reports-subnav">
      <router-link :to="{ name: 'reports-overdue-worklist' }">Overdue / At-Risk</router-link>
      <router-link :to="{ name: 'reports-stage-status-summary' }">Stage/Status Summary</router-link>
      <router-link :to="{ name: 'reports-reconciliation-review' }">Reconciliation Review</router-link>
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
