import { describe, it, expect, vi, afterEach } from 'vitest'

// API_BASE is read when the module loads, so each case loads it afresh.
async function loadWith(base) {
  vi.resetModules()
  if (base === undefined) vi.unstubAllEnvs()
  else vi.stubEnv('VITE_API_BASE', base)
  return import('./apiBase')
}

function fakeTarget() {
  const calls = []
  return { calls, fetch: (input, init) => { calls.push([input, init]); return Promise.resolve('ok') } }
}

afterEach(() => {
  vi.unstubAllEnvs()
})

describe('apiBase (D-196, D-197)', () => {
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

  it('wraps fetch once: /api/... calls get the base and the X-Requested-With header, keeping their own options', async () => {
    const { installApiFetch } = await loadWith('/BlueTrack')
    const target = fakeTarget()

    installApiFetch(target)
    installApiFetch(target)
    await target.fetch('/api/me', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })

    expect(target.calls).toHaveLength(1)
    const [url, init] = target.calls[0]
    expect(url).toBe('/BlueTrack/api/me')
    expect(init.method).toBe('POST')
    expect(init.body).toBe('{}')
    expect(init.headers.get('X-Requested-With')).toBe('XMLHttpRequest')
    expect(init.headers.get('Content-Type')).toBe('application/json')
  })

  it('adds the header in development too, and passes everything that is not an /api/... string through untouched', async () => {
    const { installApiFetch } = await loadWith(undefined)
    const target = fakeTarget()
    installApiFetch(target)

    await target.fetch('/api/me')
    const request = { url: '/api/me' }
    await target.fetch(request)
    await target.fetch('https://example.com/api/x', { method: 'POST' })

    expect(target.calls[0][0]).toBe('/api/me')
    expect(target.calls[0][1].headers.get('X-Requested-With')).toBe('XMLHttpRequest')
    expect(target.calls[1]).toEqual([request, undefined])
    expect(target.calls[2]).toEqual(['https://example.com/api/x', { method: 'POST' }])
  })
})
