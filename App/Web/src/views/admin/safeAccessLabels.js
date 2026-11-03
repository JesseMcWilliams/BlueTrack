// D-300: one line for a Safe access policy rule, e.g. "Required: {SafeName}_Users holds End user, with people in it".
export function describeSafeAccessRule(t, rule) {
  if (rule.ruleType === 'Required') {
    return t(rule.requirePeople ? 'admin.safeAccessPolicies.ruleRequiredPeople' : 'admin.safeAccessPolicies.ruleRequired',
      { pattern: rule.memberPattern, role: rule.safeRoleName ?? '' })
  }
  return rule.safeRoleName
    ? t('admin.safeAccessPolicies.ruleForbiddenRole', { pattern: rule.memberPattern, role: rule.safeRoleName })
    : t('admin.safeAccessPolicies.ruleForbidden', { pattern: rule.memberPattern })
}
