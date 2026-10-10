// D-196: where the API lives. In production the API is the nested IIS
// Application /BlueTrack, so the SPA calls /BlueTrack/api/... directly --
// the site-root rewrite from /api/... (D-166) handed Windows-authenticated
// requests to another IIS application and looped at sign-in (401.1). In
// development (Vite's proxy) and in tests the API is at /api, so the base
// is empty. Set by VITE_API_BASE (App/Web/.env.production).
export const API_BASE = (import.meta.env.VITE_API_BASE ?? '').replace(/\/+$/, '')

// D-197: the API refuses POST/PUT/PATCH/DELETE without this header (its
// CSRF defence, RequireApiRequestHeaderMiddleware); another site's form
// can't send it.
export const API_REQUEST_HEADER = ['X-Requested-With', 'XMLHttpRequest']

// Prefixes an /api/... path with API_BASE; anything else is returned as is.
export function apiUrl(path) {
  return typeof path === 'string' && path.startsWith('/api/') ? API_BASE + path : path
}

function withApiHeader(init) {
  const headers = new Headers(init?.headers)
  if (!headers.has(API_REQUEST_HEADER[0])) headers.set(...API_REQUEST_HEADER)
  return { ...init, headers }
}

// Wraps fetch so every fetch('/api/...') in the app reaches the API (at the
// base, with the header) without each call site knowing. Request objects
// and other URLs pass through untouched, so nothing goes to another origin.
export function installApiFetch(target = globalThis) {
  if (target.fetch?.apiBaseInstalled) return
  const nativeFetch = target.fetch.bind(target)
  const wrapped = (input, init) =>
    typeof input === 'string' && input.startsWith('/api/')
      ? nativeFetch(apiUrl(input), withApiHeader(init))
      : nativeFetch(input, init)
  wrapped.apiBaseInstalled = true
  target.fetch = wrapped
}
