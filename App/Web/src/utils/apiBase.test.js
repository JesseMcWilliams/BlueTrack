import { describe, it, expect, vi, afterEach } from 'vitest'

// API_BASE is read when the module loads, so each case loads it afresh.
async function loadWith(base) {
  vi.resetModules()
  if (base === undefined) vi.unstubAllEnvs()
  else vi.stubEnv('VITE_API_BASE', base)
  return import('./apiBase')
}

afterEach(() => {
  vi.unstubAllEnvs()
})

describe('apiBase (D-196)', () => {
  it('leaves /api paths alone when no base is set (development, tests)', async () => {
    const { apiUrl, API_BASE } = await loadWith(undefined)
    expect(API_BASE).toBe('')
    expect(apiUrl('/api/me')).toBe('/api/me')
  })

  it('prefixes only /api/... paths with the production base', async () => {
    const { apiUrl } = await loadWith('/BlueTrack/')
    expect(apiUrl('/api/admin/deployment')).toBe('/BlueTrack/api/admin/deployment')
    expect(apiUrl('/api/auth/saml/login?returnUrl=%2F')).toBe('/BlueTrack/api/auth/saml/login?returnUrl=%2F')
    expect(apiUrl('/apiary')).toBe('/apiary')
    expect(apiUrl('/dashboard')).toBe('/dashboard')
    expect(apiUrl('https://example.com/api/x')).toBe('https://example.com/api/x')
  })

  it('wraps fetch once, rewriting /api/... string URLs and passing everything else through', async () => {
    const { installApiFetch } = await loadWith('/BlueTrack')
    const calls = []
    const target = { fetch: (input, init) => { calls.push([input, init]); return Promise.resolve('ok') } }

    installApiFetch(target)
    installApiFetch(target)
    await target.fetch('/api/me', { method: 'GET' })
    const request = { url: '/api/me' }
    await target.fetch(request)

    expect(calls).toEqual([['/BlueTrack/api/me', { method: 'GET' }], [request, undefined]])
  })

  it('does not touch fetch when no base is set', async () => {
    const { installApiFetch } = await loadWith(undefined)
    const original = () => Promise.resolve('ok')
    const target = { fetch: original }
    installApiFetch(target)
    expect(target.fetch).toBe(original)
  })
})
