// D-337: shared by DataSources.vue, PullDetailsDialog.vue and ImportLoadSection.vue: the names of a pull's steps,
// and how long something has been going, by the server's clock (the API sends serverTime with each list).
import { t } from '../../i18n'
import { formatCount } from '../../utils/formatCount'

// Keys are written out in full (D-227's key coverage check forbids building one at run time).
const pullStepKeys = {
  Credential: 'admin.runProgress.step_Credential',
  Logon: 'admin.runProgress.step_Logon',
  Platforms: 'admin.runProgress.step_Platforms',
  Safes: 'admin.runProgress.step_Safes',
  BuiltInSafes: 'admin.runProgress.step_BuiltInSafes',
  Accounts: 'admin.runProgress.step_Accounts',
  CpmActivity: 'admin.runProgress.step_CpmActivity',
  AccountDetails: 'admin.runProgress.step_AccountDetails',
  SafeMembers: 'admin.runProgress.step_SafeMembers',
  Users: 'admin.runProgress.step_Users',
  Groups: 'admin.runProgress.step_Groups',
  Applications: 'admin.runProgress.step_Applications',
  Logoff: 'admin.runProgress.step_Logoff',
  ChangeCheck: 'admin.runProgress.step_ChangeCheck',
  AdGroups: 'admin.runProgress.step_AdGroups',
  Staging: 'admin.runProgress.step_Staging'
}

export function pullStepLabel(name) {
  const key = pullStepKeys[name]
  return key ? t(key) : name
}

/** "1,200 of 2,454", "1,000 read", or '' for a step that isn't counted. */
export function stepCount(step) {
  if (step.itemsDone === null || step.itemsDone === undefined) return ''
  return step.itemsTotal === null || step.itemsTotal === undefined
    ? t('admin.runProgress.countRead', { done: formatCount(step.itemsDone) })
    : t('admin.runProgress.countOf', { done: formatCount(step.itemsDone), total: formatCount(step.itemsTotal) })
}

/** The API's times are UTC without a zone suffix (SQL datetime2). */
export function toDate(value) {
  if (!value) return null
  const date = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
  return Number.isNaN(date.getTime()) ? null : date
}

/** A length of time in seconds as "45 s", "12 min" or "2 h 5 min". */
export function formatSeconds(seconds) {
  const s = Math.max(0, Math.round(seconds))
  if (s < 60) return t('admin.runProgress.seconds', { n: formatCount(s) })
  const minutes = Math.floor(s / 60)
  if (minutes < 60) return t('admin.runProgress.minutes', { n: formatCount(minutes) })
  return t('admin.runProgress.hoursMinutes', { h: formatCount(Math.floor(minutes / 60)), m: formatCount(minutes % 60) })
}

/** How long from `from` to `to` (or to the server's clock, `serverNow`), formatted; '' when a time is missing. */
export function durationText(from, to, serverNow) {
  const start = toDate(from)
  const end = toDate(to) ?? toDate(serverNow)
  return start && end ? formatSeconds((end - start) / 1000) : ''
}

/** A procedure as dbo.usp_Load_FactAccount shows as usp_Load_FactAccount. */
export function procedureName(name) {
  return (name ?? '').replace(/^dbo\./i, '')
}
