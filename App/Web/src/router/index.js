import { createRouter, createWebHistory } from 'vue-router'
import { nextTick } from 'vue'
import { useRightsStore } from '../stores/rights'

// Routes mirror the confirmed page inventory (Design_Application-Structure.md,
// D-43), with Admin (D-47) and Reports (D-56) as hub pages with sub-navigation
// rather than flat top-level entries.
//
// Every route (except 'dashboard', the implicit root) carries meta.breadcrumb
// (the i18n key of its crumb label, D-227: 'shell.routes.<routeName>' in
// src/locales/en/shell.json) and, where its logical parent isn't
// 'dashboard', meta.breadcrumbParent (the parent route's name). Breadcrumbs.vue
// walks this meta chain to build the trail -- deliberately NOT Vue Router's
// own route.matched, which only reflects actual nested-route parentage.
// That's correct for Reports/Admin's hub+children structure below, but
// account-progress-list/-detail and risk-exceptions-list/-edit are flat
// sibling routes (a detail page's dynamic segment is a sibling of its list
// page, not nested under it), so route.matched for a detail page never
// included its list page at all -- found 2026-09-06 ("breadcrumb isn't
// showing Accounts... multiple pages have this behaviour"). The same
// route.matched approach also duplicated 'Dashboard' on the dashboard page
// itself, since route.matched there is exactly [dashboard], appended after
// Breadcrumbs.vue's own hardcoded "Dashboard" root link.
const routes = [
  {
    path: '/login',
    name: 'login',
    meta: { breadcrumb: 'shell.routes.login' },
    component: () => import('../views/Login.vue')
  },
  // D-205: a direct sign-in address per method (an IdP's launch URL, or a
  // link an admin hands out); overrides the default sign-in method.
  {
    path: '/login/:provider(windows|oidc|saml)',
    name: 'login-provider',
    meta: { breadcrumb: 'shell.routes.loginProvider' },
    component: () => import('../views/Login.vue')
  },
  {
    path: '/',
    name: 'dashboard',
    component: () => import('../views/Dashboard.vue')
  },
  {
    path: '/accounts',
    name: 'account-progress-list',
    meta: { breadcrumb: 'shell.routes.accountProgressList' },
    component: () => import('../views/AccountProgressList.vue')
  },
  // D-182: bulk edit of the accounts selected on the list.
  {
    path: '/accounts/bulk-edit',
    name: 'account-progress-bulk-edit',
    meta: { breadcrumb: 'shell.routes.accountProgressBulkEdit', breadcrumbParent: 'account-progress-list' },
    component: () => import('../views/AccountProgressBulkEdit.vue')
  },
  {
    path: '/accounts/:accountKey',
    name: 'account-progress-detail',
    meta: { breadcrumb: 'shell.routes.accountProgressDetail', breadcrumbParent: 'account-progress-list' },
    component: () => import('../views/AccountProgressDetail.vue'),
    props: true
  },
  {
    path: '/exceptions',
    name: 'risk-exceptions-list',
    meta: { breadcrumb: 'shell.routes.riskExceptionsList' },
    component: () => import('../views/RiskExceptionsList.vue')
  },
  // D-183: import exceptions approved in another tool.
  {
    path: '/exceptions/bulk-import',
    name: 'risk-exceptions-bulk-import',
    meta: { breadcrumb: 'shell.routes.riskExceptionsBulkImport', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionsBulkImport.vue')
  },
  {
    path: '/exceptions/new',
    name: 'risk-exception-create',
    meta: { breadcrumb: 'shell.routes.riskExceptionCreate', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionEdit.vue')
  },
  {
    path: '/exceptions/:exceptionKey',
    name: 'risk-exception-edit',
    meta: { breadcrumb: 'shell.routes.riskExceptionEdit', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionEdit.vue'),
    props: true
  },
  {
    path: '/exceptions/approvals',
    name: 'risk-exceptions-approval-worklist',
    meta: { breadcrumb: 'shell.routes.riskExceptionsApprovalWorklist', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionsApprovalWorklist.vue')
  },
  {
    path: '/exceptions/overdue',
    name: 'risk-exceptions-overdue-worklist',
    meta: { breadcrumb: 'shell.routes.riskExceptionsOverdueWorklist', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionsOverdueWorklist.vue')
  },
  // Reports hub with sub-navigation (D-56) -- three confirmed report types.
  {
    path: '/reports',
    name: 'reports',
    meta: { breadcrumb: 'shell.routes.reports' },
    component: () => import('../views/reports/ReportsHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'reports-overdue-worklist' }
      },
      {
        path: 'overdue',
        name: 'reports-overdue-worklist',
        meta: { breadcrumb: 'shell.routes.reportsOverdueWorklist', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/OverdueAtRiskWorklist.vue')
      },
      {
        path: 'stage-status-summary',
        name: 'reports-stage-status-summary',
        meta: { breadcrumb: 'shell.routes.reportsStageStatusSummary', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/StageStatusFunnelSummary.vue')
      },
      {
        path: 'reconciliation-review',
        name: 'reports-reconciliation-review',
        meta: { breadcrumb: 'shell.routes.reportsReconciliationReview', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/ReconciliationReviewQueue.vue')
      },
      // D-212 (Data Sources phase 4c): feed accounts that may be CyberArk accounts.
      {
        path: 'possible-matches',
        name: 'reports-possible-matches',
        meta: { breadcrumb: 'shell.routes.reportsPossibleMatches', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/PossibleAccountMatches.vue')
      },
      // D-186: safes and accounts flagged for deletion by the name patterns.
      {
        path: 'decom-safes',
        name: 'reports-decom-safes',
        meta: { breadcrumb: 'shell.routes.reportsDecomSafes', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/DecomSafes.vue')
      },
      {
        path: 'decom-accounts',
        name: 'reports-decom-accounts',
        meta: { breadcrumb: 'shell.routes.reportsDecomAccounts', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/DecomAccounts.vue')
      },
      {
        path: 'unresolved-entitlement-members',
        name: 'reports-unresolved-entitlement-members',
        meta: { breadcrumb: 'shell.routes.reportsUnresolvedEntitlementMembers', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/UnresolvedEntitlementMembers.vue')
      },
      {
        path: 'risk-score',
        name: 'reports-risk-score',
        meta: { breadcrumb: 'shell.routes.reportsRiskScore', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/RiskScoreReport.vue')
      },
      {
        path: 'risk-exception-sod',
        name: 'reports-risk-exception-sod',
        meta: { breadcrumb: 'shell.routes.reportsRiskExceptionSod', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/RiskExceptionSodReport.vue')
      },
      {
        path: 'discovered-accounts',
        name: 'reports-discovered-accounts',
        meta: { breadcrumb: 'shell.routes.reportsDiscoveredAccounts', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/DiscoveredAccounts.vue')
      },
      {
        path: 'kpi-summary',
        name: 'reports-kpi-summary',
        meta: { breadcrumb: 'shell.routes.reportsKpiSummary', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/KpiSummary.vue')
      },
      // D-221: stage, % complete and risk level over time, from the daily snapshot.
      {
        path: 'trends',
        name: 'reports-trends',
        meta: { breadcrumb: 'shell.routes.reportsTrends', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/Trends.vue')
      }
    ]
  },
  {
    path: '/profile',
    name: 'my-profile',
    meta: { breadcrumb: 'shell.routes.myProfile' },
    component: () => import('../views/MyProfile.vue')
  },
  // D-121: promoted out of the Admin hub to their own top-level nav entries
  // (Analyst now holds ManageTargets/ManageAccessGroups too, same as
  // Admin) -- flat top-level routes, no breadcrumbParent, matching the
  // naming convention of other top-level routes like account-progress-list.
  {
    path: '/targets',
    name: 'targets',
    meta: { breadcrumb: 'shell.routes.targets' },
    component: () => import('../views/Targets.vue')
  },
  // D-124 Phase 4: Add/Edit moved off Targets.vue's own inline form onto
  // its own routed page, mirroring risk-exception-create/-edit's shape.
  {
    path: '/targets/new',
    name: 'target-create',
    meta: { breadcrumb: 'shell.routes.targetCreate', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetEdit.vue')
  },
  {
    path: '/targets/:targetKey',
    name: 'target-edit',
    meta: { breadcrumb: 'shell.routes.targetEdit', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetEdit.vue'),
    props: true
  },
  // D-124 Phase 5: the 2 always-visible inline "Bulk Import" sections moved
  // off Targets.vue onto this dedicated routed page, reached via a single
  // "Bulk Actions" header link -- consistent with Phase 4's Add/Edit move.
  {
    path: '/targets/bulk-import',
    name: 'targets-bulk-import',
    meta: { breadcrumb: 'shell.routes.targetsBulkImport', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetsBulkImport.vue')
  },
  {
    path: '/access-groups',
    name: 'access-groups',
    meta: { breadcrumb: 'shell.routes.accessGroups' },
    component: () => import('../views/AccessGroups.vue')
  },
  // D-124 Phase 4: Add/Edit moved off AccessGroups.vue's own inline form
  // onto its own routed page, mirroring risk-exception-create/-edit's shape.
  {
    path: '/access-groups/new',
    name: 'access-group-create',
    meta: { breadcrumb: 'shell.routes.accessGroupCreate', breadcrumbParent: 'access-groups' },
    component: () => import('../views/AccessGroupEdit.vue')
  },
  {
    path: '/access-groups/:accessGroupKey',
    name: 'access-group-edit',
    meta: { breadcrumb: 'shell.routes.accessGroupEdit', breadcrumbParent: 'access-groups' },
    component: () => import('../views/AccessGroupEdit.vue'),
    props: true
  },
  // D-124 Phase 5: the 3 always-visible inline "Bulk Import" sections moved
  // off AccessGroups.vue onto this dedicated routed page, reached via a
  // single "Bulk Actions" header link -- consistent with Phase 4's Add/Edit
  // move.
  {
    path: '/access-groups/bulk-import',
    name: 'access-groups-bulk-import',
    meta: { breadcrumb: 'shell.routes.accessGroupsBulkImport', breadcrumbParent: 'access-groups' },
    component: () => import('../views/AccessGroupsBulkImport.vue')
  },
  // Admin hub with sub-navigation (D-47) -- one top-nav entry, sections
  // inside gated per-permission at render time, not per-route here.
  {
    path: '/admin',
    name: 'admin',
    meta: { breadcrumb: 'shell.routes.admin' },
    component: () => import('../views/admin/AdminHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'admin-identity-providers' }
      },
      {
        path: 'identity-providers',
        name: 'admin-identity-providers',
        meta: { breadcrumb: 'shell.routes.adminIdentityProviders', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviders.vue')
      },
      // D-124 Phase 4: Add/Edit moved off IdentityProviders.vue's own
      // inline form onto its own routed page -- breadcrumbParent stays
      // 'admin' (flat), matching every other admin child route's own
      // convention rather than pointing at the specific list page.
      {
        path: 'identity-providers/new',
        name: 'admin-identity-provider-create',
        meta: { breadcrumb: 'shell.routes.adminIdentityProviderCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviderEdit.vue')
      },
      {
        path: 'identity-providers/:providerKey',
        name: 'admin-identity-provider-edit',
        meta: { breadcrumb: 'shell.routes.adminIdentityProviderEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviderEdit.vue'),
        props: true
      },
      {
        path: 'group-role-mapping',
        name: 'admin-group-role-mapping',
        meta: { breadcrumb: 'shell.routes.adminGroupRoleMapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GroupRoleMapping.vue')
      },
      // D-124 Phase 4 (partial conversion): only "Add Mapping" moves -- no
      // matching Edit route exists on the underlying page today.
      {
        path: 'group-role-mapping/new',
        name: 'admin-group-role-mapping-create',
        meta: { breadcrumb: 'shell.routes.adminGroupRoleMappingCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GroupRoleMappingCreate.vue')
      },
      {
        path: 'roles-permissions',
        name: 'admin-roles-permissions',
        meta: { breadcrumb: 'shell.routes.adminRolesPermissions', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RolesAndPermissions.vue')
      },
      // D-124 Phase 4: Add/Edit moved off RolesAndPermissions.vue's own
      // inline form onto its own routed page.
      {
        path: 'roles-permissions/new',
        name: 'admin-role-create',
        meta: { breadcrumb: 'shell.routes.adminRoleCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RoleEdit.vue')
      },
      {
        path: 'roles-permissions/:appRoleKey',
        name: 'admin-role-edit',
        meta: { breadcrumb: 'shell.routes.adminRoleEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RoleEdit.vue'),
        props: true
      },
      {
        path: 'application-mapping',
        name: 'admin-application-mapping',
        meta: { breadcrumb: 'shell.routes.adminApplicationMapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationSafeMapping.vue')
      },
      // D-124 Phase 4: Add/Edit moved off ApplicationSafeMapping.vue's
      // Applications section onto its own routed page -- the Safes
      // section (a plain per-row <select>, not a form) is untouched.
      {
        path: 'application-mapping/new',
        name: 'admin-application-create',
        meta: { breadcrumb: 'shell.routes.adminApplicationCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationEdit.vue')
      },
      // D-180: the two CSV imports (Applications; Safe -> Application
      // assignments), reached via the page's "Bulk Actions" button like
      // Targets/Access Groups (D-124 Phase 5). Listed before the
      // :applicationKey route so 'bulk-import' is never read as a key.
      {
        path: 'application-mapping/bulk-import',
        name: 'admin-application-mapping-bulk-import',
        meta: { breadcrumb: 'shell.routes.adminApplicationMappingBulkImport', breadcrumbParent: 'admin-application-mapping' },
        component: () => import('../views/admin/ApplicationSafeMappingBulkImport.vue')
      },
      {
        path: 'application-mapping/:applicationKey',
        name: 'admin-application-edit',
        meta: { breadcrumb: 'shell.routes.adminApplicationEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationEdit.vue'),
        props: true
      },
      {
        path: 'secrets-store',
        name: 'admin-secrets-store',
        meta: { breadcrumb: 'shell.routes.adminSecretsStore', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/SecretsStoreConfiguration.vue')
      },
      {
        path: 'field-metadata',
        name: 'admin-field-metadata',
        meta: { breadcrumb: 'shell.routes.adminFieldMetadata', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataManagement.vue')
      },
      // D-124 Phase 4: Add/Edit moved off FieldMetadataManagement.vue's own
      // inline form onto its own routed page.
      {
        path: 'field-metadata/new',
        name: 'admin-field-metadata-create',
        meta: { breadcrumb: 'shell.routes.adminFieldMetadataCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataEdit.vue')
      },
      {
        path: 'field-metadata/:fieldMetadataKey',
        name: 'admin-field-metadata-edit',
        meta: { breadcrumb: 'shell.routes.adminFieldMetadataEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataEdit.vue'),
        props: true
      },
      {
        path: 'audit-log',
        name: 'admin-audit-log',
        meta: { breadcrumb: 'shell.routes.adminAuditLog', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/AuditLogViewer.vue')
      },
      {
        path: 'configuration',
        name: 'admin-configuration',
        meta: { breadcrumb: 'shell.routes.adminConfiguration', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GlobalApplicationConfiguration.vue')
      },
      {
        path: 'deployment',
        name: 'admin-deployment',
        meta: { breadcrumb: 'shell.routes.adminDeployment', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/DeploymentInfo.vue')
      },
      {
        path: 'notifications',
        name: 'admin-notifications',
        meta: { breadcrumb: 'shell.routes.adminNotifications', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/Notifications.vue')
      },
      // D-124 Phase 4 (partial conversion): only "Add Recipient" moves --
      // no matching Edit route exists on the underlying page today.
      {
        path: 'notifications/recipients/new',
        name: 'admin-notification-recipient-create',
        meta: { breadcrumb: 'shell.routes.adminNotificationRecipientCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/NotificationRecipientCreate.vue')
      },
      {
        path: 'credentials',
        name: 'admin-credentials',
        meta: { breadcrumb: 'shell.routes.adminCredentials', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/Credentials.vue')
      },
      {
        path: 'target-match-review',
        name: 'admin-target-match-review',
        meta: { breadcrumb: 'shell.routes.adminTargetMatchReview', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/TargetMatchReview.vue')
      },
      {
        path: 'import-mapping-profiles',
        name: 'admin-import-mapping-profiles',
        meta: { breadcrumb: 'shell.routes.adminImportMappingProfiles', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfiles.vue')
      },
      // D-124 Phase 4: Add/Edit moved off ImportMappingProfiles.vue's own
      // inline form onto its own routed page.
      {
        path: 'import-mapping-profiles/new',
        name: 'admin-import-mapping-profile-create',
        meta: { breadcrumb: 'shell.routes.adminImportMappingProfileCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfileEdit.vue')
      },
      {
        path: 'import-mapping-profiles/:importMappingProfileKey',
        name: 'admin-import-mapping-profile-edit',
        meta: { breadcrumb: 'shell.routes.adminImportMappingProfileEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfileEdit.vue'),
        props: true
      },
      // D-181 (Data Sources phase 2): scheduled CSV data feeds.
      {
        path: 'data-sources',
        name: 'admin-data-sources',
        meta: { breadcrumb: 'shell.routes.adminDataSources', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/DataSources.vue')
      },
      // D-211 (Data Sources phase 3): the CyberArk sources' nightly import settings.
      {
        path: 'data-sources/cyberark',
        name: 'admin-cyberark-sources-edit',
        meta: { breadcrumb: 'shell.routes.adminCyberarkSourcesEdit', breadcrumbParent: 'admin-data-sources' },
        component: () => import('../views/admin/CyberArkSourcesEdit.vue')
      },
      {
        path: 'data-sources/new',
        name: 'admin-data-feed-create',
        meta: { breadcrumb: 'shell.routes.adminDataFeedCreate', breadcrumbParent: 'admin-data-sources' },
        component: () => import('../views/admin/DataFeedEdit.vue')
      },
      {
        path: 'data-sources/:dataFeedKey',
        name: 'admin-data-feed-edit',
        meta: { breadcrumb: 'shell.routes.adminDataFeedEdit', breadcrumbParent: 'admin-data-sources' },
        component: () => import('../views/admin/DataFeedEdit.vue'),
        props: true
      },
      {
        path: 'risk-score-bands',
        name: 'admin-risk-score-bands',
        meta: { breadcrumb: 'shell.routes.adminRiskScoreBands', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBands.vue')
      },
      // D-124 Phase 4: Add/Edit moved off RiskScoreBands.vue's own inline
      // form onto its own routed page.
      {
        path: 'risk-score-bands/new',
        name: 'admin-risk-score-band-create',
        meta: { breadcrumb: 'shell.routes.adminRiskScoreBandCreate', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBandEdit.vue')
      },
      {
        path: 'risk-score-bands/:riskScoreBandKey',
        name: 'admin-risk-score-band-edit',
        meta: { breadcrumb: 'shell.routes.adminRiskScoreBandEdit', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBandEdit.vue'),
        props: true
      },
      // D-233: account classification for Reports > Discovered Accounts.
      {
        path: 'account-classification',
        name: 'admin-account-classification',
        meta: { breadcrumb: 'shell.routes.adminAccountClassification', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/AccountClassification.vue')
      },
      {
        path: 'account-classification/rules/new',
        name: 'admin-account-classification-rule-create',
        meta: { breadcrumb: 'shell.routes.adminAccountClassificationRuleCreate', breadcrumbParent: 'admin-account-classification' },
        component: () => import('../views/admin/AccountClassificationRuleEdit.vue')
      },
      {
        path: 'account-classification/rules/:ruleKey',
        name: 'admin-account-classification-rule-edit',
        meta: { breadcrumb: 'shell.routes.adminAccountClassificationRuleEdit', breadcrumbParent: 'admin-account-classification' },
        component: () => import('../views/admin/AccountClassificationRuleEdit.vue'),
        props: true
      },
      {
        path: 'siem-forwarding',
        name: 'admin-siem-forwarding',
        meta: { breadcrumb: 'shell.routes.adminSiemForwarding', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/SiemForwarding.vue')
      }
    ]
  },
  {
    path: '/access-denied',
    name: 'access-denied',
    meta: { breadcrumb: 'shell.routes.accessDenied' },
    component: () => import('../views/AccessDenied.vue')
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    meta: { breadcrumb: 'shell.routes.notFound' },
    component: () => import('../views/AccessDenied.vue')
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
})

// D-100: Login.vue itself needs no guarding (it's the escape hatch), and
// /api/me's own 401 is the one authoritative "not authenticated" signal --
// an authenticated user with zero permissions still gets a 200 with empty
// roleNames/permissionNames (MeController), so this never fires for that
// case, only for a genuinely unauthenticated request.
router.beforeEach(async (to) => {
  if (to.name === 'login' || to.name === 'login-provider') return true

  const rights = useRightsStore()
  await rights.ensureLoaded()

  if (rights.authenticated === false) {
    return { name: 'login', query: { returnUrl: to.fullPath } }
  }

  return true
})

// D-92: a client-routed SPA gives assistive tech no "page changed" signal
// on its own (no full page load, no new document title announced) --
// moves focus to <main> (App.vue) and announces the new page's own <h1>/<h2>
// text via a visually-hidden live region, once Vue has actually rendered
// the new route's content (nextTick).
router.afterEach(() => {
  nextTick(() => {
    const main = document.getElementById('main-content')
    main?.focus()

    const heading = main?.querySelector('h1, h2')
    const announcer = document.getElementById('route-announcer')
    if (announcer) {
      announcer.textContent = heading?.textContent?.trim() || document.title
    }
  })
})

export default router
