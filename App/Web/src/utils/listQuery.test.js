import { describe, it, expect } from 'vitest'
import { readText, readOneOf, readInt, readPage, readDate, parseSort, formatSort, compactQuery, sameQuery } from './listQuery'

// UI review W2-2, W7-2: reading and writing list state in the page address.
describe('reading the address', () => {
  it('readText gives the first value, and the fallback for a missing key or an odd value', () => {
    expect(readText({ stage: 'Discovery' }, 'stage')).toBe('Discovery')
    expect(readText({ stage: ['A', 'B'] }, 'stage')).toBe('A')
    expect(readText({}, 'stage')).toBe('')
    expect(readText({}, 'stage', 'x')).toBe('x')
    expect(readText({ stage: null }, 'stage')).toBe('')
    expect(readText(undefined, 'stage')).toBe('')
  })

  it('readOneOf only lets a known value through', () => {
    expect(readOneOf({ deleted: 'Only' }, 'deleted', ['Hide', 'Show', 'Only'], 'Hide')).toBe('Only')
    expect(readOneOf({ deleted: 'Everything' }, 'deleted', ['Hide', 'Show', 'Only'], 'Hide')).toBe('Hide')
    expect(readOneOf({}, 'deleted', ['Hide', 'Show', 'Only'], 'Hide')).toBe('Hide')
  })

  it.each([
    ['3', 3], ['1', 1], ['0', 1], ['-2', 1], ['2.5', 1], ['abc', 1], ['', 1], ['99999999999', 1]
  ])('readPage(%j) is %i', (text, expected) => {
    expect(readPage({ page: text })).toBe(expected)
  })

  it('readInt honours a minimum and a fallback', () => {
    expect(readInt({ days: '0' }, 'days', 30, 0)).toBe(0)
    expect(readInt({ days: '0' }, 'days', 30, 1)).toBe(30)
    expect(readInt({}, 'days', 30)).toBe(30)
  })

  it('readDate accepts only YYYY-MM-DD dates', () => {
    expect(readDate({ from: '2026-10-19' }, 'from')).toBe('2026-10-19')
    expect(readDate({ from: '2026-13-45' }, 'from', 'x')).toBe('x')
    expect(readDate({ from: '10/19/2026' }, 'from')).toBe('')
    expect(readDate({}, 'from', '2026-01-01')).toBe('2026-01-01')
  })
})

describe('sort', () => {
  const allowed = ['userName', 'stageName', 'address']

  it('parses the API form, keeping only known fields, once each', () => {
    expect(parseSort('stageName:desc,userName:asc,bogus:asc,stageName:asc', allowed)).toEqual([
      { field: 'stageName', descending: true },
      { field: 'userName', descending: false }
    ])
  })

  it('takes a missing or odd direction as ascending', () => {
    expect(parseSort('userName,address:sideways', allowed)).toEqual([
      { field: 'userName', descending: false }, { field: 'address', descending: false }
    ])
  })

  it('gives nothing for nothing', () => {
    expect(parseSort('', allowed)).toEqual([])
    expect(parseSort(undefined, allowed)).toEqual([])
  })

  it('formats what it parses', () => {
    const columns = [{ field: 'stageName', descending: true }, { field: 'userName', descending: false }]
    expect(formatSort(columns)).toBe('stageName:desc,userName:asc')
    expect(parseSort(formatSort(columns), allowed)).toEqual(columns)
    expect(formatSort([])).toBe('')
  })
})

describe('compactQuery', () => {
  it('leaves out empty values and the defaults, and turns the rest into text', () => {
    expect(compactQuery(
      { stage: 'Discovery', status: '', owner: null, deleted: 'Hide', linked: 'Show', page: 1, sort: 'a:asc' },
      { deleted: 'Hide', linked: 'Hide', page: 1 }
    )).toEqual({ stage: 'Discovery', linked: 'Show', sort: 'a:asc' })
  })

  it('keeps a page after the first, as text', () => {
    expect(compactQuery({ page: 3 }, { page: 1 })).toEqual({ page: '3' })
  })

  it('gives an empty address for an untouched page', () => {
    expect(compactQuery({ stage: '', page: 1 }, { page: 1 })).toEqual({})
  })
})

describe('sameQuery', () => {
  it('ignores key order, a one-item array and empty values', () => {
    expect(sameQuery({ a: '1', b: '2' }, { b: '2', a: '1' })).toBe(true)
    expect(sameQuery({ a: ['1'] }, { a: '1' })).toBe(true)
    expect(sameQuery({ a: '1', b: '' }, { a: '1' })).toBe(true)
    expect(sameQuery({}, undefined)).toBe(true)
  })

  it('tells a real difference', () => {
    expect(sameQuery({ a: '1' }, { a: '2' })).toBe(false)
    expect(sameQuery({ a: '1' }, {})).toBe(false)
    expect(sameQuery({ a: '1' }, { a: '1', b: '2' })).toBe(false)
  })
})
