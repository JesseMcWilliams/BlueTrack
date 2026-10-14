<script setup>
// Admin hub with sub-navigation (D-47): one top-nav entry, sections shown
// per the signed-in user's permissions -- mirrors the API's own real
// [Authorize(Policy = ...)] gates on each admin controller, not a
// separately-invented list.
import { computed } from 'vue'
import { useRightsStore } from '../../stores/rights'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rights = useRightsStore()

const sections = [
  { name: 'admin-identity-providers', labelKey: 'admin.adminHub.identityProviders', permission: 'ManageIdentityProviders' },
  { name: 'admin-group-role-mapping', labelKey: 'admin.adminHub.groupRoleMapping', permission: 'ManageGroupRoleMapping' },
  { name: 'admin-roles-permissions', labelKey: 'admin.adminHub.rolesPermissions', permission: 'ManageRolesAndPermissions' },
  { name: 'admin-application-mapping', labelKey: 'admin.adminHub.applicationMapping', permission: 'CurateApplicationMapping' },
  { name: 'admin-secrets-store', labelKey: 'admin.adminHub.secretsStore', permission: 'ManageSecretsStore' },
  { name: 'admin-field-metadata', labelKey: 'admin.adminHub.fieldMetadata', permission: 'ManageFieldMetadata' },
  { name: 'admin-audit-log', labelKey: 'admin.adminHub.auditLog', permission: 'ViewAuditLog' },
  { name: 'admin-configuration', labelKey: 'admin.adminHub.configuration', permission: 'ManageApplicationConfiguration' },
  { name: 'admin-deployment', labelKey: 'admin.adminHub.deployment', permission: 'ViewDeploymentInfo' },
  { name: 'admin-notifications', labelKey: 'admin.adminHub.notifications', permission: 'ManageNotifications' },
  { name: 'admin-credentials', labelKey: 'admin.adminHub.credentials', permission: 'ManageCredentials' },
  { name: 'admin-target-match-review', labelKey: 'admin.adminHub.targetMatchReview', permission: 'ManageTargets' },
  { name: 'admin-import-mapping-profiles', labelKey: 'admin.adminHub.importMappingProfiles', permission: 'ManageTargets' },
  { name: 'admin-risk-score-bands', labelKey: 'admin.adminHub.riskScoreBands', permission: 'ManageRiskScoreBands' },
  { name: 'admin-data-sources', labelKey: 'admin.adminHub.dataSources', permission: 'ManageDataSources' }
]

const visibleSections = computed(() => sections.filter(s => rights.hasPermission(s.permission)))
</script>

<template>
  <div class="admin-layout">
    <nav class="admin-subnav">
      <router-link v-for="s in visibleSections" :key="s.name" :to="{ name: s.name }">{{ t(s.labelKey) }}</router-link>
      <p v-if="rights.loaded && visibleSections.length === 0">{{ t('admin.adminHub.noSections') }}</p>
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
