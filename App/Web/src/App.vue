<script setup>
// Top-level nav per D-43: Dashboard | Accounts | Exceptions | Reports |
// Admin | user menu. Admin and Reports are hub pages with their own
// sub-navigation (D-47, D-56), so they get a single top-level entry each.
// D-121 added Targets/Access Groups as two more top-level entries (each
// gated on its own ManageTargets/ManageAccessGroups permission), promoted
// out of the Admin hub now that Analyst holds those permissions too.
import { ref, onMounted } from 'vue'
import Breadcrumbs from './components/Breadcrumbs.vue'
import { useRightsStore } from './stores/rights'

const rights = useRightsStore()

// DevFakeAuth-enabled-too-long warning (added 2026-09-06, user-requested):
// DevFakeAuth bypasses real authentication and is meant only for local
// development -- left enabled for an extended period on a shared
// environment is a real exposure. Checked here (App.vue), not a specific
// page, so it's visible wherever an admin happens to be browsing.
// Gated behind ManageIdentityProviders both because that's who can
// actually act on it, and because GET /api/admin/identity-providers
// itself requires that policy -- calling it without the permission
// would just 403 for every other user on every single page load.
// ModifiedDate is the closest available signal for "enabled since," not
// a dedicated one -- see IdentityProviderDetail.cs's own comment on why
// that's an approximation, not exact.
const devFakeAuthWarning = ref(null)
const DEV_FAKE_AUTH_STALE_DAYS = 7

async function checkDevFakeAuthDuration() {
  if (!rights.hasPermission('ManageIdentityProviders')) return
  try {
    const response = await fetch('/api/admin/identity-providers')
    if (!response.ok) return
    const providers = await response.json()
    const devFakeAuth = providers.find(p => p.providerType === 'DevFakeAuth')
    if (!devFakeAuth?.isEnabled || !devFakeAuth.modifiedDate) return

    const enabledDays = (Date.now() - new Date(devFakeAuth.modifiedDate).getTime()) / (1000 * 60 * 60 * 24)
    if (enabledDays >= DEV_FAKE_AUTH_STALE_DAYS) {
      devFakeAuthWarning.value =
        `DevFakeAuth has been enabled for over ${DEV_FAKE_AUTH_STALE_DAYS} days (since ${devFakeAuth.modifiedDate.slice(0, 10)}). ` +
        'It bypasses real authentication and should only be left on briefly during local development.'
    }
  } catch {
    // Non-fatal -- this is an advisory banner, not a page any user needs to load.
  }
}

// Loaded once here so every page can read permissions without each one
// re-fetching /api/me -- the frontend permission-aware UI pass. Pages that
// need permissions before rendering (e.g. AccountProgressDetail deciding
// whether to acquire the edit lock) await ensureLoaded() themselves rather
// than assume this has already run -- Vue mounts children before parents,
// so a child route can mount before this does.
onMounted(async () => {
  await rights.ensureLoaded()
  await checkDevFakeAuthDuration()
})
</script>

<template>
  <div id="layout">
    <a href="#main-content" class="skip-link visually-hidden">Skip to main content</a>
    <nav class="top-nav" aria-label="Primary">
      <router-link :to="{ name: 'dashboard' }">Dashboard</router-link>
      <router-link :to="{ name: 'account-progress-list' }">Accounts</router-link>
      <router-link :to="{ name: 'risk-exceptions-list' }">Exceptions</router-link>
      <router-link :to="{ name: 'reports' }">Reports</router-link>
      <!-- D-121: Targets/Access Groups promoted out of the Admin hub to
           their own top-level entries -- gated per-permission here (unlike
           Dashboard/Accounts/Exceptions/Reports/Admin above, which need no
           gate of their own), same hasPermission() pattern AdminHub.vue's
           own sidebar already uses for each of its sections. -->
      <router-link v-if="rights.hasPermission('ManageTargets')" :to="{ name: 'targets' }">Targets</router-link>
      <router-link v-if="rights.hasPermission('ManageAccessGroups')" :to="{ name: 'access-groups' }">Access Groups</router-link>
      <router-link :to="{ name: 'admin' }">Admin</router-link>
      <router-link :to="{ name: 'my-profile' }" class="top-nav__user-menu">My Profile</router-link>
    </nav>
    <Breadcrumbs />
    <p v-if="devFakeAuthWarning" role="alert" class="dev-fake-auth-warning">{{ devFakeAuthWarning }}</p>
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
.dev-fake-auth-warning {
  margin: 0;
  padding: var(--space-3) var(--space-4);
  border-bottom: 2px solid var(--color-error-text);
  color: var(--color-error-text);
  font-weight: bold;
}
</style>
