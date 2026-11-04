<script setup>
// Admin hub with sub-navigation (D-47): one top-nav entry, sections shown
// per the signed-in user's permissions -- mirrors the API's own real
// [Authorize(Policy = ...)] gates on each admin controller, not a
// separately-invented list.
import { computed, onMounted, ref } from 'vue'
import { useRightsStore } from '../../stores/rights'
import { useI18n } from 'vue-i18n'
import { useSubnavCurrent } from '../../composables/useSubnavCurrent'

const { t } = useI18n()

const rights = useRightsStore()
const isCurrent = useSubnavCurrent()

// D-330: the 23 sections in five groups, in this order; each group's sections keep the order below.
const GROUPS = [
  { key: 'access', labelKey: 'admin.adminHub.groups.access' },
  { key: 'data', labelKey: 'admin.adminHub.groups.data' },
  { key: 'rules', labelKey: 'admin.adminHub.groups.rules' },
  { key: 'integrations', labelKey: 'admin.adminHub.groups.integrations' },
  { key: 'system', labelKey: 'admin.adminHub.groups.system' }
]

const sections = [
  { name: 'admin-identity-providers', labelKey: 'admin.adminHub.identityProviders', permission: 'ManageIdentityProviders', group: 'access' },
  { name: 'admin-group-role-mapping', labelKey: 'admin.adminHub.groupRoleMapping', permission: 'ManageGroupRoleMapping', group: 'access' },
  { name: 'admin-roles-permissions', labelKey: 'admin.adminHub.rolesPermissions', permission: 'ManageRolesAndPermissions', group: 'access' },
  { name: 'admin-credentials', labelKey: 'admin.adminHub.credentials', permission: 'ManageCredentials', group: 'access' },
  { name: 'admin-api-keys', labelKey: 'admin.adminHub.apiKeys', permission: 'ManageApiKeys', group: 'access' },
  { name: 'admin-data-sources', labelKey: 'admin.adminHub.dataSources', permission: 'ManageDataSources', group: 'data' },
  { name: 'admin-application-mapping', labelKey: 'admin.adminHub.applicationMapping', permission: 'CurateApplicationMapping', group: 'data' },
  { name: 'admin-platform-mapping', labelKey: 'admin.adminHub.platformMapping', permission: 'CuratePlatformMapping', group: 'data' },
  { name: 'admin-target-match-review', labelKey: 'admin.adminHub.targetMatchReview', permission: 'ManageTargets', group: 'data' },
  { name: 'admin-import-mapping-profiles', labelKey: 'admin.adminHub.importMappingProfiles', permission: 'ManageTargets', group: 'data' },
  { name: 'admin-account-classification', labelKey: 'admin.adminHub.accountClassification', permission: 'ManageDiscoveredAccounts', group: 'data' },
  { name: 'admin-field-metadata', labelKey: 'admin.adminHub.fieldMetadata', permission: 'ManageFieldMetadata', group: 'data' },
  { name: 'admin-progress-rules', labelKey: 'admin.adminHub.progressRules', permission: 'ManageProgressRules', group: 'rules' },   // D-283
  { name: 'admin-blueprint-stages', labelKey: 'admin.adminHub.blueprintStages', permission: 'ManageProgressRules', group: 'rules' },   // D-284
  { name: 'admin-safe-access-policies', labelKey: 'admin.adminHub.safeAccessPolicies', permission: 'ManageSafeAccessPolicies', group: 'rules' },   // D-300
  { name: 'admin-risk-score-bands', labelKey: 'admin.adminHub.riskScoreBands', permission: 'ManageRiskScoreBands', group: 'rules' },
  { name: 'admin-secrets-store', labelKey: 'admin.adminHub.secretsStore', permission: 'ManageSecretsStore', group: 'integrations' },
  { name: 'admin-notifications', labelKey: 'admin.adminHub.notifications', permission: 'ManageNotifications', group: 'integrations' },
  { name: 'admin-siem-forwarding', labelKey: 'admin.adminHub.siemForwarding', permission: 'ManageApplicationConfiguration', group: 'integrations' },
  { name: 'admin-power-bi', labelKey: 'admin.adminHub.powerBi', permission: 'ViewDeploymentInfo', group: 'integrations' },   // D-314
  { name: 'admin-configuration', labelKey: 'admin.adminHub.configuration', permission: 'ManageApplicationConfiguration', group: 'system' },
  { name: 'admin-audit-log', labelKey: 'admin.adminHub.auditLog', permission: 'ViewAuditLog', group: 'system' },
  { name: 'admin-deployment', labelKey: 'admin.adminHub.deployment', permission: 'ViewDeploymentInfo', group: 'system' }
]

const visibleSections = computed(() => sections.filter(s => rights.hasPermission(s.permission)))
const visibleGroups = computed(() => GROUPS
  .map(g => ({ ...g, sections: visibleSections.value.filter(s => s.group === g.key) }))
  .filter(g => g.sections.length > 0))

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
    <nav class="admin-subnav hub-subnav">
      <div v-for="g in visibleGroups" :key="g.key" class="hub-subnav-group" role="group" :aria-labelledby="`admin-group-${g.key}`">
        <span :id="`admin-group-${g.key}`" class="hub-subnav-group-title">{{ t(g.labelKey) }}</span>
        <router-link v-for="s in g.sections" :key="s.name" :to="{ name: s.name }" :class="{ 'hub-subnav-current': isCurrent(s.name) }">{{ t(s.labelKey) }}<span v-if="s.name === 'admin-data-sources' && pullProblem" class="nav-warning" role="img" :aria-label="t('admin.adminHub.pullProblem')" :title="t('admin.adminHub.pullProblem')">{{ ' ⚠' }}</span></router-link>
      </div>
      <p v-if="rights.loaded && visibleSections.length === 0">{{ t('admin.adminHub.noSections') }}</p>
    </nav>
    <div class="hub-content">
      <router-view />
    </div>
  </div>
</template>

<style scoped>
.admin-layout {
  display: flex;
  gap: 2rem;
}
.admin-subnav {
  min-width: 220px;
}
/* No overflow here, as in ReportsHub: a scroll container would stop the tables' sticky headers following the page. */
.hub-content {
  flex: 1;
  min-width: 0;
}
/* D-330: on a narrow screen the sections go above the page, not beside it. */
@media (max-width: 900px) {
  .admin-layout {
    flex-direction: column;
    gap: var(--space-4);
  }
}
</style>
