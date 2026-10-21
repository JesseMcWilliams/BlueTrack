// UI review step 4b: which permission a routed page needs, so the router can send a signed-in user to Access Denied,
// or to the no-access landing page when they hold no permissions at all. Presentation only: the API's
// [Authorize(Policy = ...)] gates remain the enforcement point. The list mirrors AdminHub.vue's sections and
// ReportsHub.vue's reports (which hide the links); a page not named here needs no permission of its own.
export const ADMIN_SECTIONS = [
  { list: 'admin-identity-providers', permission: 'ManageIdentityProviders', routes: ['admin-identity-provider-create', 'admin-identity-provider-edit'] },
  { list: 'admin-group-role-mapping', permission: 'ManageGroupRoleMapping', routes: ['admin-group-role-mapping-create'] },
  { list: 'admin-roles-permissions', permission: 'ManageRolesAndPermissions', routes: ['admin-role-create', 'admin-role-edit'] },
  { list: 'admin-application-mapping', permission: 'CurateApplicationMapping', routes: ['admin-application-create', 'admin-application-mapping-bulk-import', 'admin-application-edit'] },
  { list: 'admin-secrets-store', permission: 'ManageSecretsStore', routes: [] },
  { list: 'admin-field-metadata', permission: 'ManageFieldMetadata', routes: ['admin-field-metadata-create', 'admin-field-metadata-edit'] },
  { list: 'admin-audit-log', permission: 'ViewAuditLog', routes: [] },
  { list: 'admin-configuration', permission: 'ManageApplicationConfiguration', routes: [] },
  { list: 'admin-deployment', permission: 'ViewDeploymentInfo', routes: [] },
  { list: 'admin-notifications', permission: 'ManageNotifications', routes: ['admin-notification-recipient-create'] },
  { list: 'admin-credentials', permission: 'ManageCredentials', routes: [] },
  { list: 'admin-target-match-review', permission: 'ManageTargets', routes: [] },
  { list: 'admin-import-mapping-profiles', permission: 'ManageTargets', routes: ['admin-import-mapping-profile-create', 'admin-import-mapping-profile-edit'] },
  { list: 'admin-risk-score-bands', permission: 'ManageRiskScoreBands', routes: ['admin-risk-score-band-create', 'admin-risk-score-band-edit'] },
  { list: 'admin-data-sources', permission: 'ManageDataSources', routes: ['admin-cyberark-sources-edit', 'admin-data-feed-create', 'admin-data-feed-edit'] },
  { list: 'admin-account-classification', permission: 'ManageDiscoveredAccounts', routes: ['admin-account-classification-rule-create', 'admin-account-classification-rule-edit'] },
  { list: 'admin-siem-forwarding', permission: 'ManageApplicationConfiguration', routes: [] },
  { list: 'admin-platform-mapping', permission: 'CuratePlatformMapping', routes: [] },
  { list: 'admin-api-keys', permission: 'ManageApiKeys', routes: ['admin-api-key-create', 'admin-api-key-edit'] },
  { list: 'admin-progress-rules', permission: 'ManageProgressRules', routes: ['admin-progress-rule-create', 'admin-progress-rule-edit'] },   // D-283
  { list: 'admin-blueprint-stages', permission: 'ManageProgressRules', routes: [] }   // D-284
]

const OTHER = {
  targets: 'ManageTargets',
  'target-create': 'ManageTargets',
  'target-edit': 'ManageTargets',
  'targets-bulk-import': 'ManageTargets',
  'access-groups': 'ManageAccessGroups',
  'access-group-create': 'ManageAccessGroups',
  'access-group-edit': 'ManageAccessGroups',
  'access-groups-bulk-import': 'ManageAccessGroups',
  'reports-reconciliation-review': 'ConfirmReconciliation',
  'reports-possible-matches': 'ConfirmReconciliation',
  'reports-risk-score': 'ViewRiskReport',
  'reports-risk-exception-sod': 'ViewRiskExceptionSodReport',
  'reports-discovered-accounts': 'ViewDiscoveredAccounts'
}

export const ROUTE_PERMISSIONS = { ...OTHER }
for (const section of ADMIN_SECTIONS) {
  for (const name of [section.list, ...section.routes]) ROUTE_PERMISSIONS[name] = section.permission
}

// Pages a user with no permissions may still open: signing in, the profile (it shows their roles), and the two
// pages that explain.
const OPEN_TO_ANYONE = ['login', 'login-provider', 'my-profile', 'access-denied', 'no-access']

/**
 * Where the router should send a signed-in user instead of `to`, or null to let them through.
 * `rights` is the loaded rights store; `to` is the target route (with redirectedFrom when it was a redirect; the Admin entry's has no route name, so its path is used).
 */
export function accessRedirect(rights, to) {
  if (!rights.loaded || rights.error || rights.authenticated !== true) return null
  const name = to.name
  if (rights.permissionNames.length === 0) {
    return OPEN_TO_ANYONE.includes(name) ? null : { name: 'no-access' }
  }
  if (name === 'no-access') return { name: 'dashboard' }
  const needed = ROUTE_PERMISSIONS[name]
  if (!needed || rights.hasPermission(needed)) return null
  // The Admin entry opens the first section; if that isn't theirs, open the first one that is.
  if (/^\/admin\/?$/.test(to.redirectedFrom?.path ?? '')) {
    const first = ADMIN_SECTIONS.find(s => rights.hasPermission(s.permission))
    if (first) return { name: first.list }
  }
  return { name: 'access-denied' }
}
