// D-283: display names for the Progress Rule fields and operators (ProgressRuleFields.cs), with literal keys so the
// localization check (D-227) can see each one.
export function fieldLabels(t) {
  return {
    Source: t('admin.progressRules.field.source'),
    SafeName: t('admin.progressRules.field.safeName'),
    PlatformId: t('admin.progressRules.field.platformId'),
    PlatformName: t('admin.progressRules.field.platformName'),
    AccountName: t('admin.progressRules.field.accountName'),
    UserName: t('admin.progressRules.field.userName'),
    Address: t('admin.progressRules.field.address'),
    AutoManaged: t('admin.progressRules.field.autoManaged'),
    CPMStatus: t('admin.progressRules.field.cpmStatus'),
    SecretType: t('admin.progressRules.field.secretType'),
    LastVerified: t('admin.progressRules.field.lastVerified'),
    LastReconciled: t('admin.progressRules.field.lastReconciled'),
    LastChanged: t('admin.progressRules.field.lastChanged'),
    LastCPMAction: t('admin.progressRules.field.lastCpmAction'),
    LastCPMActionResult: t('admin.progressRules.field.lastCpmActionResult'),
    LastCPMActionTime: t('admin.progressRules.field.lastCpmActionTime'),
    DeviceType: t('admin.progressRules.field.deviceType'),
    CreationMethod: t('admin.progressRules.field.creationMethod'),
    AccountType: t('admin.progressRules.field.accountType'),
    SourceOfRecord: t('admin.progressRules.field.sourceOfRecord'),
    RiskLevel: t('admin.progressRules.field.riskLevel'),
    Status: t('admin.progressRules.field.status'),
    OwnerName: t('admin.progressRules.field.ownerName'),
    BusinessUnit: t('admin.progressRules.field.businessUnit'),
    Application: t('admin.progressRules.field.application')
  }
}

export function operatorLabels(t) {
  return {
    Equals: t('admin.progressRules.operator.equals'),
    NotEquals: t('admin.progressRules.operator.notEquals'),
    Contains: t('admin.progressRules.operator.contains'),
    NotContains: t('admin.progressRules.operator.notContains'),
    StartsWith: t('admin.progressRules.operator.startsWith'),
    In: t('admin.progressRules.operator.in'),
    NotIn: t('admin.progressRules.operator.notIn'),
    IsEmpty: t('admin.progressRules.operator.isEmpty'),
    IsNotEmpty: t('admin.progressRules.operator.isNotEmpty'),
    WithinDays: t('admin.progressRules.operator.withinDays'),
    OlderThanDays: t('admin.progressRules.operator.olderThanDays')
  }
}

export const NO_VALUE = ['IsEmpty', 'IsNotEmpty']

// One line for a condition, e.g. "CPM status is success".
export function describeCondition(t, condition) {
  const field = fieldLabels(t)[condition.fieldName] ?? condition.fieldName
  const op = operatorLabels(t)[condition.operator] ?? condition.operator
  return NO_VALUE.includes(condition.operator) ? `${field} ${op}` : `${field} ${op} ${condition.value}`
}
