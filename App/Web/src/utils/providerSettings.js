// Identity Providers edit page: merge a provider's stored settings JSON into
// the page's own (camelCase) fields.
//
// Found 2026-10-09: Database/12_BlueTrack_OidcSamlProviderSeed.sql stores the
// placeholder settings with PascalCase names ("SpEntityId"), while this page
// uses camelCase ("spEntityId"). A plain object spread kept both, so the
// first save wrote every setting twice, and the API -- which reads names
// regardless of case, with the last copy winning -- saw the seed's empty
// copies instead of the values entered here. Names are now matched
// regardless of case, and a filled-in value beats an empty one, so the
// next save writes one clean copy of each setting.

function isFilled(value) {
  return value !== undefined && value !== null && String(value).trim() !== ''
}

/**
 * @param {Record<string, unknown>} defaults the page's fields, with default values
 * @param {string | null | undefined} storedJson the provider's ConfigurationValues
 * @returns {Record<string, unknown>} one entry per setting, named as in `defaults`
 *   (settings the page doesn't know keep their stored name, also deduplicated)
 */
export function mergeProviderSettings(defaults, storedJson) {
  const result = { ...defaults }
  if (!storedJson) return result

  let stored
  try {
    stored = JSON.parse(storedJson)
  } catch {
    return result
  }
  if (!stored || typeof stored !== 'object' || Array.isArray(stored)) return result

  // Each stored name maps to the page's own name for it, else to the first
  // spelling seen. A stored value replaces a default; among duplicate
  // spellings, a filled-in value beats an empty one.
  const nameFor = Object.fromEntries(Object.keys(defaults).map(name => [name.toLowerCase(), name]))
  const fromStore = new Set()
  for (const [name, value] of Object.entries(stored)) {
    const key = name.toLowerCase()
    const target = nameFor[key] ?? (nameFor[key] = name)
    if (!fromStore.has(target) || (isFilled(value) && !isFilled(result[target]))) {
      result[target] = value
      fromStore.add(target)
    }
  }
  return result
}
