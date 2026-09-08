<script setup>
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

// D-45/D-57: every page shows a breadcrumb trail; position defaults to
// top-left and is admin-configurable via app_config.BreadcrumbPosition.
// This scaffold hardcodes top-left (see the `breadcrumbs--top-left` class
// below) -- reading the live app_config value is a follow-up build task.
const route = useRoute()
const router = useRouter()

// Walks meta.breadcrumbParent (router/index.js), not route.matched --
// route.matched only reflects actual nested-route parentage, which is
// correct for Reports/Admin's hub+children structure but wrong for flat
// sibling routes like account-progress-list/-detail (a detail page's
// dynamic segment is a sibling of its list page, not nested under it).
// Found 2026-09-06: that mismatch meant a detail page's breadcrumb never
// included its list page at all, on every list/detail pair in the app,
// and the dashboard page duplicated itself (route.matched there is just
// [dashboard], appended after this component's own hardcoded root link).
const routesByName = new Map(router.getRoutes().map((r) => [r.name, r]))

const crumbs = computed(() => {
  const trail = []
  const seen = new Set()
  let current = routesByName.get(route.name)

  while (current && current.name !== 'dashboard' && !seen.has(current.name)) {
    seen.add(current.name)
    trail.unshift({ name: current.name, label: current.meta?.breadcrumb ?? String(current.name) })
    current = routesByName.get(current.meta?.breadcrumbParent ?? 'dashboard')
  }

  return trail
})
</script>

<template>
  <nav class="breadcrumbs breadcrumbs--top-left" aria-label="Breadcrumb">
    <router-link :to="{ name: 'dashboard' }">Dashboard</router-link>
    <span v-for="crumb in crumbs" :key="crumb.name">
      <span class="breadcrumbs__separator">/</span>
      <router-link :to="{ name: crumb.name }">{{ crumb.label }}</router-link>
    </span>
  </nav>
</template>

<style scoped>
/* Usability pass, 2026-09-06: previously padding was vertical-only (0 on
   the horizontal axis), so the trail sat flush against the browser edge
   with nothing distinguishing it from the page heading directly below --
   the exact "no separation" gap this pass addresses. The bottom border
   gives it a clear visual boundary as its own strip, consistent with
   .top-nav's own border-bottom above it. */
.breadcrumbs {
  font-size: 0.875rem;
  padding: var(--space-2) var(--space-4);
  border-bottom: 1px solid var(--color-border);
}
.breadcrumbs__separator {
  margin: 0 0.4rem;
}
</style>
