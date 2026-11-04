<script setup>
// Top-level nav per D-43: Dashboard | Accounts | Exceptions | Reports |
// Admin | user menu. Admin and Reports are hub pages with their own
// sub-navigation (D-47, D-56), so they get a single top-level entry each.
// D-121 added Targets/Access Groups as two more top-level entries (each
// gated on its own ManageTargets/ManageAccessGroups permission), promoted
// out of the Admin hub now that Analyst holds those permissions too.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import Breadcrumbs from './components/Breadcrumbs.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import UserMenu from './components/UserMenu.vue'
import { useIdleSignOut } from './composables/useIdleSignOut'
import FlashMessage from './components/FlashMessage.vue'
import IdleWarning from './components/IdleWarning.vue'
import { useRightsStore } from './stores/rights'
import { flashNavigated } from './composables/useFlash'
import { useRoute, useRouter } from 'vue-router'
import { formatDate } from './utils/formatDate'
import { canOpen, canOpenReports as canOpenReportsFor, canOpenAdmin as canOpenAdminFor } from './router/routePermissions'

const { t } = useI18n()
const rights = useRightsStore()
const route = useRoute()
// UI review step 4b: a signed-in user with no permissions gets no menu entries, only the user menu.
// D-305: the tracked-data pages and Reports need ViewDashboard (or a report's own permission); hide what the user can't open.
// D-330: nor does anyone before they are known to be signed in (the sign-in page showed Home and Admin). If /api/me
// failed, the entries still show, as before, and the pages report the failure.
const canOpenReports = computed(() => canOpenReportsFor(rights))
const canOpenAdmin = computed(() => canOpenAdminFor(rights))
const hasAccess = computed(() => !!rights.error || (rights.authenticated === true && rights.permissionNames.length > 0))
const signedIn = computed(() => rights.authenticated === true)

// D-330: the top-nav entry for the area the current page belongs to, by path, so an account's page marks Accounts
// and a Safe's page marks Accounts too (they are separate routes, so the router's own active class misses them).
const NAV_SECTIONS = [
  ['dashboard', /^\/dashboard(\/|$)/],
  ['access-groups', /^\/access-groups(\/|$)/],
  ['accounts', /^\/(accounts|safes)(\/|$)/],
  ['exceptions', /^\/exceptions(\/|$)/],
  ['targets', /^\/targets(\/|$)/],
  ['reports', /^\/reports(\/|$)/],
  ['admin', /^\/admin(\/|$)/]
]
const currentSection = computed(() => {
  if (route.path === '/') return 'home'
  return NAV_SECTIONS.find(([, pattern]) => pattern.test(route.path))?.[0] ?? null
})
const navClass = (section) => ({ 'nav-current': currentSection.value === section })
// UI review theme 3: a "saved" line set before a navigation shows on the page it lands on, and goes at the next one.
useRouter().afterEach(() => flashNavigated())
const { warning: idleWarning, touch: stayActive } = useIdleSignOut() // D-209: sign out after Idle Timeout without activity

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
// D-227: the date it was enabled since; the text is rendered from shell.json.
const devFakeAuthSince = ref(null)
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
      devFakeAuthSince.value = formatDate(devFakeAuth.modifiedDate)
    }
  } catch {
    // Non-fatal -- this is an advisory banner, not a page any user needs to load.
  }
}

// D-156: Windows Integrated Authentication disabled banner. Backed by the
// same code-level guard as the flag itself (AuthenticationExtensions.
// IsNegotiateDisabled requires IsDevelopment() *and* the config flag,
// never either alone) -- this banner is a visibility layer on top of that
// guard, not a substitute for it. Checked via the public, anonymous
// GET /api/auth/providers rather than something permission-gated,
// deliberately: anyone looking at this environment, signed in or not,
// should immediately know it isn't running with normal authentication.
const negotiateDisabled = ref(false)

async function checkNegotiateDisabled() {
  try {
    const response = await fetch('/api/auth/providers')
    if (!response.ok) return
    const data = await response.json()
    if (data.negotiateDisabled) {
      negotiateDisabled.value = true
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
  await checkNegotiateDisabled()
  await rights.ensureLoaded()
  await checkDevFakeAuthDuration()
})
</script>

<template>
  <div id="layout">
    <a href="#main-content" class="skip-link visually-hidden">{{ t('shell.app.skipToMain') }}</a>
    <!-- D-124: Dashboard always leftmost, the working pages alphabetical by
         label (Access Groups, Accounts, Exceptions, Targets); D-213: then
         Reports, and Admin always last. The user menu (D-206: the signed-in
         user's name, with Profile & Preferences, About and Sign out) stays
         pinned right. -->
    <nav v-if="hasAccess || signedIn" class="top-nav" :aria-label="t('shell.nav.ariaLabel')">
      <template v-if="hasAccess">
      <router-link :to="{ name: 'home' }" :class="navClass('home')">{{ t('shell.nav.home') }}</router-link>
      <router-link v-if="canOpen(rights, 'dashboard')" :to="{ name: 'dashboard' }" :class="navClass('dashboard')">{{ t('shell.nav.dashboard') }}</router-link>
      <!-- D-121: Targets/Access Groups gated per-permission here, same
           hasPermission() pattern AdminHub.vue's own sidebar already uses for
           each of its sections. D-305: Dashboard/Accounts/Exceptions/Reports
           too, by the router's own map. D-330: Admin only when it has a section for the user, as on Home. -->
      <router-link v-if="rights.hasPermission('ManageAccessGroups')" :to="{ name: 'access-groups' }" :class="navClass('access-groups')">{{ t('shell.nav.accessGroups') }}</router-link>
      <router-link v-if="canOpen(rights, 'account-progress-list')" :to="{ name: 'account-progress-list' }" :class="navClass('accounts')">{{ t('shell.nav.accounts') }}</router-link>
      <router-link v-if="canOpen(rights, 'risk-exceptions-list')" :to="{ name: 'risk-exceptions-list' }" :class="navClass('exceptions')">{{ t('shell.nav.exceptions') }}</router-link>
      <router-link v-if="rights.hasPermission('ManageTargets')" :to="{ name: 'targets' }" :class="navClass('targets')">{{ t('shell.nav.targets') }}</router-link>
      <router-link v-if="canOpenReports" :to="{ name: 'reports' }" :class="navClass('reports')">{{ t('shell.nav.reports') }}</router-link>
      <router-link v-if="canOpenAdmin" :to="{ name: 'admin' }" :class="navClass('admin')">{{ t('shell.nav.admin') }}</router-link>
      </template>
      <UserMenu v-if="rights.authenticated" />
    </nav>
    <!-- D-330: Home's trail would be "Home" alone, and the sign-in page's leads back to it. -->
    <Breadcrumbs v-if="signedIn && route.name !== 'home'" />
    <FlashMessage />
    <IdleWarning :warning="idleWarning" @stay="stayActive" />
    <p v-if="negotiateDisabled" role="alert" class="dev-fake-auth-warning">{{ t('shell.app.negotiateDisabledWarning') }}</p>
    <p v-if="devFakeAuthSince" role="alert" class="dev-fake-auth-warning">{{ t('shell.app.devFakeAuthWarning', { days: DEV_FAKE_AUTH_STALE_DAYS, since: devFakeAuthSince }) }}</p>
    <!-- D-92: route-change focus target (router/index.js's afterEach hook
         moves focus here) -- a client-routed SPA gives assistive tech no
         "page changed" signal otherwise. tabindex="-1" lets it receive
         programmatic focus without joining the normal Tab order. -->
    <main id="main-content" tabindex="-1">
      <!-- D-280: an account page links to its linked accounts; a new account key must load a new page (and release
           the old one's edit lock), so that route is keyed by its path. Other routes keep their instance. -->
      <router-view v-slot="{ Component, route }">
        <component :is="Component" :key="route.name === 'account-progress-detail' ? route.path : undefined" />
      </router-view>
    </main>
    <div id="route-announcer" class="visually-hidden" role="status" aria-live="polite"></div>
    <ConfirmDialog />
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
  flex-wrap: wrap; /* D-330: wraps on a narrow screen instead of running off it */
  align-items: center;
  gap: var(--space-2) var(--space-4);
  padding: var(--space-2) var(--space-4);
  border-bottom: 1px solid var(--color-border);
}
/* D-330: the area the current page belongs to; a bar as well as bold, so it doesn't rely on weight alone. */
.top-nav .nav-current {
  font-weight: bold;
  text-decoration-thickness: 3px;
  text-underline-offset: 0.35em;
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
