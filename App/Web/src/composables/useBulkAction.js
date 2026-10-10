import { ref } from 'vue'

// D-190: runs one bulk action (a POST of the selected keys plus the action's
// own values) and keeps its outcome for the page: result = { requested,
// changed, skipped: [{ key, name, reason }] }, or error for a refused
// request (400 detail). Afterwards only the skipped items stay selected,
// so they can be looked at or retried.
export function useBulkAction(selection) {
  const saving = ref(false)
  const result = ref(null)
  const error = ref(null)

  async function run(url, body, verb) {
    saving.value = true
    error.value = null
    result.value = null
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ keys: selection.keys, ...body })
      })
      if (!response.ok) {
        const problem = await response.json().catch(() => null)
        error.value = problem?.detail ?? `Failed: ${response.status}`
        return false
      }
      result.value = { ...(await response.json()), verb }
      selection.clear()
      selection.add(result.value.skipped.map(s => s.key))
      return true
    } finally {
      saving.value = false
    }
  }

  return { saving, result, error, run }
}
