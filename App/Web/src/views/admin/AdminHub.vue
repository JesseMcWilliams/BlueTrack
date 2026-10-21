<script setup>
// Admin hub with sub-navigation (D-47): one top-nav entry, sections shown
// per the signed-in user's permissions -- mirrors the API's own real
// [Authorize(Policy = ...)] gates on each admin controller, not a
// separately-invented list.
import { computed, onMounted, ref } from 'vue'
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
  { name: 'admin-data-sources', labelKey: 'admin.adminHub.dataSources', permission: 'ManageDataSources' },
  { name: 'admin-account-classification', labelKey: 'admin.adminHub.accountClassification', permission: 'ManageDiscoveredAccounts' },
  { name: 'admin-siem-forwarding', labelKey: 'admin.adminHub.siemForwarding', permission: 'ManageApplicationConfiguration' },
  { name: 'admin-platform-mapping', labelKey: 'admin.adminHub.platformMapping', permission: 'CuratePlatformMapping' },
  { name: 'admin-api-keys', labelKey: 'admin.adminHub.apiKeys', permission: 'ManageApiKeys' },
  { name: 'admin-progress-rules', labelKey: 'admin.adminHub.progressRules', permission: 'ManageProgressRules' },   // D-283
  { name: 'admin-blueprint-stages', labelKey: 'admin.adminHub.blueprintStages', permission: 'ManageProgressRules' }   // D-284
]

const visibleSections = computed(() => sections.filter(s => rights.hasPermission(s.permission)))

// D-250: a marker on Data Sources while the latest Self-Hosted Direct pull is failed or rejected, since the nightly
// import then fails too. Only asked of people who can open that page, and a failed request just shows no marker.
const pullProblem = ref(false)
onMounted(async () => {
  try {
    await rights.ensureLoaded()
    if (!rights.hasPermission('ManageDataSources')) return
    const response = await fetch('/api/admin/cyberark-sources/direct/status')
    if (!response.ok) return
    const status = await response.json()
    pullProblem.value = !!status.enabled && (status.latestStatus === 'Failed' || status.latestStatus === 'Rejected')
  } catch {
    /* no marker */
  }
})
</script>

<template>
  <div class="admin-layout">
    <nav class="admin-subnav">
      <router-link v-for="s in visibleSections" :key="s.name" :to="{ name: s.name }">{{ t(s.labelKey) }}<span v-if="s.name === 'admin-data-sources' && pullProblem" class="nav-warning" role="img" :aria-label="t('admin.adminHub.pullProblem')" :title="t('admin.adminHub.pullProblem')">{{ ' ⚠' }}</span></router-link>
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
