// Backend DATE columns serialize as a full ISO datetime string with a
// midnight time component (e.g. "2026-09-06T00:00:00"), and DATETIME2
// columns (MatchedDate) serialize with fractional seconds
// (e.g. "2026-09-06T06:06:00.2898287"). Interpolating that raw into a
// table cell shows precision no reviewer needs and forces the column
// wider than the date itself requires, squeezing whatever's next to it
// (found 2026-09-06 reviewing table column-width usability alongside
// D-110's spacing pass). Every list/detail view should format a date
// field through this rather than interpolating the raw value directly.
//
// D-294: the date part only, in the viewer's date format (formatLocale.js:
// 10/21/2026, 21/10/2026, 21.10.2026 ...). It is formatted as the calendar
// date written in the value, in UTC, so no time zone moves it to another day.
import { dateTimeFormat } from './formatLocale'

export function formatDate(value) {
  if (!value) return ''
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value))
  if (!match) return String(value)
  const [, year, month, day] = match
  return dateTimeFormat({ year: 'numeric', month: '2-digit', day: '2-digit', timeZone: 'UTC' })
    .format(new Date(Date.UTC(Number(year), Number(month) - 1, Number(day))))
}
