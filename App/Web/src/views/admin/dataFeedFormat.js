// D-181: shared by DataSources.vue and DataFeedEdit.vue.

const feedTypeLabels = {
  TargetInventory: 'Target inventory',
  AccessGroupInventory: 'Access group inventory',
  AccessGroupTargetMap: 'Access group → target map',
  AccountAccessGroupMembership: 'Account → access group membership',
  AccountTargetMap: 'Account → target map',
  Applications: 'Applications',
  SafeAssignments: 'Safe → application assignments'
}

export function feedTypeLabel(feedType) {
  return feedTypeLabels[feedType] ?? feedType
}

/**
 * Run times come from the API as UTC without a zone suffix (SQL datetime2);
 * the next scheduled run is already server local time (isLocal = true).
 */
export function formatRunTime(value, isLocal = false) {
  if (!value) return ''
  const text = isLocal || /[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`
  const date = new Date(text)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}
