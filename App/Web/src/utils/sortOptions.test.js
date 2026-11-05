import { describe, it, expect } from 'vitest'
import { byLabel } from './sortOptions'

describe('byLabel', () => {
  it('sorts strings alphabetically, ignoring case', () => {
    expect(byLabel(['beta', 'Alpha', 'gamma', 'Delta'])).toEqual(['Alpha', 'beta', 'Delta', 'gamma'])
  })

  it('sorts objects by the label function', () => {
    const items = [{ k: 1, name: 'Zulu' }, { k: 2, name: 'alpha' }, { k: 3, name: 'Mike' }]
    expect(byLabel(items, i => i.name).map(i => i.k)).toEqual([2, 3, 1])
  })

  it('sorts numbers inside the text by value', () => {
    expect(byLabel(['extensionAttribute10', 'extensionAttribute2', 'adminCount'])).toEqual(['adminCount', 'extensionAttribute2', 'extensionAttribute10'])
  })

  it('returns a new array and leaves the input unchanged', () => {
    const input = ['b', 'a']
    const result = byLabel(input)
    expect(result).toEqual(['a', 'b'])
    expect(input).toEqual(['b', 'a'])
    expect(result).not.toBe(input)
  })

  it('treats null or undefined input as empty, and missing labels as blank', () => {
    expect(byLabel(null)).toEqual([])
    expect(byLabel(undefined)).toEqual([])
    expect(byLabel([{ n: 'b' }, {}], i => i.n)).toEqual([{}, { n: 'b' }])
  })
})
