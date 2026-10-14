// D-181: shared by DataSources.vue and DataFeedEdit.vue.
import { formatDateTime } from '../../utils/formatLocale'
import { t } from '../../i18n'

const feedTypeLabelKeys = {
  TargetInventory: 'admin.dataFeedFormat.targetInventory',
  AccessGroupInventory: 'admin.dataFeedFormat.accessGroupInventory',
  AccessGroupTargetMap: 'admin.dataFeedFormat.accessGroupTargetMap',
  AccountAccessGroupMembership: 'admin.dataFeedFormat.accountAccessGroupMembership',
  AccountTargetMap: 'admin.dataFeedFormat.accountTargetMap',
  Applications: 'admin.dataFeedFormat.applications',
  SafeAssignments: 'admin.dataFeedFormat.safeAssignments',
  AccountInventory: 'admin.dataFeedFormat.accountInventory' // D-212
}

export function feedTypeLabel(feedType) {
  const key = feedTypeLabelKeys[feedType]
  return key ? t(key) : feedType
}

/**
 * Run times come from the API as UTC without a zone suffix (SQL datetime2);
 * the next scheduled run is already server local time (isLocal = true).
 */
export function formatRunTime(value, isLocal = false) {
  if (!value) return ''
  const text = isLocal || /[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`
  const date = new Date(text)
  return Number.isNaN(date.getTime()) ? value : formatDateTime(date)
}
