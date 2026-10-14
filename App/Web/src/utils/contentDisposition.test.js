import { describe, it, expect } from 'vitest'
import { fileNameFrom } from './contentDisposition'

// D-220: the export's file name, from the API's Content-Disposition header.
describe('fileNameFrom', () => {
  it('prefers the UTF-8 form', () => {
    expect(fileNameFrom("attachment; filename=plain.csv; filename*=UTF-8''BlueTrack_Risk%20Score.csv")).toBe('BlueTrack_Risk Score.csv')
  })

  it('reads a quoted or bare filename', () => {
    expect(fileNameFrom('attachment; filename="BlueTrack_KpiSummary_20261013-0930.xlsx"')).toBe('BlueTrack_KpiSummary_20261013-0930.xlsx')
    expect(fileNameFrom('attachment; filename=BlueTrack_KpiSummary_20261013-0930.csv')).toBe('BlueTrack_KpiSummary_20261013-0930.csv')
  })

  it('is null without a header or a name', () => {
    expect(fileNameFrom(null)).toBeNull()
    expect(fileNameFrom('attachment')).toBeNull()
  })
})
