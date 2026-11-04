import { describe, it, expect } from 'vitest'
import { accessRedirect, canOpenReports, canOpenAdmin, ROUTE_PERMISSIONS, ADMIN_SECTIONS, REPORT_PAGES } from './routePermissions'
import router from './index'

function rights(permissionNames, extra = {}) {
  return {
    loaded: true,
    error: null,
    authenticated: true,
    permissionNames,
    hasPermission: (name) => permissionNames.includes(name),
    ...extra
  }
}

describe('accessRedirect', () => {
  it('sends a user with no permissions to the no-access page from any page but the open ones', () => {
    expect(accessRedirect(rights([]), { name: 'dashboard' })).toEqual({ name: 'no-access' })
    expect(accessRedirect(rights([]), { name: 'targets' })).toEqual({ name: 'no-access' })
    expect(accessRedirect(rights([]), { name: 'no-access' })).toBeNull()
    expect(accessRedirect(rights([]), { name: 'my-profile' })).toBeNull()
    expect(accessRedirect(rights([]), { name: 'access-denied' })).toBeNull()
    expect(accessRedirect(rights([]), { name: 'login' })).toBeNull()
  })

  // D-307: Home is where everyone with a role starts.
  it('sends a user who has permissions away from the no-access page, to Home', () => {
    expect(accessRedirect(rights(['ViewDashboard']), { name: 'no-access' })).toEqual({ name: 'home' })
    expect(accessRedirect(rights(['ViewAuditLog']), { name: 'no-access' })).toEqual({ name: 'home' })
  })

  it('lets anyone with a role open Home, and sends one without ViewDashboard to Access Denied from the Dashboard', () => {
    // The seeded Auditor role holds only ViewAuditLog.
    expect(accessRedirect(rights(['ViewAuditLog']), { name: 'home' })).toBeNull()
    expect(accessRedirect(rights(['EditAccountProgress']), { name: 'home' })).toBeNull()
    expect(accessRedirect(rights(['ViewDashboard']), { name: 'dashboard' })).toBeNull()
    expect(accessRedirect(rights(['ViewAuditLog']), { name: 'dashboard' })).toEqual({ name: 'access-denied' })
  })

  it('says whether the Reports and Admin entries have anything for the user', () => {
    expect(canOpenReports(rights(['ViewRiskReport']))).toBe(true)
    expect(canOpenReports(rights(['ViewAuditLog']))).toBe(false)
    expect(canOpenAdmin(rights(['ViewAuditLog']))).toBe(true)
    expect(canOpenAdmin(rights(['ViewDashboard']))).toBe(false)
  })

  it('opens the first report the user can see from the Reports entry', () => {
    const to = { name: 'reports-overdue-worklist', redirectedFrom: { path: '/reports' } }
    expect(accessRedirect(rights(['ViewDashboard']), to)).toBeNull()
    expect(accessRedirect(rights(['ViewRiskReport']), to)).toEqual({ name: 'reports-risk-score' })
    expect(accessRedirect(rights(['ViewAuditLog']), to)).toEqual({ name: 'access-denied' })
  })

  it('sends a user to Access Denied for a page their permissions do not cover', () => {
    expect(accessRedirect(rights(['ManageTargets']), { name: 'access-groups' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ManageTargets']), { name: 'admin-api-key-edit' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ManageTargets']), { name: 'reports-risk-score' })).toEqual({ name: 'access-denied' })
  })

  it('lets a user through to a page they hold the permission for, and to pages that need none', () => {
    expect(accessRedirect(rights(['ManageTargets']), { name: 'targets' })).toBeNull()
    expect(accessRedirect(rights(['ManageApiKeys']), { name: 'admin-api-key-create' })).toBeNull()
    expect(accessRedirect(rights(['ManageTargets']), { name: 'reports' })).toBeNull()
    expect(accessRedirect(rights(['ManageTargets']), { name: 'not-found' })).toBeNull()
  })

  // D-304: the tracked-data pages need the permission their data needs, so they say Access Denied, not "Request failed: 403".
  it('sends a user without ViewDashboard to Access Denied from the tracked-data pages', () => {
    for (const name of ['dashboard', 'account-progress-list', 'account-progress-detail', 'risk-exceptions-list', 'reports-kpi-summary', 'reports-safe-access']) {
      expect(accessRedirect(rights(['ManageTargets']), { name })).toEqual({ name: 'access-denied' })
      expect(accessRedirect(rights(['ViewDashboard']), { name })).toBeNull()
    }
    expect(accessRedirect(rights(['ViewDashboard']), { name: 'account-progress-bulk-edit' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ViewDashboard']), { name: 'risk-exception-create' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ViewDashboard', 'ApproveExceptions']), { name: 'risk-exceptions-approval-worklist' })).toBeNull()
  })

  it('opens the first Admin section the user can see when the first one is not theirs', () => {
    const to = { name: 'admin-identity-providers', redirectedFrom: { path: '/admin' } }
    expect(accessRedirect(rights(['ManageApiKeys', 'ViewAuditLog']), to)).toEqual({ name: 'admin-audit-log' })
    expect(accessRedirect(rights(['ManageTargets']), to)).toEqual({ name: 'admin-target-match-review' })
    expect(accessRedirect(rights(['ManageIdentityProviders']), to)).toBeNull()
  })

  it('does nothing before rights load, on a load error, or when not signed in', () => {
    expect(accessRedirect(rights([], { loaded: false }), { name: 'dashboard' })).toBeNull()
    expect(accessRedirect(rights([], { error: 'Request failed: 500' }), { name: 'dashboard' })).toBeNull()
    expect(accessRedirect(rights([], { authenticated: false }), { name: 'dashboard' })).toBeNull()
  })
})

describe('route permission map', () => {
  const routeNames = new Set(router.getRoutes().map(r => r.name).filter(Boolean))

  it('names only routes that exist', () => {
    for (const name of Object.keys(ROUTE_PERMISSIONS)) expect(routeNames.has(name), name).toBe(true)
  })

  it('covers every admin page, so a new one is not left open by accident', () => {
    const covered = new Set(Object.keys(ROUTE_PERMISSIONS))
    const missing = [...routeNames].filter(n => n.startsWith('admin-') && !covered.has(n))
    expect(missing).toEqual([])
    expect(ADMIN_SECTIONS.length).toBeGreaterThan(0)
  })

  it('lists every report for the Reports entry', () => {
    expect(REPORT_PAGES.filter(n => !routeNames.has(n))).toEqual([])
    expect([...routeNames].filter(n => n.startsWith('reports-') && !REPORT_PAGES.includes(n))).toEqual([])
  })
})
