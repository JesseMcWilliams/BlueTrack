<script setup>
import { computed } from 'vue'
import { useRoute } from 'vue-router'

// D-45/D-57: every page shows a breadcrumb trail; position defaults to
// top-left and is admin-configurable via app_config.BreadcrumbPosition.
// This scaffold hardcodes top-left (see the `breadcrumbs--top-left` class
// below) -- reading the live app_config value is a follow-up build task.
const route = useRoute()

const crumbs = computed(() =>
  route.matched
    .filter((r) => r.name)
    .map((r) => ({ name: r.name, path: r.path }))
)
</script>

<template>
  <nav class="breadcrumbs breadcrumbs--top-left" aria-label="Breadcrumb">
    <router-link :to="{ name: 'dashboard' }">Dashboard</router-link>
    <span v-for="crumb in crumbs" :key="crumb.name">
      <span class="breadcrumbs__separator">/</span>
      <router-link :to="{ name: crumb.name }">{{ String(crumb.name) }}</router-link>
    </span>
  </nav>
</template>

<style scoped>
.breadcrumbs {
  font-size: 0.875rem;
  padding: 0.5rem 0;
}
.breadcrumbs__separator {
  margin: 0 0.4rem;
}
</style>
