<script setup>
// Admin hub with sub-navigation (D-47): one top-nav entry, sections shown
// per the signed-in user's permissions. Permission-gating of the sidebar
// entries themselves isn't implemented yet -- this shows all sections
// unconditionally until that's built.
</script>

<template>
  <div class="admin-layout">
    <nav class="admin-subnav">
      <router-link :to="{ name: 'admin-identity-providers' }">Identity Providers</router-link>
      <router-link :to="{ name: 'admin-group-role-mapping' }">Group → Role Mapping</router-link>
      <router-link :to="{ name: 'admin-roles-permissions' }">Roles & Permissions</router-link>
      <router-link :to="{ name: 'admin-application-mapping' }">Application ↔ Safe Mapping</router-link>
      <router-link :to="{ name: 'admin-secrets-store' }">Secrets Store Configuration</router-link>
      <router-link :to="{ name: 'admin-field-metadata' }">Field Metadata Management</router-link>
      <router-link :to="{ name: 'admin-audit-log' }">Audit Log Viewer</router-link>
      <router-link :to="{ name: 'admin-configuration' }">Global Application Configuration</router-link>
    </nav>
    <router-view />
  </div>
</template>

<style scoped>
.admin-layout {
  display: flex;
  gap: 2rem;
}
.admin-subnav {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  min-width: 220px;
}
</style>
