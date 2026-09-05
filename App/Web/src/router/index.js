import { createRouter, createWebHistory } from 'vue-router'
import { nextTick } from 'vue'

// Routes mirror the confirmed page inventory (Design_Application_Structure.md,
// D-43), with Admin (D-47) and Reports (D-56) as hub pages with sub-navigation
// rather than flat top-level entries. View components are placeholders --
// this is routing/navigation scaffolding, not built-out screens.

const routes = [
  {
    path: '/login',
    name: 'login',
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
    component: () => import('../views/AccountProgressList.vue')
  },
  {
    path: '/accounts/:accountKey',
    name: 'account-progress-detail',
    component: () => import('../views/AccountProgressDetail.vue'),
    props: true
  },
  {
    path: '/exceptions',
    name: 'risk-exceptions-list',
    component: () => import('../views/RiskExceptionsList.vue')
  },
  {
    path: '/exceptions/new',
    name: 'risk-exception-create',
    component: () => import('../views/RiskExceptionEdit.vue')
  },
  {
    path: '/exceptions/:exceptionKey',
    name: 'risk-exception-edit',
    component: () => import('../views/RiskExceptionEdit.vue'),
    props: true
  },
  {
    path: '/exceptions/approvals',
    name: 'risk-exceptions-approval-worklist',
    component: () => import('../views/RiskExceptionsApprovalWorklist.vue')
  },
  {
    path: '/exceptions/overdue',
    name: 'risk-exceptions-overdue-worklist',
    component: () => import('../views/RiskExceptionsOverdueWorklist.vue')
  },
  // Reports hub with sub-navigation (D-56) -- three confirmed report types.
  {
    path: '/reports',
    name: 'reports',
    component: () => import('../views/reports/ReportsHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'reports-overdue-worklist' }
      },
      {
        path: 'overdue',
        name: 'reports-overdue-worklist',
        component: () => import('../views/reports/OverdueAtRiskWorklist.vue')
      },
      {
        path: 'stage-status-summary',
        name: 'reports-stage-status-summary',
        component: () => import('../views/reports/StageStatusFunnelSummary.vue')
      },
      {
        path: 'reconciliation-review',
        name: 'reports-reconciliation-review',
        component: () => import('../views/reports/ReconciliationReviewQueue.vue')
      }
    ]
  },
  {
    path: '/profile',
    name: 'my-profile',
    component: () => import('../views/MyProfile.vue')
  },
  // Admin hub with sub-navigation (D-47) -- one top-nav entry, sections
  // inside gated per-permission at render time, not per-route here.
  {
    path: '/admin',
    name: 'admin',
    component: () => import('../views/admin/AdminHub.vue'),
    children: [
      {
        path: '',
        redirect: { name: 'admin-identity-providers' }
      },
      {
        path: 'identity-providers',
        name: 'admin-identity-providers',
        component: () => import('../views/admin/IdentityProviders.vue')
      },
      {
        path: 'group-role-mapping',
        name: 'admin-group-role-mapping',
        component: () => import('../views/admin/GroupRoleMapping.vue')
      },
      {
        path: 'roles-permissions',
        name: 'admin-roles-permissions',
        component: () => import('../views/admin/RolesAndPermissions.vue')
      },
      {
        path: 'application-mapping',
        name: 'admin-application-mapping',
        component: () => import('../views/admin/ApplicationSafeMapping.vue')
      },
      {
        path: 'secrets-store',
        name: 'admin-secrets-store',
        component: () => import('../views/admin/SecretsStoreConfiguration.vue')
      },
      {
        path: 'field-metadata',
        name: 'admin-field-metadata',
        component: () => import('../views/admin/FieldMetadataManagement.vue')
      },
      {
        path: 'audit-log',
        name: 'admin-audit-log',
        component: () => import('../views/admin/AuditLogViewer.vue')
      },
      {
        path: 'configuration',
        name: 'admin-configuration',
        component: () => import('../views/admin/GlobalApplicationConfiguration.vue')
      }
    ]
  },
  {
    path: '/access-denied',
    name: 'access-denied',
    component: () => import('../views/AccessDenied.vue')
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('../views/AccessDenied.vue')
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
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
