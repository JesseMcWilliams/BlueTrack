import { describe, it, expect } from 'vitest'
import { decomNote } from './decomNote'

describe('decomNote (D-186)', () => {
  it('is empty for an account that is not flagged', () => {
    expect(decomNote({ decomInFlaggedSafe: null, decomNameFlagged: null })).toBe('')
    expect(decomNote(null)).toBe('')
  })

  it('names the flagged safe and any other safe holding the account', () => {
    expect(decomNote({ decomInFlaggedSafe: true, decomSafeName: 'DEL_Finance', decomNameFlagged: false, decomOtherSafes: 'Finance_New' }))
      .toBe('Safe DEL_Finance is flagged for deletion. Also in: Finance_New.')
  })

  it('reports an account flagged by its own name', () => {
    expect(decomNote({ decomInFlaggedSafe: false, decomSafeName: 'Finance', decomNameFlagged: true, decomOtherSafes: null }))
      .toBe('The account name is flagged for deletion.')
  })
})
