import { createRouter, createWebHistory } from 'vue-router'
import { nextTick } from 'vue'
import { useRightsStore } from '../stores/rights'

// Routes mirror the confirmed page inventory (Design_Application_Structure.md,
// D-43), with Admin (D-47) and Reports (D-56) as hub pages with sub-navigation
// rather than flat top-level entries.
//
// Every route (except 'dashboard', the implicit root) carries meta.breadcrumb
// (its human-readable crumb label) and, where its logical parent isn't
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
    meta: { breadcrumb: 'Login' },
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
    meta: { breadcrumb: 'Accounts' },
    component: () => import('../views/AccountProgressList.vue')
  },
  {
    path: '/accounts/:accountKey',
    name: 'account-progress-detail',
    meta: { breadcrumb: 'Account Detail', breadcrumbParent: 'account-progress-list' },
    component: () => import('../views/AccountProgressDetail.vue'),
    props: true
  },
  {
    path: '/exceptions',
    name: 'risk-exceptions-list',
    meta: { breadcrumb: 'Exceptions' },
    component: () => import('../views/RiskExceptionsList.vue')
  },
  {
    path: '/exceptions/new',
    name: 'risk-exception-create',
    meta: { breadcrumb: 'New Exception', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionEdit.vue')
  },
  {
    path: '/exceptions/:exceptionKey',
    name: 'risk-exception-edit',
    meta: { breadcrumb: 'Edit Exception', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionEdit.vue'),
    props: true
  },
  {
    path: '/exceptions/approvals',
    name: 'risk-exceptions-approval-worklist',
    meta: { breadcrumb: 'Approval Worklist', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionsApprovalWorklist.vue')
  },
  {
    path: '/exceptions/overdue',
    name: 'risk-exceptions-overdue-worklist',
    meta: { breadcrumb: 'Overdue Reviews', breadcrumbParent: 'risk-exceptions-list' },
    component: () => import('../views/RiskExceptionsOverdueWorklist.vue')
  },
  // Reports hub with sub-navigation (D-56) -- three confirmed report types.
  {
    path: '/reports',
    name: 'reports',
    meta: { breadcrumb: 'Reports' },
    component: () => import('../views/reports/ReportsHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'reports-overdue-worklist' }
      },
      {
        path: 'overdue',
        name: 'reports-overdue-worklist',
        meta: { breadcrumb: 'Overdue / At-Risk', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/OverdueAtRiskWorklist.vue')
      },
      {
        path: 'stage-status-summary',
        name: 'reports-stage-status-summary',
        meta: { breadcrumb: 'Stage/Status Summary', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/StageStatusFunnelSummary.vue')
      },
      {
        path: 'reconciliation-review',
        name: 'reports-reconciliation-review',
        meta: { breadcrumb: 'Reconciliation Review', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/ReconciliationReviewQueue.vue')
      },
      {
        path: 'unresolved-entitlement-members',
        name: 'reports-unresolved-entitlement-members',
        meta: { breadcrumb: 'Unresolved Entitlement Members', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/UnresolvedEntitlementMembers.vue')
      },
      {
        path: 'risk-score',
        name: 'reports-risk-score',
        meta: { breadcrumb: 'Risk Score', breadcrumbParent: 'reports' },
        component: () => import('../views/reports/RiskScoreReport.vue')
      }
    ]
  },
  {
    path: '/profile',
    name: 'my-profile',
    meta: { breadcrumb: 'My Profile' },
    component: () => import('../views/MyProfile.vue')
  },
  // D-121: promoted out of the Admin hub to their own top-level nav entries
  // (Analyst now holds ManageTargets/ManageAccessGroups too, same as
  // Admin) -- flat top-level routes, no breadcrumbParent, matching the
  // naming convention of other top-level routes like account-progress-list.
  {
    path: '/targets',
    name: 'targets',
    meta: { breadcrumb: 'Targets' },
    component: () => import('../views/Targets.vue')
  },
  // D-124 Phase 4: Add/Edit moved off Targets.vue's own inline form onto
  // its own routed page, mirroring risk-exception-create/-edit's shape.
  {
    path: '/targets/new',
    name: 'target-create',
    meta: { breadcrumb: 'New Target', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetEdit.vue')
  },
  {
    path: '/targets/:targetKey',
    name: 'target-edit',
    meta: { breadcrumb: 'Edit Target', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetEdit.vue'),
    props: true
  },
  // D-124 Phase 5: the 2 always-visible inline "Bulk Import" sections moved
  // off Targets.vue onto this dedicated routed page, reached via a single
  // "Bulk Actions" header link -- consistent with Phase 4's Add/Edit move.
  {
    path: '/targets/bulk-import',
    name: 'targets-bulk-import',
    meta: { breadcrumb: 'Bulk Actions', breadcrumbParent: 'targets' },
    component: () => import('../views/TargetsBulkImport.vue')
  },
  {
    path: '/access-groups',
    name: 'access-groups',
    meta: { breadcrumb: 'Access Groups' },
    component: () => import('../views/AccessGroups.vue')
  },
  // D-124 Phase 4: Add/Edit moved off AccessGroups.vue's own inline form
  // onto its own routed page, mirroring risk-exception-create/-edit's shape.
  {
    path: '/access-groups/new',
    name: 'access-group-create',
    meta: { breadcrumb: 'New Access Group', breadcrumbParent: 'access-groups' },
    component: () => import('../views/AccessGroupEdit.vue')
  },
  {
    path: '/access-groups/:accessGroupKey',
    name: 'access-group-edit',
    meta: { breadcrumb: 'Edit Access Group', breadcrumbParent: 'access-groups' },
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
    meta: { breadcrumb: 'Bulk Actions', breadcrumbParent: 'access-groups' },
    component: () => import('../views/AccessGroupsBulkImport.vue')
  },
  // Admin hub with sub-navigation (D-47) -- one top-nav entry, sections
  // inside gated per-permission at render time, not per-route here.
  {
    path: '/admin',
    name: 'admin',
    meta: { breadcrumb: 'Admin' },
    component: () => import('../views/admin/AdminHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'admin-identity-providers' }
      },
      {
        path: 'identity-providers',
        name: 'admin-identity-providers',
        meta: { breadcrumb: 'Identity Providers', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviders.vue')
      },
      // D-124 Phase 4: Add/Edit moved off IdentityProviders.vue's own
      // inline form onto its own routed page -- breadcrumbParent stays
      // 'admin' (flat), matching every other admin child route's own
      // convention rather than pointing at the specific list page.
      {
        path: 'identity-providers/new',
        name: 'admin-identity-provider-create',
        meta: { breadcrumb: 'New Provider', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviderEdit.vue')
      },
      {
        path: 'identity-providers/:providerKey',
        name: 'admin-identity-provider-edit',
        meta: { breadcrumb: 'Edit Provider', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/IdentityProviderEdit.vue'),
        props: true
      },
      {
        path: 'group-role-mapping',
        name: 'admin-group-role-mapping',
        meta: { breadcrumb: 'Group / Role Mapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GroupRoleMapping.vue')
      },
      // D-124 Phase 4 (partial conversion): only "Add Mapping" moves -- no
      // matching Edit route exists on the underlying page today.
      {
        path: 'group-role-mapping/new',
        name: 'admin-group-role-mapping-create',
        meta: { breadcrumb: 'Add Mapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GroupRoleMappingCreate.vue')
      },
      {
        path: 'roles-permissions',
        name: 'admin-roles-permissions',
        meta: { breadcrumb: 'Roles & Permissions', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RolesAndPermissions.vue')
      },
      // D-124 Phase 4: Add/Edit moved off RolesAndPermissions.vue's own
      // inline form onto its own routed page.
      {
        path: 'roles-permissions/new',
        name: 'admin-role-create',
        meta: { breadcrumb: 'New Role', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RoleEdit.vue')
      },
      {
        path: 'roles-permissions/:appRoleKey',
        name: 'admin-role-edit',
        meta: { breadcrumb: 'Edit Role', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RoleEdit.vue'),
        props: true
      },
      {
        path: 'application-mapping',
        name: 'admin-application-mapping',
        meta: { breadcrumb: 'Application Mapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationSafeMapping.vue')
      },
      // D-124 Phase 4: Add/Edit moved off ApplicationSafeMapping.vue's
      // Applications section onto its own routed page -- the Safes
      // section (a plain per-row <select>, not a form) is untouched.
      {
        path: 'application-mapping/new',
        name: 'admin-application-create',
        meta: { breadcrumb: 'New Application', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationEdit.vue')
      },
      {
        path: 'application-mapping/:applicationKey',
        name: 'admin-application-edit',
        meta: { breadcrumb: 'Edit Application', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationEdit.vue'),
        props: true
      },
      {
        path: 'secrets-store',
        name: 'admin-secrets-store',
        meta: { breadcrumb: 'Secrets Store', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/SecretsStoreConfiguration.vue')
      },
      {
        path: 'field-metadata',
        name: 'admin-field-metadata',
        meta: { breadcrumb: 'Field Metadata', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataManagement.vue')
      },
      // D-124 Phase 4: Add/Edit moved off FieldMetadataManagement.vue's own
      // inline form onto its own routed page.
      {
        path: 'field-metadata/new',
        name: 'admin-field-metadata-create',
        meta: { breadcrumb: 'New Field', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataEdit.vue')
      },
      {
        path: 'field-metadata/:fieldMetadataKey',
        name: 'admin-field-metadata-edit',
        meta: { breadcrumb: 'Edit Field', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/FieldMetadataEdit.vue'),
        props: true
      },
      {
        path: 'audit-log',
        name: 'admin-audit-log',
        meta: { breadcrumb: 'Audit Log', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/AuditLogViewer.vue')
      },
      {
        path: 'configuration',
        name: 'admin-configuration',
        meta: { breadcrumb: 'Configuration', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GlobalApplicationConfiguration.vue')
      },
      {
        path: 'deployment',
        name: 'admin-deployment',
        meta: { breadcrumb: 'Deployment Info', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/DeploymentInfo.vue')
      },
      {
        path: 'notifications',
        name: 'admin-notifications',
        meta: { breadcrumb: 'Notifications', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/Notifications.vue')
      },
      // D-124 Phase 4 (partial conversion): only "Add Recipient" moves --
      // no matching Edit route exists on the underlying page today.
      {
        path: 'notifications/recipients/new',
        name: 'admin-notification-recipient-create',
        meta: { breadcrumb: 'Add Recipient', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/NotificationRecipientCreate.vue')
      },
      {
        path: 'credentials',
        name: 'admin-credentials',
        meta: { breadcrumb: 'Credentials & LDAP', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/Credentials.vue')
      },
      {
        path: 'target-match-review',
        name: 'admin-target-match-review',
        meta: { breadcrumb: 'Target Match Review', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/TargetMatchReview.vue')
      },
      {
        path: 'import-mapping-profiles',
        name: 'admin-import-mapping-profiles',
        meta: { breadcrumb: 'Import Mapping Profiles', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfiles.vue')
      },
      // D-124 Phase 4: Add/Edit moved off ImportMappingProfiles.vue's own
      // inline form onto its own routed page.
      {
        path: 'import-mapping-profiles/new',
        name: 'admin-import-mapping-profile-create',
        meta: { breadcrumb: 'New Profile', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfileEdit.vue')
      },
      {
        path: 'import-mapping-profiles/:importMappingProfileKey',
        name: 'admin-import-mapping-profile-edit',
        meta: { breadcrumb: 'Edit Profile', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ImportMappingProfileEdit.vue'),
        props: true
      },
      {
        path: 'risk-score-bands',
        name: 'admin-risk-score-bands',
        meta: { breadcrumb: 'Risk Score Bands', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBands.vue')
      },
      // D-124 Phase 4: Add/Edit moved off RiskScoreBands.vue's own inline
      // form onto its own routed page.
      {
        path: 'risk-score-bands/new',
        name: 'admin-risk-score-band-create',
        meta: { breadcrumb: 'New Band', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBandEdit.vue')
      },
      {
        path: 'risk-score-bands/:riskScoreBandKey',
        name: 'admin-risk-score-band-edit',
        meta: { breadcrumb: 'Edit Band', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RiskScoreBandEdit.vue'),
        props: true
      }
    ]
  },
  {
    path: '/access-denied',
    name: 'access-denied',
    meta: { breadcrumb: 'Access Denied' },
    component: () => import('../views/AccessDenied.vue')
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    meta: { breadcrumb: 'Not Found' },
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
  if (to.name === 'login') return true

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
