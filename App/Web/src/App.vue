<script setup>
// Top-level nav per D-43: Dashboard | Accounts | Exceptions | Reports |
// Admin | user menu. Admin and Reports are hub pages with their own
// sub-navigation (D-47, D-56), so they get a single top-level entry each.
import { onMounted } from 'vue'
import Breadcrumbs from './components/Breadcrumbs.vue'
import { useRightsStore } from './stores/rights'

// Loaded once here so every page can read permissions without each one
// re-fetching /api/me -- the frontend permission-aware UI pass. Pages that
// need permissions before rendering (e.g. AccountProgressDetail deciding
// whether to acquire the edit lock) await ensureLoaded() themselves rather
// than assume this has already run -- Vue mounts children before parents,
// so a child route can mount before this does.
onMounted(() => useRightsStore().ensureLoaded())
</script>

<template>
  <div id="layout">
    <a href="#main-content" class="skip-link visually-hidden">Skip to main content</a>
    <nav class="top-nav" aria-label="Primary">
      <router-link :to="{ name: 'dashboard' }">Dashboard</router-link>
      <router-link :to="{ name: 'account-progress-list' }">Accounts</router-link>
      <router-link :to="{ name: 'risk-exceptions-list' }">Exceptions</router-link>
      <router-link :to="{ name: 'reports' }">Reports</router-link>
      <router-link :to="{ name: 'admin' }">Admin</router-link>
      <router-link :to="{ name: 'my-profile' }" class="top-nav__user-menu">My Profile</router-link>
    </nav>
    <Breadcrumbs />
    <!-- D-92: route-change focus target (router/index.js's afterEach hook
         moves focus here) -- a client-routed SPA gives assistive tech no
         "page changed" signal otherwise. tabindex="-1" lets it receive
         programmatic focus without joining the normal Tab order. -->
    <main id="main-content" tabindex="-1">
      <router-view />
    </main>
    <div id="route-announcer" class="visually-hidden" role="status" aria-live="polite"></div>
  </div>
</template>

<style scoped>
/* Usability pass, 2026-09-06: previously #main-content had no padding at
   all -- every page's content sat flush against the browser edge and
   directly under the breadcrumb bar with nothing separating them. See
   themes.css's own spacing-scale comment for the reasoning behind the
   var(--space-*) values used throughout this pass. */
.top-nav {
  display: flex;
  align-items: center;
  gap: var(--space-4);
  padding: var(--space-3) var(--space-4);
  border-bottom: 1px solid var(--color-border);
}
.top-nav__user-menu {
  margin-left: auto;
}
#main-content {
  padding: var(--space-4);
}
</style>
