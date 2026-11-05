// D-342, D-344: display names for the DNA columns an admin can restrict (DnaColumns.Restrictable) and the fields a DNA
// tracking rule can test (DnaTrackRuleFields), with literal keys so the localization check (D-227) can see each one.
export function dnaColumnLabels(t) {
  return {
    MachineName: t('admin.dna.col.machineName'),
    MachineType: t('admin.dna.col.machineType'),
    OsVersion: t('admin.dna.col.osVersion'),
    DisplayName: t('admin.dna.col.displayName'),
    AccountDescription: t('admin.dna.col.accountDescription'),
    AccountType: t('admin.dna.col.accountType'),
    AccountCategory: t('admin.dna.col.accountCategory'),
    AccountGroup: t('admin.dna.col.accountGroup'),
    PrivilegedDomainGroup: t('admin.dna.col.privilegedDomainGroup'),
    InstanceName: t('admin.dna.col.instanceName'),
    DatabaseName: t('admin.dna.col.databaseName'),
    Roles: t('admin.dna.col.roles'),
    DbPermissions: t('admin.dna.col.dbPermissions'),
    ComplianceStatus: t('admin.dna.col.complianceStatus'),
    AccountState: t('admin.dna.col.accountState'),
    ThreatCause: t('admin.dna.col.threatCause'),
    PassTheHashVulnerable: t('admin.dna.col.passTheHashVulnerable'),
    PassTheHashHashFound: t('admin.dna.col.passTheHashHashFound'),
    ServiceAccountType: t('admin.dna.col.serviceAccountType'),
    ServiceAccountDescription: t('admin.dna.col.serviceAccountDescription'),
    SpnDescription: t('admin.dna.col.spnDescription'),
    LocalMappedAccount: t('admin.dna.col.localMappedAccount'),
    PasswordNeverExpires: t('admin.dna.col.passwordNeverExpires'),
    PasswordAgeDays: t('admin.dna.col.passwordAgeDays'),
    PasswordLastSet: t('admin.dna.col.passwordLastSet'),
    LastLoginDate: t('admin.dna.col.lastLoginDate'),
    AccountExpirationDate: t('admin.dna.col.accountExpirationDate'),
    Details: t('admin.dna.col.details')
  }
}

export function dnaFieldLabels(t) {
  return {
    Finding: t('admin.dna.field.finding'),
    MachineName: t('admin.dna.col.machineName'),
    MachineType: t('admin.dna.col.machineType'),
    OsVersion: t('admin.dna.col.osVersion'),
    AccountName: t('admin.dna.field.accountName'),
    AccountType: t('admin.dna.col.accountType'),
    AccountCategory: t('admin.dna.col.accountCategory'),
    AccountGroup: t('admin.dna.col.accountGroup'),
    PrivilegedDomainGroup: t('admin.dna.col.privilegedDomainGroup'),
    ComplianceStatus: t('admin.dna.col.complianceStatus'),
    AccountState: t('admin.dna.col.accountState'),
    ThreatCause: t('admin.dna.col.threatCause'),
    ServiceAccountType: t('admin.dna.col.serviceAccountType'),
    DomainAccount: t('admin.dna.field.domainAccount'),
    PasswordNeverExpires: t('admin.dna.col.passwordNeverExpires'),
    PasswordLastSet: t('admin.dna.col.passwordLastSet'),
    LastLogin: t('admin.dna.col.lastLoginDate'),
    UnresolvedSid: t('admin.dna.field.unresolvedSid')
  }
}
