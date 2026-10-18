import { describe, it, expect } from 'vitest'
import { useLatestRequest } from './useLatestRequest'

describe('useLatestRequest', () => {
  it('only the newest request is current', () => {
    const latest = useLatestRequest()
    const first = latest.begin()
    expect(latest.isCurrent(first)).toBe(true)
    const second = latest.begin()
    expect(latest.isCurrent(first)).toBe(false)
    expect(latest.isCurrent(second)).toBe(true)
  })

  it('each caller has its own counter', () => {
    const a = useLatestRequest()
    const b = useLatestRequest()
    const request = a.begin()
    b.begin()
    b.begin()
    expect(a.isCurrent(request)).toBe(true)
  })
})
