import { describe, it, expect } from 'vitest'
import { accessRedirect, ROUTE_PERMISSIONS, ADMIN_SECTIONS } from './routePermissions'
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

  it('sends a user who has permissions away from the no-access page', () => {
    expect(accessRedirect(rights(['ViewAccounts']), { name: 'no-access' })).toEqual({ name: 'dashboard' })
  })

  it('sends a user to Access Denied for a page their permissions do not cover', () => {
    expect(accessRedirect(rights(['ManageTargets']), { name: 'access-groups' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ManageTargets']), { name: 'admin-api-key-edit' })).toEqual({ name: 'access-denied' })
    expect(accessRedirect(rights(['ManageTargets']), { name: 'reports-risk-score' })).toEqual({ name: 'access-denied' })
  })

  it('lets a user through to a page they hold the permission for, and to pages that need none', () => {
    expect(accessRedirect(rights(['ManageTargets']), { name: 'targets' })).toBeNull()
    expect(accessRedirect(rights(['ManageApiKeys']), { name: 'admin-api-key-create' })).toBeNull()
    expect(accessRedirect(rights(['ManageTargets']), { name: 'account-progress-list' })).toBeNull()
    expect(accessRedirect(rights(['ManageTargets']), { name: 'not-found' })).toBeNull()
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
})
