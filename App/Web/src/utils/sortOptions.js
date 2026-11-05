// Drop-down lists whose order has no meaning list their options alphabetically
// by the text the user sees (case-insensitive). Returns a new array; the input
// is not changed, so a constant's first entry can still serve as a default.
// Numbers inside the text sort by value (extensionAttribute2 before extensionAttribute10).
export function byLabel(items, labelOf = item => item) {
  const text = item => String(labelOf(item) ?? '')
  return [...(items ?? [])].sort((a, b) => text(a).localeCompare(text(b), undefined, { sensitivity: 'base', numeric: true }))
}
