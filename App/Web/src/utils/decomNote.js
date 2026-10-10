// D-186: the text behind the "flagged for deletion" marker on the Account
// Progress list and account page -- empty when the account isn't flagged.
export function decomNote(account) {
  if (!account) return ''
  const parts = []
  if (account.decomInFlaggedSafe && account.decomSafeName) parts.push(`Safe ${account.decomSafeName} is flagged for deletion.`)
  if (account.decomNameFlagged) parts.push('The account name is flagged for deletion.')
  if (parts.length && account.decomOtherSafes) parts.push(`Also in: ${account.decomOtherSafes}.`)
  return parts.join(' ')
}
