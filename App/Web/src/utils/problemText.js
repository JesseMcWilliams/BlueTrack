// UI review theme 1: a failed request should tell the person why, not only "save failed: 400". The API answers a
// refusal as a ProblemDetails (`detail`), sometimes with a `message` or `error` of its own (older controllers), and a
// model-validation failure as `errors: { Field: ['text'] }`. This reads whichever is there, and falls back to the
// caller's own text (usually the status code) when the body has none or isn't JSON.

/** The reason in an already-parsed refusal body, or null. */
export function problemReason(problem) {
  if (!problem || typeof problem !== 'object') return null
  for (const key of ['detail', 'message', 'error']) {
    const value = problem[key]
    if (typeof value === 'string' && value.trim()) return value.trim()
  }
  if (problem.errors && typeof problem.errors === 'object') {
    for (const messages of Object.values(problem.errors)) {
      const first = Array.isArray(messages) ? messages.find(m => typeof m === 'string' && m.trim()) : typeof messages === 'string' ? messages : null
      if (first) return first.trim()
    }
  }
  return null
}

/**
 * The reason a refused response gave, or `fallback`. Reads the body once, so call it only for a response that is not
 * going to be read again.
 */
export async function problemMessage(response, fallback) {
  const problem = await response.json().catch(() => null)
  return problemReason(problem) ?? fallback
}
