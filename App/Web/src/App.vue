<script setup>
// Top-level nav per D-43: Dashboard | Accounts | Exceptions | Reports |
// Admin | user menu. Admin and Reports are hub pages with their own
// sub-navigation (D-47, D-56), so they get a single top-level entry each.
import Breadcrumbs from './components/Breadcrumbs.vue'
</script>

<template>
  <div id="layout">
    <nav class="top-nav">
      <router-link :to="{ name: 'dashboard' }">Dashboard</router-link>
      <router-link :to="{ name: 'account-progress-list' }">Accounts</router-link>
      <router-link :to="{ name: 'risk-exceptions-list' }">Exceptions</router-link>
      <router-link :to="{ name: 'reports' }">Reports</router-link>
      <router-link :to="{ name: 'admin' }">Admin</router-link>
      <router-link :to="{ name: 'my-profile' }" class="top-nav__user-menu">My Profile</router-link>
    </nav>
    <Breadcrumbs />
    <main>
      <router-view />
    </main>
  </div>
</template>

<style scoped>
.top-nav {
  display: flex;
  gap: 1rem;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid #ddd;
}
.top-nav__user-menu {
  margin-left: auto;
}
</style>
