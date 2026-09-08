// Backend DATE columns serialize as a full ISO datetime string with a
// midnight time component (e.g. "2026-09-06T00:00:00"), and DATETIME2
// columns (MatchedDate) serialize with fractional seconds
// (e.g. "2026-09-06T06:06:00.2898287"). Interpolating that raw into a
// table cell shows precision no reviewer needs and forces the column
// wider than the date itself requires, squeezing whatever's next to it
// (found 2026-09-06 reviewing table column-width usability alongside
// D-110's spacing pass). Every list/detail view should format a date
// field through this rather than interpolating the raw value directly.
export function formatDate(value) {
  if (!value) return ''
  return String(value).slice(0, 10)
}
