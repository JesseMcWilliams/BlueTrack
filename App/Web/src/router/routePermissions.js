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
  { list: 'admin-power-bi', permission: 'ViewDeploymentInfo', routes: [] },   // D-314
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
  { list: 'admin-blueprint-stages', permission: 'ManageProgressRules', routes: [] },   // D-284
  { list: 'admin-safe-access-policies', permission: 'ManageSafeAccessPolicies', routes: ['admin-safe-access-policy-create', 'admin-safe-access-policy-edit', 'admin-safe-role-create', 'admin-safe-role-edit'] }   // D-300
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
  'reports-discovered-accounts': 'ViewDiscoveredAccounts',
  'reports-dna-accounts': 'ViewDiscoveredAccounts',   // D-325
  // UI review step 4 (D-304): the pages of tracked data, which showed "Request failed: 403" to a signed-in user
  // without the permission their data needs (the API's gate on each page's first request).
  dashboard: 'ViewDashboard',
  'account-progress-list': 'ViewDashboard',
  'account-progress-detail': 'ViewDashboard',
  'safe-detail': 'ViewDashboard',   // D-308
  'account-progress-bulk-edit': 'EditAccountProgress',
  'risk-exceptions-list': 'ViewDashboard',
  'risk-exceptions-overdue-worklist': 'ViewDashboard',
  'risk-exception-edit': 'ViewDashboard',
  'risk-exception-create': 'ApproveExceptions',
  'risk-exceptions-bulk-import': 'ApproveExceptions',
  'risk-exceptions-approval-worklist': 'ApproveExceptions',
  'reports-cpm-health': 'ViewDashboard',
  'reports-cyberark-applications': 'ViewDashboard',
  'reports-decom-accounts': 'ViewDashboard',
  'reports-decom-safes': 'ViewDashboard',
  'reports-kpi-summary': 'ViewDashboard',
  'reports-overdue-worklist': 'ViewDashboard',
  'reports-safe-access': 'ViewDashboard',
  'reports-stage-status-summary': 'ViewDashboard',
  'reports-trends': 'ViewDashboard',
  'reports-unresolved-entitlement-members': 'ViewDashboard'
}

export const ROUTE_PERMISSIONS = { ...OTHER }
for (const section of ADMIN_SECTIONS) {
  for (const name of [section.list, ...section.routes]) ROUTE_PERMISSIONS[name] = section.permission
}

// The reports in ReportsHub.vue's order, so the Reports entry can open the first one the user may see.
export const REPORT_PAGES = [
  'reports-overdue-worklist', 'reports-stage-status-summary', 'reports-reconciliation-review', 'reports-possible-matches',
  'reports-unresolved-entitlement-members', 'reports-cpm-health', 'reports-cyberark-applications', 'reports-risk-score',
  'reports-risk-exception-sod', 'reports-discovered-accounts', 'reports-dna-accounts', 'reports-decom-safes', 'reports-decom-accounts',
  'reports-kpi-summary', 'reports-trends', 'reports-safe-access'
]

export function canOpen(rights, name) {
  const needed = ROUTE_PERMISSIONS[name]
  return !needed || rights.hasPermission(needed)
}

/** Whether the Reports entry has a report for this user. */
export function canOpenReports(rights) {
  return REPORT_PAGES.some(name => canOpen(rights, name))
}

/** Whether the Admin entry has a section for this user. */
export function canOpenAdmin(rights) {
  return ADMIN_SECTIONS.some(s => rights.hasPermission(s.permission))
}

// Pages a user with no permissions may still open: signing in, the profile (it shows their roles), and the two
// pages that explain.
const OPEN_TO_ANYONE = ['login', 'login-provider', 'my-profile', 'access-denied', 'no-access']

/**
 * Where the router should send a signed-in user instead of `to`, or null to let them through.
 * `rights` is the loaded rights store; `to` is the target route (with redirectedFrom when it was a redirect; the Admin and Reports entries' have no route name, so their path is used).
 */
export function accessRedirect(rights, to) {
  if (!rights.loaded || rights.error || rights.authenticated !== true) return null
  const name = to.name
  if (rights.permissionNames.length === 0) {
    return OPEN_TO_ANYONE.includes(name) ? null : { name: 'no-access' }
  }
  // D-307: Home is where everyone with a role starts (D-305 sent them to their first page instead).
  if (name === 'no-access') return { name: 'home' }
  if (canOpen(rights, name)) return null
  // The Admin and Reports entries open their first page; if that isn't theirs, open the first one that is.
  const from = to.redirectedFrom?.path ?? ''
  if (/^\/admin\/?$/.test(from)) {
    const first = ADMIN_SECTIONS.find(s => rights.hasPermission(s.permission))
    if (first) return { name: first.list }
  }
  if (/^\/reports\/?$/.test(from)) {
    const first = REPORT_PAGES.find(n => canOpen(rights, n))
    if (first) return { name: first }
  }
  return { name: 'access-denied' }
}
