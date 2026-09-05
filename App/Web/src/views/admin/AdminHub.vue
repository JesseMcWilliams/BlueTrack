<script setup>
// Admin hub with sub-navigation (D-47): one top-nav entry, sections shown
// per the signed-in user's permissions -- mirrors the API's own real
// [Authorize(Policy = ...)] gates on each admin controller, not a
// separately-invented list.
import { computed } from 'vue'
import { useRightsStore } from '../../stores/rights'

const rights = useRightsStore()

const sections = [
  { name: 'admin-identity-providers', label: 'Identity Providers', permission: 'ManageIdentityProviders' },
  { name: 'admin-group-role-mapping', label: 'Group → Role Mapping', permission: 'ManageGroupRoleMapping' },
  { name: 'admin-roles-permissions', label: 'Roles & Permissions', permission: 'ManageRolesAndPermissions' },
  { name: 'admin-application-mapping', label: 'Application ↔ Safe Mapping', permission: 'CurateApplicationMapping' },
  { name: 'admin-secrets-store', label: 'Secrets Store Configuration', permission: 'ManageSecretsStore' },
  { name: 'admin-field-metadata', label: 'Field Metadata Management', permission: 'ManageFieldMetadata' },
  { name: 'admin-audit-log', label: 'Audit Log Viewer', permission: 'ViewAuditLog' },
  { name: 'admin-configuration', label: 'Global Application Configuration', permission: 'ManageApplicationConfiguration' },
  { name: 'admin-deployment', label: 'Deployment', permission: 'ViewDeploymentInfo' }
]

const visibleSections = computed(() => sections.filter(s => rights.hasPermission(s.permission)))
</script>

<template>
  <div class="admin-layout">
    <nav class="admin-subnav">
      <router-link v-for="s in visibleSections" :key="s.name" :to="{ name: s.name }">{{ s.label }}</router-link>
      <p v-if="rights.loaded && visibleSections.length === 0">No admin sections available -- you don't hold any admin permission.</p>
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
