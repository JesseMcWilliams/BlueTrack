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
      }
    ]
  },
  {
    path: '/profile',
    name: 'my-profile',
    meta: { breadcrumb: 'My Profile' },
    component: () => import('../views/MyProfile.vue')
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
      {
        path: 'group-role-mapping',
        name: 'admin-group-role-mapping',
        meta: { breadcrumb: 'Group / Role Mapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/GroupRoleMapping.vue')
      },
      {
        path: 'roles-permissions',
        name: 'admin-roles-permissions',
        meta: { breadcrumb: 'Roles & Permissions', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/RolesAndPermissions.vue')
      },
      {
        path: 'application-mapping',
        name: 'admin-application-mapping',
        meta: { breadcrumb: 'Application Mapping', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/ApplicationSafeMapping.vue')
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
      {
        path: 'credentials',
        name: 'admin-credentials',
        meta: { breadcrumb: 'Credentials & LDAP', breadcrumbParent: 'admin' },
        component: () => import('../views/admin/Credentials.vue')
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
