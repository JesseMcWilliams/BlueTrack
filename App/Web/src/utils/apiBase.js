// D-196: where the API lives. In production the API is the nested IIS
// Application /BlueTrack, so the SPA calls /BlueTrack/api/... directly --
// the site-root rewrite from /api/... (D-166) handed Windows-authenticated
// requests to another IIS application and looped at sign-in (401.1). In
// development (Vite's proxy) and in tests the API is at /api, so the base
// is empty. Set by VITE_API_BASE (App/Web/.env.production).
export const API_BASE = (import.meta.env.VITE_API_BASE ?? '').replace(/\/+$/, '')

// Prefixes an /api/... path with API_BASE; anything else is returned as is.
export function apiUrl(path) {
  return typeof path === 'string' && path.startsWith('/api/') ? API_BASE + path : path
}

// Wraps fetch so every fetch('/api/...') in the app reaches the API without
// each call site knowing the base. Request objects and other URLs pass through.
export function installApiFetch(target = globalThis) {
  if (!API_BASE || target.fetch?.apiBaseInstalled) return
  const nativeFetch = target.fetch.bind(target)
  const wrapped = (input, init) => nativeFetch(apiUrl(input), init)
  wrapped.apiBaseInstalled = true
  target.fetch = wrapped
}
